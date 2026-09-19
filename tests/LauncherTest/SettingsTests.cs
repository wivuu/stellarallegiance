using StellarAllegiance.Launcher.Diagnostics;
using StellarAllegiance.Launcher.Settings;

static class SettingsTests
{
    public static void Run()
    {
        T.Section("Settings store");
        string dir = T.TempDir("settings");
        try
        {
            string path = Path.Combine(dir, "launcher.json");
            var log = new NullLog();
            var store = new JsonSettingsStore(path, log);

            var fresh = store.Load();
            T.Check(
                fresh is { V: 1, Prefs: { BetaChannel: false, AutoLaunch: false, RenderMode: "auto" }, State.Pending: null },
                "no file → defaults (auto-launch OFF, stable channel)"
            );

            fresh.Prefs.BetaChannel = true;
            fresh.State.Pending = new PendingUpdate
            {
                TargetVersion = "1.2.3",
                FromVersion = "1.2.2",
                Attempts = 1,
                ResumePlay = true,
                Notes = [new PendingNote { Version = "1.2.3", Markdown = "* a note" }],
            };
            store.Save(fresh);
            var back = store.Load();
            T.Check(back.Prefs.BetaChannel, "prefs round-trip");
            T.Check(
                back.State.Pending is { TargetVersion: "1.2.3", FromVersion: "1.2.2", Attempts: 1, ResumePlay: true },
                "pending update round-trips"
            );
            T.Eq("* a note", back.State.Pending?.Notes[0].Markdown, "…including its release notes");
            T.Check(File.ReadAllText(path).Contains("\"betaChannel\": true"), "camelCase on disk (source-generated JSON)");
            T.Check(!File.Exists(path + ".tmp"), "the atomic-write temp file does not linger");

            File.WriteAllText(
                path,
                "{ \"v\": 1, \"prefs\": { \"autoLaunch\": true, \"fromTheFuture\": 42 }, \"alsoUnknown\": [] }"
            );
            T.Check(
                store.Load().Prefs.AutoLaunch,
                "unknown fields from a newer launcher are ignored, known ones still load"
            );

            File.WriteAllText(path, "{ this is not json");
            var recovered = store.Load();
            T.Check(recovered.Prefs is { AutoLaunch: false }, "a corrupt file never stops the launcher: defaults are used");
            T.Check(File.Exists(path + ".bad"), "…and the corrupt file is kept as .bad for inspection");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

sealed class NullLog : ILauncherLog
{
    public List<string> Markers { get; } = [];

    public void Info(string message) { }

    public void Warn(string message, Exception? ex = null) { }

    public void Error(string message, Exception? ex = null) { }

    public void Marker(string state, string details = "") => Markers.Add(details.Length == 0 ? state : $"{state} {details}");
}
