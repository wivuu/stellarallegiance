using StellarAllegiance.Shared;

// Stand-in game for the launcher's end-to-end tests. See StubGame.csproj.
//
//   --stub-report=<file>   append one line per run describing how we were started (args + the
//                          launcher's env vars). Falls back to $SA_STUB_REPORT, then a temp file.
//   --stub-exit=<code>     exit with this code. 85 (UPDATE NOW) is only honoured while the launcher
//                          says an update exists (SA_LAUNCHER_UPDATE is a version) — exactly like the
//                          real game, whose UPDATE NOW button only appears then. Without that rule a
//                          scripted "press UPDATE NOW" would loop forever after the update landed.
//   --stub-seconds=<n>     stay alive this long first (second-instance / orphan tests).
string? report = Environment.GetEnvironmentVariable("SA_STUB_REPORT");
int exitCode = 0;
double seconds = 0;
foreach (var arg in args)
{
    if (arg.StartsWith("--stub-report=", StringComparison.Ordinal))
        report = arg["--stub-report=".Length..];
    else if (arg.StartsWith("--stub-exit=", StringComparison.Ordinal))
        _ = int.TryParse(arg["--stub-exit=".Length..], out exitCode);
    else if (arg.StartsWith("--stub-seconds=", StringComparison.Ordinal))
        _ = double.TryParse(arg["--stub-seconds=".Length..], System.Globalization.CultureInfo.InvariantCulture, out seconds);
}
report ??= Path.Combine(Path.GetTempPath(), "sa-stubgame-report.txt");

string under = Environment.GetEnvironmentVariable(LauncherContract.EnvFlag) ?? "";
string update = Environment.GetEnvironmentVariable(LauncherContract.EnvUpdate) ?? "";
bool updateKnown = update.Length > 0 && update != LauncherContract.EnvUpdateNone;
if (exitCode == LauncherContract.UpdateExitCode && !updateKnown)
    exitCode = 0;

string line =
    $"run pid={Environment.ProcessId} exit={exitCode} {LauncherContract.EnvFlag}={under} {LauncherContract.EnvUpdate}={update} "
    + $"cwd={Environment.CurrentDirectory} exe={Environment.ProcessPath} args=[{string.Join('|', args)}]";
try
{
    File.AppendAllText(report, line + Environment.NewLine);
}
catch (Exception ex)
{
    Console.Error.WriteLine("stubgame: cannot write report: " + ex.Message);
}
Console.WriteLine("stubgame: " + line);

if (seconds > 0)
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
return exitCode;
