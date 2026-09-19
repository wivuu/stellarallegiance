using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace StellarAllegiance.Launcher.Instance;

// One launcher per user. OWNERSHIP is an exclusive handle on `launcher.lock`: the kernel releases it when
// the process dies, so a crashed launcher can never wedge the next start (no stale pid files).
//
// Why this matters beyond tidiness: only the instance that owns the lock may ever touch Velopack's apply
// path. A second launcher start while a match is running must do NOTHING but hand focus back — on Windows
// an apply kills every process under the install root, including the game.
//
// The DOORBELL is a current-user-only named pipe: a second instance rings it and leaves; the owner raises
// Activated (the flow then focuses the running game, or re-shows the launcher window).
//
// NEVER identify launcher/game processes by name: the launcher is `StellarLauncher` and the game is
// `stellarallegiance`, and Windows process names are case-insensitive.
public sealed class SingleInstance : IDisposable
{
    private const string Ring = "SHOW";
    private readonly string _lockPath;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private FileStream? _lock;

    public SingleInstance(string lockPath)
    {
        _lockPath = lockPath;
        // Short and per-data-dir: on Unix a pipe is a socket file whose whole path must fit in ~104 bytes.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(lockPath))))[..12];
        _pipeName = "sa-launcher-" + hash;
    }

    public bool IsOwner => _lock is not null;

    // Raised (on a background thread) when another launcher instance rang the doorbell.
    public event Action? Activated;

    // Retries briefly: when Velopack restarts us after an update, the new process can start a moment
    // before the old one has finished exiting and released the lock.
    public bool TryAcquire(int attempts = 3, int delayMs = 500)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _ = ListenAsync(_stop.Token);
                return true;
            }
            catch (IOException)
            {
                // held by another launcher
            }
            catch (UnauthorizedAccessException)
            {
                // read-only data dir: run without single-instance protection rather than not at all
                return true;
            }
            if (i + 1 < attempts)
                Thread.Sleep(delayMs);
        }
        return false;
    }

    // Called by the instance that did NOT get the lock, right before it exits. Best effort.
    public void RingOwner(int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            client.Write(Encoding.ASCII.GetBytes(Ring));
            client.Flush();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // the owner is busy or mid-exit: nothing to do, and never a reason to fail
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                );
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var buffer = new byte[16];
                int read = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (Encoding.ASCII.GetString(buffer, 0, read) == Ring)
                    Activated?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A doorbell that cannot be set up (exotic temp dir, stale socket) is not worth dying for.
                try
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lock?.Dispose();
        _lock = null;
    }
}
