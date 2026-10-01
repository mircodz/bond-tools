using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bond.Parser.CodeGeneration;
using Bond.Parser.Parser;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace BondTools.Build;

/// <summary>
/// Generates C# for each Bond item. Generation is cheap, so it always runs; outputs are only rewritten when their
/// content changes, which keeps the C# compilation incremental.
/// </summary>
public sealed class GenerateBond : Microsoft.Build.Utilities.Task
{
    public ITaskItem[] Sources { get; set; } = [];

    [Required]
    public string ProjectDirectory { get; set; } = "";

    [Required]
    public string OutputDirectory { get; set; } = "";

    [Output]
    public ITaskItem[] GeneratedFiles { get; private set; } = [];

    public override bool Execute()
    {
        Directory.CreateDirectory(OutputDirectory);
        var outputs = new List<string>();
        foreach (var source in Sources)
        {
            var path = source.GetMetadata("FullPath");
            var output = Path.Combine(OutputDirectory, OutputName(path));
            outputs.Add(output);
            try
            {
                Generate(source, path, output);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.LogError(null, "BOND1001", null, path, 0, 0, 0, 0, "{0}", error.Message);
            }
        }

        foreach (var stale in Directory.EnumerateFiles(OutputDirectory, "*.g.cs").Except(outputs))
        {
            if (File.ReadLines(stale).FirstOrDefault() == CSharpGenerator.GeneratedHeader)
            {
                File.Delete(stale);
            }
        }

        GeneratedFiles = outputs.Select(output => (ITaskItem)new TaskItem(Escape(output))).ToArray();
        return !Log.HasLoggedErrors;
    }

    private void Generate(ITaskItem source, string path, string output)
    {
        var options = new CSharpGenerationOptions
        {
            UsingNamespaces = List(source, "Usings"),
            NamespaceMappings = List(source, "NamespaceMappings"),
            TypeMappings = List(source, "TypeMappings"),
            ModelFeatures = (IsEnabled(source, "Clone") ? CSharpModelFeatures.Cloning : 0)
                | (IsEnabled(source, "Equality") ? CSharpModelFeatures.Equality : 0)
                | (IsEnabled(source, "ToString") ? CSharpModelFeatures.StringRepresentation : 0),
            Serialization = IsEnabled(source, "Serialization")
        };
        var imports = List(source, "ImportDirectories").Select(directory => Path.GetFullPath(directory, ProjectDirectory));
        var parsed = ParserFacade.ParseFileAsync(path, DefaultImportResolver.Create(imports)).GetAwaiter().GetResult();
        var generated = parsed.Success ? CSharpGenerator.Generate(parsed.Ast!, path, options) : null;
        foreach (var error in generated?.Errors ?? parsed.Errors)
        {
            Log.LogError(null, "BOND1001", null, error.FilePath ?? path, error.Line, error.Column, 0, 0, "{0}", error.Message);
        }

        if (generated is { Success: true } && !(File.Exists(output) && File.ReadAllText(output) == generated.Code))
        {
            File.WriteAllText(output, generated.Code);
        }
    }

    // Unique per source path, so schemas with the same name in different directories do not collide.
    private static string OutputName(string source) =>
        Path.GetFileNameWithoutExtension(source) + "." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..8].ToLowerInvariant() + ".g.cs";

    private static string[] List(ITaskItem item, string name) =>
        item.GetMetadata(name).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsEnabled(ITaskItem item, string name) =>
        string.Equals(item.GetMetadata(name), "true", StringComparison.OrdinalIgnoreCase);

    // Task item specs are MSBuild-escaped; literal paths may contain characters such as ';' or '%'.
    private static string Escape(string path) =>
        string.Concat(path.Select(character => "%*?@$();'".Contains(character) ? $"%{(int)character:X2}" : character.ToString()));
}
