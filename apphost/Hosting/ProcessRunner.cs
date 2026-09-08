using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace StellarAllegiance.AppHost.Hosting;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? StdinText = null
);

public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> OutputTail)
{
    public string Tail => string.Join(Environment.NewLine, OutputTail);
}

// Runs child processes with every stdout/stderr line forwarded to an ILogger (so a dashboard command or a
// resource's console log shows `dotnet build` / `railway up` / Godot output live). Never goes through a
// shell: arguments are passed as an ArgumentList, so paths with spaces and `--` separators survive intact.
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, ILogger log, CancellationToken ct, int tailLines = 60)
    {
        using var process = Start(spec, log, out var tail, tailLines);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, tail.ToArray());
    }

    // Detached launch: output keeps streaming to the logger, the caller owns the Process (see ClientLauncher).
    public static Process StartDetached(ProcessSpec spec, ILogger log) => Start(spec, log, out _, 0);

    static Process Start(ProcessSpec spec, ILogger log, out Queue<string> tail, int tailLines)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StdinText is not null,
        };
        foreach (var a in spec.Args)
            psi.ArgumentList.Add(a);
        if (spec.Environment is not null)
            foreach (var (k, v) in spec.Environment)
                psi.Environment[k] = v;

        var ring = new Queue<string>();
        tail = ring;
        var display = $"{Path.GetFileName(spec.FileName)} {string.Join(' ', spec.Args)}";
        log.LogInformation("$ {Command}", display);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Forward(e.Data, LogLevel.Information);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, LogLevel.Warning);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (spec.StdinText is not null)
        {
            process.StandardInput.Write(spec.StdinText);
            process.StandardInput.Close();
        }
        return process;

        void Forward(string? line, LogLevel level)
        {
            if (line is null)
                return;
            log.Log(level, "{Line}", line);
            if (tailLines <= 0)
                return;
            lock (ring)
            {
                ring.Enqueue(line);
                while (ring.Count > tailLines)
                    ring.Dequeue();
            }
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
