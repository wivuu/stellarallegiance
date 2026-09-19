using StellarAllegiance.Launcher.Game;

// The launcher is the front door for every way the game used to be started, so argument passthrough
// is a compatibility contract with every existing script, harness flag and shortcut.
static class ArgsTests
{
    public static void Run()
    {
        T.Section("LauncherArgs");

        var a = LauncherArgs.Parse([
            "--host",
            "1.2.3.4:8090",
            "--launcher-feed=/tmp/feed",
            "--anonymous",
            "--",
            "--ui-shot=/tmp/x.png",
        ]);
        T.Seq(
            ["--host", "1.2.3.4:8090", "--anonymous", "--", "--ui-shot=/tmp/x.png"],
            a.GameArgs,
            "game args keep their order, the bare -- and its tail"
        );
        T.Eq("/tmp/feed", a.Feed, "--launcher-feed= is consumed");

        var tail = LauncherArgs.Parse(["--", "--launcher-feed=x", "--launcher-showcase"]);
        T.Seq(
            ["--", "--launcher-feed=x", "--launcher-showcase"],
            tail.GameArgs,
            "after a bare -- nothing is ours, even --launcher-* look-alikes"
        );
        T.Eq(null, tail.Feed, "…and it is not parsed as a launcher flag either");
        T.Check(!tail.Showcase, "…nor as a bare launcher switch");

        // Only the = form carries a value: a launcher flag must never swallow the next token.
        var bare = LauncherArgs.Parse(["--launcher-feed", "/tmp/feed", "--host", "h:1"]);
        T.Eq(null, bare.Feed, "--launcher-feed without = has no value");
        T.Seq(["/tmp/feed", "--host", "h:1"], bare.GameArgs, "…and the following token still reaches the game");

        var psn = LauncherArgs.Parse(["-psn_0_12345", "--anonymous"]);
        T.Seq(["--anonymous"], psn.GameArgs, "macOS -psn_* is dropped");

        var unknown = LauncherArgs.Parse(["--launcher-tpyo=1", "--anonymous"]);
        T.Seq(["--launcher-tpyo=1"], unknown.Unknown, "an unknown --launcher-* flag is reported");
        T.Seq(["--anonymous"], unknown.GameArgs, "…and never leaks into the game");

        var all = LauncherArgs.Parse([
            "--launcher-game=/g",
            "--launcher-data=/d",
            "--launcher-render=software",
            "--launcher-resume=play",
            "--launcher-selftest=update",
            "--launcher-fake=offer",
            "--launcher-shot=/s.png",
            "--launcher-showcase",
            "--launcher-no-autolaunch",
        ]);
        T.Check(
            all
                is {
                    GameOverride: "/g",
                    DataDir: "/d",
                    Render: "software",
                    Resume: "play",
                    SelfTest: "update",
                    Fake: "offer",
                    Shot: "/s.png",
                    Showcase: true,
                    NoAutoLaunch: true
                },
            "every launcher flag parses"
        );
        T.Eq(0, all.GameArgs.Count, "…and none reaches the game");

        T.Check(
            LauncherArgs.Parse(["--veloapp-install", "1.0.0"]).IsVelopackHook,
            "--veloapp-* as args[0] is a Velopack hook"
        );
        T.Check(LauncherArgs.Parse(["--squirrel-updated", "1.0.0"]).IsVelopackHook, "--squirrel-* (legacy) too");
        T.Check(!LauncherArgs.Parse(["--host", "--veloapp-install"]).IsVelopackHook, "…but only in first position");

        // Restart args: what Velopack relaunches us with after an apply.
        var r = LauncherArgs.Parse([
            "--launcher-feed=/f",
            "--launcher-resume=play",
            "--host",
            "h:1",
            "--",
            "--launcher-resume=keep-me",
        ]);
        T.Seq(
            ["--launcher-feed=/f", "--host", "h:1", "--", "--launcher-resume=keep-me"],
            r.RestartArgs(resumePlay: false),
            "restart args drop a stale resume marker, keep the rest (and the game's tail) intact"
        );
        T.Seq(
            ["--launcher-resume=play", "--launcher-feed=/f", "--host", "h:1", "--", "--launcher-resume=keep-me"],
            r.RestartArgs(resumePlay: true),
            "resume=play goes in FRONT so it can never land after a bare --"
        );
        T.Eq("play", LauncherArgs.Parse(r.RestartArgs(true)).Resume, "…and round-trips through Parse");
    }
}
