using System.Diagnostics;

namespace Agentic.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, long DurationMs);

/// <summary>Runs an external program. Abstracted so model adapters and validators can be tested without real processes.</summary>
public interface IProcessRunner
{
    /// <param name="arguments">Passed as an argument list (never through a shell), so values cannot inject commands.</param>
    /// <param name="standardInput">Written to stdin and then closed; null closes stdin immediately.</param>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, string workingDirectory,
        TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? environment = null);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput,
        string workingDirectory, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            if (standardInput is not null) await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // the process exited before reading its input; its exit code and stderr explain why
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        var output = await stdout;
        var error = await stderr;
        return new ProcessResult(timedOut ? -1 : process.ExitCode, output, error, timedOut, clock.ElapsedMilliseconds);
    }
}
