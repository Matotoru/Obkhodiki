using System.Diagnostics;
using System.Text;

namespace ZapretHub.Core.Engine;

/// <summary>
/// Owns a single winws.exe process. winws needs administrator rights (WinDivert driver).
/// The process is placed in a kill-on-close job so it never outlives the app.
/// </summary>
public sealed class WinwsRunner : IEngineRunner, IDisposable
{
    private const int OutputTailLines = 40;

    private readonly Func<string> _winwsPath;
    private readonly TimeSpan _startupCheck;
    private readonly object _gate = new();
    private readonly Queue<string> _outputTail = new();
    private readonly KillOnCloseJob? _job = OperatingSystem.IsWindows() ? KillOnCloseJob.TryCreate() : null;
    private Process? _process;
    private Process? _stopRequested;
    private bool _disposed;

    public WinwsRunner(Func<string> winwsPath, TimeSpan? startupCheck = null)
    {
        _winwsPath = winwsPath;
        _startupCheck = startupCheck ?? TimeSpan.FromMilliseconds(700);
    }

    /// <summary>Raised when a successfully started winws exits without StopAsync. Argument: last output lines.</summary>
    public event Action<string>? Crashed;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is not null && !HasExitedSafe(_process);
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate) return _process is not null && !HasExitedSafe(_process) ? _process.Id : null;
        }
    }

    public async Task StartAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        var exe = _winwsPath();
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        lock (_gate) _outputTail.Clear();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }
        try
        {
            _job?.Assign(process);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently (app exiting): never leave a winws outside the job.
            KillAndDispose(process);
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // winws exits almost immediately on bad arguments or a missing driver; surface that as a start failure.
        try
        {
            await Task.Delay(_startupCheck, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillAndDispose(process);
            throw;
        }

        if (process.HasExited)
        {
            // The parameterless wait also drains the redirected output streams.
            process.WaitForExit();
            var code = process.ExitCode;
            process.Dispose();
            throw new InvalidOperationException($"winws exited with code {code}:{Environment.NewLine}{OutputTail()}");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                KillAndDispose(process);
                throw new ObjectDisposedException(nameof(WinwsRunner));
            }
            _process = process;
        }
        process.Exited += (_, _) => OnExited(process);
        // Exited may have fired before the handler was attached.
        if (HasExitedSafe(process)) OnExited(process);
    }

    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>Synchronous stop: safe to call from Dispose on a UI thread.
    /// If the kill fails the runner keeps tracking the process, so it is not silently orphaned.</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            if (process is null) return;
            _stopRequested = process;
        }

        Kill(process);

        lock (_gate)
        {
            if (ReferenceEquals(_process, process)) _process = null;
        }
        process.Dispose();
    }

    public string OutputTail()
    {
        lock (_gate) return string.Join(Environment.NewLine, _outputTail);
    }

    private static void KillAndDispose(Process process)
    {
        try
        {
            Kill(process);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    throw new InvalidOperationException($"winws (PID {process.Id}) did not stop within 5 seconds.");
                }
            }
        }
        catch (InvalidOperationException) when (HasExitedSafe(process))
        {
            // Exited between the check and Kill.
        }
    }

    private static bool HasExitedSafe(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void Remember(string? line)
    {
        if (line is null) return;
        lock (_gate)
        {
            _outputTail.Enqueue(line);
            while (_outputTail.Count > OutputTailLines) _outputTail.Dequeue();
        }
    }

    private void OnExited(Process process)
    {
        bool unexpected;
        lock (_gate)
        {
            unexpected = ReferenceEquals(_process, process) && !ReferenceEquals(_stopRequested, process);
            if (unexpected) _process = null;
        }
        if (!unexpected) return;
        process.WaitForExit();
        var tail = OutputTail();
        process.Dispose();
        Crashed?.Invoke(tail);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try
        {
            Stop();
        }
        catch (Exception ex)
        {
            // Dispose runs during app exit; the job handle closing below still kills winws.
            Trace.TraceError("winws stop on dispose failed: " + ex.Message);
        }
        _job?.Dispose();
    }
}
