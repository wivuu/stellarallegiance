using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Game;
using StellarAllegiance.Launcher.Settings;
using StellarAllegiance.Launcher.Update;
using StellarAllegiance.Shared;

// LauncherFlow — the launcher's brain — driven entirely by fakes (Fakes.cs). The window is just a view of
// LauncherFlow.View, so everything a player can see or press is pinned here without a UI.
static class FlowTests
{
    public static void Run()
    {
        Boot();
        CheckAndUpdate();
        DownloadPhases();
        Failures();
        Playing();
        GameAsksForUpdate();
        AfterRestart();
        ApplyAttemptCap();
        OrphanAndSecondInstance();
        AutoLaunch();
        BetaAndTranslocation();
        TrackerAndCopy();
        InvariantFuzz();
    }

    static void Boot()
    {
        T.Section("Flow · boot");

        using (var dev = new Rig())
        {
            dev.Updates.IsInstalled = false;
            dev.Start();
            T.Eq(LauncherState.NotInstalled, dev.View.State, "a dev run (not installed) never talks to the feed");
            T.Eq(0, dev.Updates.Checks.Count, "…no check");
            T.Check(dev.View.Primary is { Label: "LAUNCH", Enabled: true }, "…but LAUNCH works when the game is there");
            T.Check(dev.View.Footer.StartsWith("DEV BUILD"), "…and the footer says DEV BUILD");
        }
        using (var devNoGame = new Rig(gameExists: false))
        {
            devNoGame.Updates.IsInstalled = false;
            devNoGame.Start();
            T.Check(devNoGame.View.Primary is { Enabled: false }, "LAUNCH is disabled when there is no game to launch");
        }

        using var r = new Rig().Start();
        T.Eq(LauncherState.Checking, r.View.State, "an installed launcher checks the feed on start");
        T.Check(
            r.View.Primary is { Command: LauncherCommand.Play, Enabled: true },
            "PLAY is available WHILE checking — the check never gates the game"
        );
        T.Eq(BarMode.Sweep, r.View.Bar, "…with an indeterminate bar");
        r.Updates.CompleteCheck(null);
        r.Pump();
        T.Eq(LauncherState.UpToDate, r.View.State, "no offer → up to date");
        T.Eq("v1.0.0 · STABLE · test-rid", r.View.Footer, "footer = version · channel · rid");
        T.Check(
            r.Settings.Value.State is { LastCheckLatest: "1.0.0", LastCheckUtc: not null },
            "a successful check is remembered"
        );

        using var cached = new Rig();
        cached.Settings.Value.State.LastCheckUtc = cached.Time.GetUtcNow() - TimeSpan.FromMinutes(2);
        cached.Settings.Value.State.LastCheckLatest = "1.0.0";
        cached.Start();
        T.Eq(LauncherState.UpToDate, cached.View.State, "checked 2 minutes ago and already current → no network round-trip");
        T.Eq(0, cached.Updates.Checks.Count, "…really none");

        using var stale = new Rig();
        stale.Settings.Value.State.LastCheckUtc = stale.Time.GetUtcNow() - TimeSpan.FromMinutes(6);
        stale.Settings.Value.State.LastCheckLatest = "1.0.0";
        stale.Start();
        T.Eq(1, stale.Updates.Checks.Count, "a 6-minute-old answer is stale → check again");

        using var always = new Rig(alwaysCheck: true);
        always.Settings.Value.State.LastCheckUtc = always.Time.GetUtcNow();
        always.Settings.Value.State.LastCheckLatest = "1.0.0";
        always.Start();
        T.Eq(1, always.Updates.Checks.Count, "AlwaysCheck (self-test / feed override) bypasses the cache");
    }

    static void CheckAndUpdate()
    {
        T.Section("Flow · update available → download → apply");
        using var r = new Rig(["--host", "h:1", "--", "--ui-x"]).Start();
        var offer = FakeUpdates.Offer("1.1.0", delta: true, "* notes for 1.1.0");
        r.Updates.CompleteCheck(offer);
        r.Pump();
        T.Eq(LauncherState.UpdateAvailable, r.View.State, "an offer → UPDATE AVAILABLE");
        T.Eq("v1.1.0", r.View.Pill, "the pill names the new version");
        T.Eq("patch 48.2 MB · full 343.0 MB", r.View.Subline, "a delta shows patch size AND full size");
        T.Check(r.View.Primary is { Label: "UPDATE NOW", Command: LauncherCommand.Update }, "primary = UPDATE NOW");
        T.Check(
            r.View.Secondary is { Label: "PLAY v1.0.0", Command: LauncherCommand.Play },
            "secondary = PLAY v{current}: updates are offered, never forced"
        );
        T.Eq(1, r.View.Notes.Count, "the offer's release notes are shown");
        T.Eq("PATCH NOTES", r.View.NotesTitle, "…under PATCH NOTES");

        r.Press(LauncherCommand.Update);
        T.Eq(LauncherState.Downloading, r.View.State, "UPDATE NOW → downloading");
        T.Check(
            r.View.Primary is null && r.View.Secondary is { Label: "CANCEL", Enabled: true },
            "only CANCEL is offered while downloading"
        );

        r.Updates.CompleteDownload();
        r.Pump();
        T.Eq(LauncherState.Applying, r.View.State, "download done → handed to the updater");
        T.Check(r.View.Primary is null && r.View.Secondary is null, "nothing can be pressed while applying");
        T.Check(!r.Flow.RequestClose(), "…and the window refuses to close (closing would strand the updater)");
        T.Eq(1, r.Updates.Applies.Count, "applied exactly once");
        T.Check(
            !r.Updates.Applies[0].Silent,
            "NOT silent: silent mode refuses the elevation prompt a root-owned install needs"
        );
        T.Seq(
            ["--launcher-game=" + r.GameExe, "--host", "h:1", "--", "--ui-x"],
            r.Updates.Applies[0].RestartArgs,
            "Velopack restarts us with the original command line"
        );
        T.Eq(0, r.Host.ExitCode, "the launcher exits so the updater can swap the install");
        var pending = r.Settings.Value.State.Pending;
        T.Check(
            pending is { TargetVersion: "1.1.0", FromVersion: "1.0.0", Attempts: 1, ResumePlay: false },
            "a pending record is persisted BEFORE the hand-off"
        );
        T.Eq("* notes for 1.1.0", pending?.Notes[0].Markdown, "…carrying the notes for the WHAT'S NEW screen");

        using var gated = new Rig().Start();
        gated.Updates.CompleteCheck(null);
        gated.Pump();
        gated.Press(LauncherCommand.Update);
        T.Eq(LauncherState.UpToDate, gated.View.State, "a command the current view does not offer is ignored");
        T.Eq(0, gated.Updates.Downloads.Count, "…so nothing was downloaded");
    }

    static void DownloadPhases()
    {
        T.Section("Flow · download phases");
        using var r = new Rig().Start();
        r.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0", delta: true));
        r.Pump();
        r.Press(LauncherCommand.Update);

        r.Updates.Progress(35);
        r.Pump();
        T.Eq("receiving patch · 50%", r.View.Subline, "Velopack's 0-70 patch download is shown as 0-100");
        T.Check(r.View is { Bar: BarMode.Fill, BarPercent: 50 }, "…on a determinate bar");
        r.Updates.Progress(35);
        r.Pump();
        T.Eq(50, r.View.BarPercent, "duplicate progress values are harmless");

        r.Updates.Progress(70);
        r.Pump();
        T.Eq("REBUILDING PACKAGE", r.View.Title, "at 70 the full package is being rebuilt from the deltas");
        T.Check(
            r.View is { Bar: BarMode.Sweep, Secondary: { Enabled: false } },
            "…indeterminate, and CANCEL is disabled (the patch process cannot be cancelled)"
        );
        r.Press(LauncherCommand.Cancel);
        T.Eq(LauncherState.Downloading, r.View.State, "…so pressing it does nothing");

        r.Updates.Progress(12);
        r.Pump();
        T.Eq(
            "patch rejected — pulling full package · 12%",
            r.View.Subline,
            "progress going BACKWARDS = Velopack fell back to the full package"
        );
        T.Check(r.View.Secondary is { Enabled: true }, "…which can be cancelled again");

        r.Press(LauncherCommand.Cancel);
        T.Eq(LauncherState.UpdateAvailable, r.View.State, "CANCEL returns to the offer");
        T.Eq(0, r.Updates.Applies.Count, "…and nothing was applied");
    }

    static void Failures()
    {
        T.Section("Flow · failures never block PLAY");
        using var r = new Rig().Start();
        r.Updates.FailCheck(new HttpRequestException("rate limited (403)"));
        r.Pump();
        T.Eq(LauncherState.CheckFailed, r.View.State, "a failed check is a state, not an error dialog");
        T.Check(
            r.View.Primary is { Command: LauncherCommand.Play, Enabled: true },
            "PLAY still works offline / rate-limited"
        );
        T.Eq("READY FOR LAUNCH", r.View.Title, "…and the title says so");
        T.Check(
            r.View.Secondary is { Command: LauncherCommand.RetryCheck, Enabled: false },
            "RETRY CHECK starts on a cooldown (GitHub allows 60 unauthenticated calls/hour/IP)"
        );
        r.Tick(29);
        T.Check(r.View.Secondary is { Enabled: false }, "…still cooling down at 29 s");
        r.Tick(2);
        T.Check(r.View.Secondary is { Enabled: true }, "…available after 30 s");
        r.Press(LauncherCommand.RetryCheck);
        T.Eq(2, r.Updates.Checks.Count, "RETRY CHECK asks again");

        using var d = new Rig().Start();
        d.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        d.Pump();
        d.Press(LauncherCommand.Update);
        d.Updates.FailDownload(new IOException("disk full"));
        d.Pump();
        T.Eq(LauncherState.UpdateFailed, d.View.State, "a failed download → UPDATE FAILED");
        T.Check(
            d.View.Alert is { Tone: Tone.Danger } a && a.Body.Contains("untouched"),
            "…reassuring that the installed build is untouched"
        );
        T.Check(
            d.View.Primary is { Command: LauncherCommand.RetryUpdate }
                && d.View.Secondary is { Command: LauncherCommand.Play },
            "RETRY UPDATE, or just play the current build"
        );
        d.Press(LauncherCommand.RetryUpdate);
        d.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        d.Pump();
        T.Eq(LauncherState.Downloading, d.View.State, "RETRY UPDATE re-checks and goes straight back into the download");

        using var missing = new Rig(gameExists: false).Start();
        missing.Updates.CompleteCheck(null);
        missing.Pump();
        missing.Press(LauncherCommand.Play);
        T.Eq(
            LauncherState.GameMissing,
            missing.View.State,
            "PLAY without a game binary → GAME FILES MISSING, not an exception"
        );

        using var noStart = new Rig().Start();
        noStart.Updates.CompleteCheck(null);
        noStart.Pump();
        noStart.Games.FailToStart = new InvalidOperationException("exec format error");
        noStart.Press(LauncherCommand.Play);
        T.Eq(LauncherState.GameCrashed, noStart.View.State, "a game that cannot be started is reported like a crash");
        T.Check(noStart.View.Alert?.Body.Contains("exec format error") == true, "…with the reason");
    }

    static void Playing()
    {
        T.Section("Flow · playing");
        using var r = new Rig(["--anonymous", "--", "--ui-x"]).Start();
        r.Press(LauncherCommand.Play);
        T.Eq(LauncherState.GameRunning, r.View.State, "PLAY during the check starts the game at once");
        T.Seq(["--anonymous", "--", "--ui-x"], r.Games.Runs[0].Args, "the game gets its arguments verbatim");
        T.Eq(
            null,
            r.Games.Runs[0].KnownUpdate,
            "the check had not finished → SA_LAUNCHER_UPDATE is absent (the game may check for itself)"
        );
        T.Check(r.Host.Calls.Contains("hide"), "the launcher hides and stays resident");

        r.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        r.Pump();
        T.Eq(LauncherState.GameRunning, r.View.State, "a check answer arriving mid-game changes nothing on screen");
        T.Eq(0, r.Updates.Downloads.Count, "…and starts NO update work");

        r.Games.Exit(0);
        r.Pump();
        T.Eq(LauncherState.Exiting, r.View.State, "the game quit → the launcher is done too");
        T.Eq(0, r.Host.ExitCode, "…exit 0");
        r.Press(LauncherCommand.Play);
        T.Eq(
            1,
            r.Games.Runs.Count,
            "Exiting is terminal: nothing can start the game again in the instant before the process ends"
        );

        using var known = new Rig().Start();
        known.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        known.Pump();
        known.Press(LauncherCommand.Play);
        T.Eq(
            "1.1.0",
            known.Games.Runs[0].KnownUpdate,
            "PLAY v{current} with an offer known → the game is told which version it can ask for"
        );

        using var none = new Rig().Start();
        none.Updates.CompleteCheck(null);
        none.Pump();
        none.Press(LauncherCommand.Play);
        T.Eq(
            LauncherContract.EnvUpdateNone,
            none.Games.Runs[0].KnownUpdate,
            "checked and current → the game is told \"none\" and skips its own check"
        );

        using var crash = new Rig().Start();
        crash.Updates.CompleteCheck(null);
        crash.Pump();
        crash.Press(LauncherCommand.Play);
        crash.Games.Exit(139, seconds: 724);
        crash.Pump();
        T.Eq(LauncherState.GameCrashed, crash.View.State, "exit 139 → SIGNAL LOST");
        T.Check(crash.Host.Calls.Contains("show"), "…the launcher comes back");
        T.Check(
            crash.View.Alert?.Body.Contains("Exit code 139 after 12m 04s") == true,
            "…with the exit code and how long the game ran"
        );
        crash.Press(LauncherCommand.OpenLogFolder);
        T.Check(
            crash.Host.Calls.Any(c => c.StartsWith("folder:") && c.EndsWith("godot-logs")),
            "OPEN LOG FOLDER points at the game's log folder"
        );
        crash.Press(LauncherCommand.Relaunch);
        T.Eq(2, crash.Games.Runs.Count, "RELAUNCH starts the game again");

        using var closing = new Rig().Start();
        T.Check(closing.Flow.RequestClose(), "closing the window while idle is allowed");
        T.Eq(LauncherState.Exiting, closing.View.State, "…and is terminal");
    }

    static void GameAsksForUpdate()
    {
        T.Section("Flow · UPDATE NOW pressed in-game (exit code 85)");
        using var r = new Rig(["--host", "h:1"]).Start();
        r.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        r.Pump();
        r.Press(LauncherCommand.Play);
        r.Games.Exit(LauncherContract.UpdateExitCode);
        r.Pump();
        T.Eq(LauncherState.UpdateRequestedByGame, r.View.State, "exit 85 → UPDATE REQUESTED");
        T.Check(r.Host.Calls.Contains("show"), "the launcher re-shows itself");
        T.Eq(2, r.Updates.Checks.Count, "…and asks the feed again (cache bypassed)");
        r.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        r.Pump();
        T.Eq(
            LauncherState.Downloading,
            r.View.State,
            "…then downloads WITHOUT another click: the player already said yes in-game"
        );
        r.Updates.CompleteDownload();
        r.Pump();
        T.Check(
            r.Settings.Value.State.Pending is { ResumePlay: true },
            "the pending record says: go straight back into the game"
        );
        T.Seq(
            ["--launcher-resume=play", "--launcher-game=" + r.GameExe, "--host", "h:1"],
            r.Updates.Applies[0].RestartArgs,
            "…and so do the restart args"
        );

        using var gone = new Rig().Start();
        gone.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        gone.Pump();
        gone.Press(LauncherCommand.Play);
        gone.Games.Exit(LauncherContract.UpdateExitCode);
        gone.Pump();
        gone.Updates.CompleteCheck(null); // the release was pulled in the meantime
        gone.Pump();
        T.Eq(
            LauncherState.UpToDate,
            gone.View.State,
            "the game asked for an update the feed no longer offers → nothing to install"
        );
        T.Eq(3, gone.View.CountdownSeconds, "…so go back into the game by itself");
        gone.Tick(3);
        T.Eq(2, gone.Games.Runs.Count, "…after the countdown");

        using var cancel = new Rig().Start();
        cancel.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        cancel.Pump();
        cancel.Press(LauncherCommand.Play);
        cancel.Games.Exit(LauncherContract.UpdateExitCode);
        cancel.Pump();
        cancel.Press(LauncherCommand.Cancel);
        T.Eq(LauncherState.UpdateAvailable, cancel.View.State, "CANCEL during the re-check returns to the known offer");
        cancel.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        cancel.Pump();
        T.Eq(LauncherState.UpdateAvailable, cancel.View.State, "…and the superseded check's late answer is ignored");
        T.Eq(0, cancel.Updates.Downloads.Count, "…no download was started");
    }

    static void AfterRestart()
    {
        T.Section("Flow · first start after an update");
        using var r = new Rig();
        r.Updates.CurrentVersion = "1.1.0";
        r.Settings.Value.State.Pending = new PendingUpdate
        {
            TargetVersion = "1.1.0",
            FromVersion = "1.0.0",
            Attempts = 1,
            Notes = [new PendingNote { Version = "1.1.0", Markdown = "* shiny" }],
        };
        r.Start();
        T.Eq(LauncherState.UpdatedJustNow, r.View.State, "pending target == current version → UPDATE COMPLETE");
        T.Eq("v1.1.0 INSTALLED", r.View.Pill, "…naming the version");
        T.Eq("WHAT'S NEW", r.View.NotesTitle, "the notes panel becomes WHAT'S NEW");
        T.Eq("* shiny", r.View.Notes[0].Markdown, "…showing the notes carried across the restart");
        T.Eq(null, r.Settings.Value.State.Pending, "the pending record is consumed");
        T.Eq(0, r.Updates.Checks.Count, "no feed check: we just installed the newest thing it had");
        T.Eq(null, r.View.CountdownSeconds, "no auto-launch unless asked for");
        r.Press(LauncherCommand.Play);
        T.Eq(LauncherContract.EnvUpdateNone, r.Games.Runs[0].KnownUpdate, "the game is told it is current");

        using var resume = new Rig(["--launcher-resume=play"]);
        resume.Updates.CurrentVersion = "1.1.0";
        resume.Settings.Value.State.Pending = new PendingUpdate
        {
            TargetVersion = "1.1.0",
            FromVersion = "1.0.0",
            Attempts = 1,
            ResumePlay = true,
        };
        resume.Start();
        T.Eq(3, resume.View.CountdownSeconds, "the game asked for this update → a 3 s countdown back into it");
        T.Check(resume.View.Subline.Contains("auto-launch in 3s"), "…announced on screen");
        resume.Tick(2);
        T.Eq(1, resume.View.CountdownSeconds, "…ticking");
        resume.Tick(1);
        T.Eq(LauncherState.GameRunning, resume.View.State, "…and then the game starts by itself");

        using var interrupted = new Rig();
        interrupted.Updates.CurrentVersion = "1.1.0";
        interrupted.Settings.Value.State.Pending = new PendingUpdate
        {
            TargetVersion = "1.1.0",
            FromVersion = "1.0.0",
            Attempts = 1,
            ResumePlay = true,
        };
        interrupted.Start();
        interrupted.Flow.OnUserInput();
        interrupted.Pump();
        T.Eq(null, interrupted.View.CountdownSeconds, "any input cancels the countdown");
        interrupted.Tick(10);
        T.Eq(0, interrupted.Games.Runs.Count, "…for good");

        using var downloaded = new Rig();
        downloaded.Updates.PendingVersion = "1.1.0";
        downloaded.Start();
        T.Eq(
            LauncherState.Applying,
            downloaded.View.State,
            "a package downloaded earlier but never applied is installed on the next start"
        );
        T.Eq(
            0,
            downloaded.Updates.Checks.Count + downloaded.Updates.Downloads.Count,
            "…without checking or downloading again"
        );
        T.Check(downloaded.Updates.Applies is [{ Offer: null }], "…(apply the newest downloaded package)");
    }

    static void ApplyAttemptCap()
    {
        T.Section("Flow · failed applies cannot loop");
        using var once = new Rig();
        once.Settings.Value.State.Pending = new PendingUpdate
        {
            TargetVersion = "1.1.0",
            FromVersion = "1.0.0",
            Attempts = 1,
        };
        once.Updates.PendingVersion = "1.1.0"; // the downloaded package is still sitting there
        once.Start();
        T.Eq(
            LauncherState.UpdateFailed,
            once.View.State,
            "pending target != current version → the apply failed (Velopack restarted the old build)"
        );
        T.Eq(0, once.Updates.Applies.Count, "…and it is NOT silently re-applied on start (that would be an update loop)");
        T.Check(
            once.Settings.Value.State.Pending is not null,
            "the failed record is kept, so the NEXT start does not auto-install either"
        );
        T.Check(once.View.Primary is { Command: LauncherCommand.RetryUpdate }, "one failure → RETRY UPDATE is offered");
        once.Press(LauncherCommand.RetryUpdate);
        once.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        once.Pump();
        once.Updates.CompleteDownload();
        once.Pump();
        T.Check(once.Settings.Value.State.Pending is { Attempts: 2 }, "the retry is counted as attempt 2");

        using var twice = new Rig();
        twice.Settings.Value.State.Pending = new PendingUpdate
        {
            TargetVersion = "1.1.0",
            FromVersion = "1.0.0",
            Attempts = 2,
        };
        twice.Start();
        T.Eq(
            LauncherState.Checking,
            twice.View.State,
            "out of attempts, but a NEWER release may fix it → still ask the feed"
        );
        twice.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        twice.Pump();
        T.Eq(LauncherState.UpdateFailed, twice.View.State, "still only the broken version on offer → UPDATE FAILED");
        T.Check(
            twice.View.Primary is { Command: LauncherCommand.Play }
                && twice.View.Secondary is { Command: LauncherCommand.OpenReleases },
            "…now offering PLAY and the releases page instead of another retry"
        );
        twice.Press(LauncherCommand.OpenReleases);
        T.Check(
            twice.Host.Calls.Contains("url:" + LauncherCopy.ReleasesUrl),
            "…which opens the fixed releases URL (never a URL from the feed)"
        );

        using var newer = new Rig();
        newer.Settings.Value.State.Pending = new PendingUpdate
        {
            TargetVersion = "1.1.0",
            FromVersion = "1.0.0",
            Attempts = 2,
        };
        newer.Start();
        newer.Updates.CompleteCheck(FakeUpdates.Offer("1.1.1"));
        newer.Pump();
        T.Eq(LauncherState.UpdateAvailable, newer.View.State, "a newer release gets a fresh start");
        newer.Press(LauncherCommand.Update);
        newer.Updates.CompleteDownload();
        newer.Pump();
        T.Check(
            newer.Settings.Value.State.Pending is { TargetVersion: "1.1.1", Attempts: 1 },
            "…with its own attempt counter"
        );
    }

    static void OrphanAndSecondInstance()
    {
        T.Section("Flow · orphaned game + second instance");
        using var r = new Rig();
        r.Games.Orphan = 4242;
        r.Updates.PendingVersion = "1.1.0"; // even with a package waiting…
        r.Start();
        T.Eq(LauncherState.GameAdopted, r.View.State, "a game left behind by a dead launcher is adopted");
        T.Eq(0, r.Updates.Checks.Count + r.Updates.Applies.Count, "…and NOTHING update-related happens while it lives");
        r.Press(LauncherCommand.SwitchToGame);
        T.Check(r.Host.Calls.Contains("focus:4242"), "SWITCH TO GAME brings it forward");
        r.Flow.OnSecondInstance();
        T.Eq(2, r.Host.Calls.Count(c => c == "focus:4242"), "a second launcher start focuses the game too");
        r.Games.OrphanExits();
        r.Pump();
        T.Eq(
            LauncherState.Applying,
            r.View.State,
            "once it exits, normal start-up resumes (here: the waiting package is installed)"
        );
        T.Eq(0, r.Updates.UpdateWorkWhileGameAlive, "…never before");

        using var idle = new Rig().Start();
        idle.Flow.OnSecondInstance();
        T.Check(idle.Host.Calls.Contains("show"), "second instance while idle → just show the window");

        using var playing = new Rig().Start();
        playing.Press(LauncherCommand.Play);
        playing.Flow.OnSecondInstance();
        T.Check(
            playing.Host.Calls.Contains("focus:1000"),
            "second instance while playing → focus the game, do nothing else"
        );
        T.Eq(0, playing.Updates.Applies.Count, "…certainly no apply (on Windows that would kill the match)");
    }

    static void AutoLaunch()
    {
        T.Section("Flow · auto-launch");
        using var r = new Rig();
        r.Settings.Value.Prefs.AutoLaunch = true;
        r.Start();
        r.Updates.CompleteCheck(null);
        r.Pump();
        T.Eq(3, r.View.CountdownSeconds, "auto-launch on + up to date → 3 s countdown");
        r.Tick(3);
        T.Eq(1, r.Games.Runs.Count, "…then the game starts");

        using var offer = new Rig();
        offer.Settings.Value.Prefs.AutoLaunch = true;
        offer.Start();
        offer.Updates.CompleteCheck(FakeUpdates.Offer("1.1.0"));
        offer.Pump();
        T.Eq(null, offer.View.CountdownSeconds, "an available update is never skipped by auto-launch");
        offer.Tick(10);
        T.Eq(0, offer.Games.Runs.Count, "…the player decides");

        using var suppressed = new Rig(["--launcher-no-autolaunch"]);
        suppressed.Settings.Value.Prefs.AutoLaunch = true;
        suppressed.Start();
        suppressed.Updates.CompleteCheck(null);
        suppressed.Pump();
        T.Eq(null, suppressed.View.CountdownSeconds, "--launcher-no-autolaunch suppresses it for one run");

        using var toggled = new Rig();
        toggled.Settings.Value.Prefs.AutoLaunch = true;
        toggled.Start();
        toggled.Updates.CompleteCheck(null);
        toggled.Pump();
        toggled.Flow.SetAutoLaunch(false);
        toggled.Pump();
        T.Check(
            toggled.View.CountdownSeconds is null && !toggled.Settings.Value.Prefs.AutoLaunch,
            "turning the toggle off cancels a running countdown and is saved"
        );

        using var offline = new Rig();
        offline.Settings.Value.Prefs.AutoLaunch = true;
        offline.Start();
        offline.Updates.FailCheck(new HttpRequestException("offline"));
        offline.Pump();
        T.Eq(3, offline.View.CountdownSeconds, "auto-launch also works when the check failed (offline play)");
    }

    static void BetaAndTranslocation()
    {
        T.Section("Flow · beta channel + macOS translocation");
        using var r = new Rig().Start();
        r.Updates.CompleteCheck(null);
        r.Pump();
        r.Flow.SetBetaChannel(true);
        r.Pump();
        T.Seq(
            [UpdateChannel.Stable, UpdateChannel.Beta],
            r.Updates.Checks,
            "switching to BETA re-checks on the beta channel right away"
        );
        T.Check(
            r.Settings.Value.Prefs.BetaChannel && r.Settings.Value.State.LastCheckUtc is null,
            "…and the cached stable answer is dropped"
        );
        r.Updates.CompleteCheck(null);
        r.Pump();
        T.Eq("v1.0.0 · BETA · test-rid", r.View.Footer, "the footer shows the channel");

        using var t = new Rig(
            os: HostOs.MacOs,
            launcherDir: "/private/var/folders/xx/T/AppTranslocation/ABCD/d/Stellar Allegiance.app/Contents/MacOS/"
        ).Start();
        T.Eq(
            LauncherState.UpToDate,
            t.View.State,
            "a translocated app (run from Downloads, quarantined) goes straight to READY"
        );
        T.Eq(0, t.Updates.Checks.Count, "…without checking: Velopack cannot replace a bundle on that read-only mount");
        T.Check(
            t.View.Alert is { Tone: Tone.Warn } alert && alert.Title.Contains("MOVE STELLAR ALLEGIANCE TO APPLICATIONS"),
            "…and tells the player how to fix it"
        );
        t.Press(LauncherCommand.Play);
        T.Eq(1, t.Games.Runs.Count, "PLAY still works");
        t.Games.Exit(LauncherContract.UpdateExitCode);
        t.Pump();
        T.Eq(
            0,
            t.Updates.Checks.Count + t.Updates.Downloads.Count,
            "even an in-game UPDATE NOW starts no update work there"
        );
    }

    static void TrackerAndCopy()
    {
        T.Section("DownloadPhaseTracker + copy");
        var full = new DownloadPhaseTracker(isDelta: false);
        T.Eq(
            new DownloadPhase(DownloadPhaseKind.ReceivingFull, 40, true),
            full.Update(40),
            "full package: progress passes through"
        );
        T.Check(!full.Update(100).Cancellable, "…and 100 is no longer cancellable");

        var delta = new DownloadPhaseTracker(isDelta: true);
        T.Eq(new DownloadPhase(DownloadPhaseKind.ReceivingPatch, 0, true), delta.Update(0), "delta: 0 → 0%");
        T.Eq(
            new DownloadPhase(DownloadPhaseKind.ReceivingPatch, 98, true),
            delta.Update(69),
            "delta: 69 → 98% of the patch download"
        );
        T.Eq(DownloadPhaseKind.Reconstructing, delta.Update(70).Kind, "delta: 70 → reconstructing");
        T.Eq(
            DownloadPhaseKind.Reconstructing,
            delta.Update(100).Kind,
            "delta: the final jump to 100 is still 'reconstructing → done', not a fallback"
        );
        var fallback = new DownloadPhaseTracker(isDelta: true);
        fallback.Update(70);
        T.Eq(
            new DownloadPhase(DownloadPhaseKind.FullFallback, 3, true),
            fallback.Update(3),
            "delta: dropping back below 70 = the full-package fallback"
        );
        T.Eq(DownloadPhaseKind.FullFallback, fallback.Update(80).Kind, "…and it stays a full download from then on");
        T.Eq(0, new DownloadPhaseTracker(true).Update(-5).Percent, "out-of-range values are clamped");

        T.Eq("950 KB", LauncherCopy.Bytes(950_000), "bytes: KB");
        T.Eq("48.2 MB", LauncherCopy.Bytes(48_200_000), "bytes: MB with one decimal");
        T.Eq("1.25 GB", LauncherCopy.Bytes(1_250_000_000), "bytes: GB with two decimals");
        T.Eq("12m 04s", LauncherCopy.Duration(TimeSpan.FromSeconds(724)), "duration: minutes");
        T.Eq("2h 05m", LauncherCopy.Duration(TimeSpan.FromMinutes(125)), "duration: hours");
        T.Eq(
            "https://github.com/wivuu/stellarallegiance/releases/tag/v1.2.3",
            LauncherCopy.ReleaseTagUrl("1.2.3"),
            "release URL is built from a fixed base"
        );
    }

    // The reason LauncherFlow exists: NO update may be downloaded or applied while a game is alive — on
    // Windows an apply kills every process under the install root. Throw random events at the flow (button
    // presses in any state, check/download completions and failures, game exits with every kind of code,
    // second-instance rings, timers, toggles) and assert the fakes never saw update work with a live game.
    static void InvariantFuzz()
    {
        T.Section("Flow · invariant fuzz");
        var random = new Random(20260919);
        var commands = Enum.GetValues<LauncherCommand>();
        int violations = 0,
            runs = 0,
            applies = 0,
            downloads = 0;
        for (int scenario = 0; scenario < 400; scenario++)
        {
            using var r = new Rig(alwaysCheck: random.Next(2) == 0);
            r.Settings.Value.Prefs.AutoLaunch = random.Next(3) == 0;
            if (random.Next(6) == 0)
                r.Games.Orphan = 7777;
            if (random.Next(5) == 0)
                r.Updates.PendingVersion = "1.1.0";
            if (random.Next(7) == 0)
                r.Settings.Value.State.Pending = new PendingUpdate
                {
                    TargetVersion = random.Next(2) == 0 ? "1.0.0" : "1.1.0",
                    FromVersion = "0.9.0",
                    Attempts = random.Next(1, 3),
                    ResumePlay = random.Next(2) == 0,
                };
            r.Start();

            for (int step = 0; step < 40 && r.Host.ExitCode is null; step++)
            {
                switch (random.Next(11))
                {
                    case 0:
                    case 1:
                    case 2:
                    {
                        // Mostly press what is actually on screen (so downloads and applies really happen),
                        // sometimes a command the view does NOT offer (which must be ignored).
                        var offered = new[] { r.View.Primary?.Command, r.View.Secondary?.Command }
                            .OfType<LauncherCommand>()
                            .ToArray();
                        r.Flow.Execute(
                            offered.Length > 0 && random.Next(4) != 0
                                ? offered[random.Next(offered.Length)]
                                : commands[random.Next(commands.Length)]
                        );
                        break;
                    }
                    case 3:
                        r.Updates.CompleteCheck(
                            random.Next(3) == 0
                                ? null
                                : FakeUpdates.Offer(random.Next(2) == 0 ? "1.1.0" : "1.2.0", random.Next(2) == 0)
                        );
                        break;
                    case 4:
                        r.Updates.FailCheck(new HttpRequestException("x"));
                        break;
                    case 5:
                        r.Updates.Progress(random.Next(101));
                        break;
                    case 6:
                        if (random.Next(4) == 0)
                            r.Updates.FailDownload(new IOException("x"));
                        else
                            r.Updates.CompleteDownload();
                        break;
                    case 7:
                        r.Games.Exit(
                            random.Next(4) switch
                            {
                                0 => 0,
                                1 => LauncherContract.UpdateExitCode,
                                2 => 139,
                                _ => 1,
                            }
                        );
                        break;
                    case 8:
                        r.Flow.OnSecondInstance();
                        break;
                    case 9:
                        r.Tick(random.Next(1, 6));
                        break;
                    case 10:
                        if (r.Games.Orphan is not null)
                            r.Games.OrphanExits();
                        else
                            r.Flow.SetBetaChannel(random.Next(2) == 0);
                        break;
                }
                r.Pump();
            }
            violations += r.Updates.UpdateWorkWhileGameAlive;
            runs += r.Games.Runs.Count;
            applies += r.Updates.Applies.Count;
            downloads += r.Updates.Downloads.Count;
        }
        T.Eq(
            0,
            violations,
            $"no download/apply ever happened while a game was alive (400 random scenarios: {runs} game runs, {downloads} downloads, {applies} applies)"
        );
        T.Check(
            runs > 100 && downloads > 50 && applies > 20,
            "…and the fuzz really did exercise games, downloads and applies"
        );
    }
}
