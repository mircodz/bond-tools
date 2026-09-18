using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace BondTools.Build;

public sealed class GenerateBond : Task, ICancelableTask
{
    [Required] public string ProjectFile { get; set; } = "";
    [Required] public string OutputDirectory { get; set; } = "";
    [Required] public string CompilerPath { get; set; } = "";
    public string DotNetHostPath { get; set; } = "";
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] ImportDirectories { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] Usings { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] NamespaceMappings { get; set; } = Array.Empty<ITaskItem>();
    public ITaskItem[] TypeMappings { get; set; } = Array.Empty<ITaskItem>();
    [Output] public ITaskItem[] GeneratedFiles { get; private set; } = Array.Empty<ITaskItem>();
    [Output] public ITaskItem[] WrittenFiles { get; private set; } = Array.Empty<ITaskItem>();

    private readonly object _processLock = new object();
    private Process? _process;
    private bool _cancelled;

    public override bool Execute()
    {
        string? requestPath = null;
        try
        {
            var project = Path.GetFullPath(ProjectFile);
            var projectDirectory = Path.GetDirectoryName(project)!;
            var outputDirectory = Path.GetFullPath(Path.Combine(projectDirectory, OutputDirectory));
            var compiler = Path.GetFullPath(CompilerPath);
            if (!File.Exists(compiler))
            {
                Log.LogError("BOND1001", "", "", project, 0, 0, 0, 0,
                    "The bundled Bond compiler is missing: {0}. Restore BondTools.Build.", compiler);
                return false;
            }

            var request = new XDocument(new XElement("BondBuild",
                new XAttribute("Version", "1"),
                new XAttribute("ProjectFile", project),
                new XAttribute("OutputDirectory", outputDirectory),
                Values("ImportDirectories", ImportDirectories.Select(item => item.ItemSpec)),
                Values("Usings", Usings.Select(item => item.ItemSpec)),
                Values("NamespaceMappings", NamespaceMappings.Select(item => item.ItemSpec)),
                Values("TypeMappings", TypeMappings.Select(item => item.ItemSpec)),
                new XElement("Inputs", Sources.Select(item => new XElement("Input",
                    new XAttribute("Path", item.ItemSpec),
                    new XAttribute("Descriptors", item.GetMetadata("Descriptors")),
                    new XAttribute("Clone", item.GetMetadata("Clone")),
                    new XAttribute("Equality", item.GetMetadata("Equality")),
                    new XAttribute("Debugger", item.GetMetadata("Debugger")),
                    Metadata(item, "ImportDirectories"),
                    Metadata(item, "Usings"),
                    Metadata(item, "NamespaceMappings"),
                    Metadata(item, "TypeMappings"))))));

            var requestDirectory = Path.GetDirectoryName(outputDirectory)!;
            EnsureDirectoryWithoutLinks(requestDirectory);
            Directory.CreateDirectory(requestDirectory);
            requestPath = Path.Combine(requestDirectory, "bond-request-" + Guid.NewGuid().ToString("N") + ".xml");
            using (var stream = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                request.Save(writer);

            var arguments = new CommandLineBuilder();
            arguments.AppendFileNameIfNotNull(compiler);
            arguments.AppendSwitch("--msbuild");
            arguments.AppendFileNameIfNotNull(requestPath);
            var stdout = new List<string>();
            var stderr = new List<string>();
            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = string.IsNullOrWhiteSpace(DotNetHostPath) ? "dotnet" : DotNetHostPath,
                    Arguments = arguments.ToString(),
                    WorkingDirectory = projectDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            })
            {
                process.OutputDataReceived += (_, args) => { if (args.Data != null) stdout.Add(args.Data); };
                process.ErrorDataReceived += (_, args) => { if (args.Data != null) stderr.Add(args.Data); };
                lock (_processLock)
                {
                    if (_cancelled)
                        return false;
                    process.Start();
                    _process = process;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (_processLock)
                    _process = null;
                foreach (var line in stderr)
                    Log.LogMessageFromText(line, MessageImportance.High);
                if (process.ExitCode != 0)
                {
                    if (!Log.HasLoggedErrors)
                        Log.LogError("Bond compiler failed with exit code {0}. Ensure the .NET 8 runtime is installed.", process.ExitCode);
                    return false;
                }
            }

            var generated = new List<ITaskItem>();
            var written = new List<ITaskItem>();
            foreach (var line in stdout)
            {
                if (line.StartsWith("BOND_OUTPUT\t", StringComparison.Ordinal))
                    generated.Add(new TaskItem(line.Substring("BOND_OUTPUT\t".Length)));
                else if (line.StartsWith("BOND_FILE\t", StringComparison.Ordinal))
                    written.Add(new TaskItem(line.Substring("BOND_FILE\t".Length)));
                else if (line.StartsWith("BOND_STATE\t", StringComparison.Ordinal))
                    Log.LogMessage(MessageImportance.Normal, line.Substring("BOND_STATE\t".Length));
                else
                    Log.LogMessage(MessageImportance.Low, line);
            }
            GeneratedFiles = generated.ToArray();
            WrittenFiles = written.Concat(generated).ToArray();
            return !Log.HasLoggedErrors;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
            || error is ArgumentException || error is NotSupportedException || error is Win32Exception
            || error is XmlException)
        {
            Log.LogError(null, "BOND1001", null, ProjectFile, 0, 0, 0, 0, "{0}", error.Message);
            return false;
        }
        finally
        {
            lock (_processLock)
                _process = null;
            if (requestPath != null)
            {
                try { File.Delete(requestPath); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Log.LogWarning("Could not remove Bond request '{0}': {1}", requestPath, error.Message);
                }
            }
        }
    }

    public void Cancel()
    {
        lock (_processLock)
        {
            _cancelled = true;
            if (_process != null && !_process.HasExited)
                _process.Kill();
        }
    }

    private static XElement Metadata(ITaskItem item, string name) =>
        Values(name, item.GetMetadata(name).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()));

    private static XElement Values(string name, IEnumerable<string> values) =>
        new XElement(name, values.Select(value => new XElement("Value", value)));

    private static void EnsureDirectoryWithoutLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
        {
            try
            {
                if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Bond intermediate directories cannot be symbolic links: " + directory.FullName);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
