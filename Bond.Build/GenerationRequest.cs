using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bond.Parser.CodeGeneration;
using Microsoft.Build.Framework;

namespace BondTools.Build;

internal sealed record BuildInput(
    string Source,
    string Output,
    string[] ImportDirectories,
    CSharpGenerationOptions Options);

internal sealed record BuildRequest(string ProjectFile, string OutputDirectory, BuildInput[] Inputs)
{
    internal static BuildRequest Create(string projectFile, string outputDirectory, ITaskItem[] sources,
        ITaskItem[] importDirectories, ITaskItem[] usings, ITaskItem[] namespaceMappings, ITaskItem[] typeMappings)
    {
        var project = BuildFiles.FullPath(projectFile);
        var projectDirectory = Path.GetDirectoryName(project)!;
        outputDirectory = Path.TrimEndingDirectorySeparator(BuildFiles.FullPath(outputDirectory, projectDirectory));
        if (outputDirectory == Path.GetPathRoot(outputDirectory) || BuildFiles.PathComparer.Equals(outputDirectory, projectDirectory))
            throw new InvalidDataException("Bond outputs must use a dedicated intermediate directory.");

        var inputs = new List<BuildInput>();
        var sourcePaths = new HashSet<string>(BuildFiles.PathComparer);
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sources)
        {
            var source = BuildFiles.FullPath(item.ItemSpec, projectDirectory);
            if (!string.Equals(Path.GetExtension(source), ".bond", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(Path.GetFileNameWithoutExtension(source)))
                throw new InvalidDataException($"Input must be a named .bond file: {source}");
            if (!sourcePaths.Add(source))
                throw new InvalidDataException($"Bond input is included more than once: {source}");
            var output = OutputFor(project, source);
            if (!outputs.Add(output))
                throw new InvalidDataException($"Bond output paths collide (case-insensitive): {output}");

            var features = CSharpModelFeatures.None;
            foreach (var (name, flag) in new[]
            {
                ("Descriptors", CSharpModelFeatures.Descriptors),
                ("Clone", CSharpModelFeatures.Cloning),
                ("Equality", CSharpModelFeatures.Equality),
                ("Debugger", CSharpModelFeatures.Debugger)
            })
            {
                var value = item.GetMetadata(name);
                if (!bool.TryParse(value, out var enabled))
                    throw new InvalidDataException($"Bond {name} for '{source}' must be true or false, not '{value}'.");
                if (enabled)
                    features |= flag;
            }
            string[] Combined(string name, ITaskItem[] shared) =>
                shared.Select(option => option.ItemSpec.Trim())
                    .Concat(item.GetMetadata(name).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToArray();
            inputs.Add(new BuildInput(source, output,
                Combined("ImportDirectories", importDirectories).Select(value => BuildFiles.FullPath(value, projectDirectory)).ToArray(),
                new CSharpGenerationOptions
                {
                    UsingNamespaces = Combined("Usings", usings),
                    NamespaceMappings = Combined("NamespaceMappings", namespaceMappings),
                    TypeMappings = Combined("TypeMappings", typeMappings),
                    ModelFeatures = features
                }));
        }
        return new BuildRequest(project, outputDirectory, inputs.ToArray());
    }

    internal static string OutputFor(string projectFile, string source)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(projectFile)!, source);
        var directory = BuildFiles.IsRelativeChild(relative)
            ? Path.Combine("sources", BuildFiles.HashText(relative.Replace(Path.DirectorySeparatorChar, '/')))
            : Path.Combine("external", BuildFiles.HashText(source));
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + ".g.cs");
    }
}
