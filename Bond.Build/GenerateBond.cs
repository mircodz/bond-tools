using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace BondTools.Build;

public sealed class GenerateBond : Task, ICancelableTask
{
    [Required]
    public string ProjectFile { get; set; } = "";

    [Required]
    public string OutputDirectory { get; set; } = "";

    public ITaskItem[] Sources { get; set; } = [];
    public ITaskItem[] ImportDirectories { get; set; } = [];
    public ITaskItem[] Usings { get; set; } = [];
    public ITaskItem[] NamespaceMappings { get; set; } = [];
    public ITaskItem[] TypeMappings { get; set; } = [];
    [Output]
    public ITaskItem[] GeneratedFiles { get; private set; } = [];

    [Output]
    public ITaskItem[] WrittenFiles { get; private set; } = [];

    private readonly CancellationTokenSource _cancellation = new();

    public override bool Execute()
    {
        try
        {
            var request = BuildRequest.Create(ProjectFile, OutputDirectory, Sources,
                ImportDirectories, Usings, NamespaceMappings, TypeMappings);
            var result = GenerationEngine.RunAsync(request, _cancellation.Token).GetAwaiter().GetResult();

            foreach (var error in result.Errors)
            {
                Log.LogError(null, "BOND1001", null, error.FilePath ?? ProjectFile,
                    error.Line, error.Column, 0, 0, "{0}", error.Message);
            }

            if (result.Errors.Count != 0)
            {
                return false;
            }

            GeneratedFiles = result.GeneratedFiles.Select(path => (ITaskItem)new TaskItem(path)).ToArray();
            WrittenFiles = result.WrittenFiles.Select(path => (ITaskItem)new TaskItem(path)).ToArray();
            Log.LogMessage(MessageImportance.High, "{0}", result.Status);
            return true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            Log.LogMessage(MessageImportance.High, "Bond generation cancelled.");
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            Log.LogError(null, "BOND1001", null, ProjectFile, 0, 0, 0, 0, "{0}", error.Message);
            return false;
        }
    }

    public void Cancel() => _cancellation.Cancel();
}
