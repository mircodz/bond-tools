using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Bond.Build.Tests;

internal static class TestProcess
{
    public static async Task<TestCommandResult> Run(ProcessStartInfo start, TimeSpan timeout)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(timeout);

        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var error = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return new TestCommandResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }
    }
}

internal sealed record TestCommandResult(int ExitCode, string Output, string Error);
