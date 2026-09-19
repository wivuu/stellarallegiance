using System.Text;

namespace StellarAllegiance.Launcher.Diagnostics;

public interface ILauncherLog
{
    void Info(string message);
    void Warn(string message, Exception? ex = null);
    void Error(string message, Exception? ex = null);

    // A machine-readable state marker: `LAUNCHER_E2E_STATE: <state> k=v …`. The scripted end-to-end
    // test (scripts/launcher-e2e.ps1) and the CI smoke test poll the log for these, so their shape is
    // a contract — change the tests when you change a marker.
    void Marker(string state, string details = "");
}

// Append-only text log, flushed per line (the e2e scripts tail it while the launcher is running, and
// a launcher that dies mid-update must leave its last words on disk). Rotates at 1 MB, keeps 3.
public sealed class FileLog : ILauncherLog, IDisposable
{
    public const string MarkerPrefix = "LAUNCHER_E2E_STATE:";
    private const long RotateBytes = 1024 * 1024;
    private const int Keep = 3;

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly bool _echo;
    private StreamWriter? _writer;

    public FileLog(string path, bool echoToConsole = false)
    {
        _path = path;
        _echo = echoToConsole;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Rotate();
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)
            )
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _writer = null; // a launcher that cannot log must still launch the game
        }
    }

    public void Info(string message) => Write("INF", message, null);

    public void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);

    public void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    public void Marker(string state, string details = "") =>
        Write("INF", details.Length == 0 ? $"{MarkerPrefix} {state}" : $"{MarkerPrefix} {state} {details}", null);

    private void Write(string level, string message, Exception? ex)
    {
        string line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{Environment.ProcessId}] {level} {message}";
        if (ex is not null)
            line += Environment.NewLine + ex;
        lock (_gate)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // disk full / file yanked: logging is best-effort, never a reason to fail
            }
            if (_echo)
                Console.WriteLine(line);
        }
    }

    private void Rotate()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < RotateBytes)
            return;
        for (int i = Keep - 1; i >= 1; i--)
        {
            string from = $"{_path}.{i}";
            if (File.Exists(from))
                File.Move(from, $"{_path}.{i + 1}", overwrite: true);
        }
        File.Move(_path, $"{_path}.1", overwrite: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
