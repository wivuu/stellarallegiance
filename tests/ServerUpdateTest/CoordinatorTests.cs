using SimServer.Update;
using StellarAllegiance.Shared.Net;
using static SimServer.Update.ServerUpdateCoordinator;

// ServerUpdateCoordinator, scenario by scenario, on fake time. Two halves: WHO RINGS THE DOORBELL (the
// lobby's Release Adverts, the retry ladder, the safety net) and WHAT HAPPENS THEN (notice → empty for
// the idle window → download → drain → apply → restart), including every way that can go wrong.
static class CoordinatorTests
{
    static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    // A server that has just been told about - and offered - `version`.
    static Rig Offered(string version = "1.1.0", bool playerConnected = false, UpdateRestart restart = UpdateRestart.Exit)
    {
        var rig = new Rig(restart: restart);
        if (playerConnected)
            rig.Gate.Join();
        rig.Backend.NextOffer = Rig.OfferOf(version);
        rig.Coordinator.OnLobbyAdvert(version);
        rig.Tick();
        return rig;
    }

    public static void Run()
    {
        Doorbells();
        SafetyNet();
        WarnMode();
        NoticeLifecycle();
        IdleWindowAndDownload();
        DrainAndApply();
        Failures();
        BootState();
    }

    static void Doorbells()
    {
        T.Section("doorbell: the lobby's Release Advert");
        var rig = Offered(playerConnected: true);
        T.Eq(1, rig.Backend.Checks, "an advert newer than this build asks the feed at once");
        T.Eq(UpdatePhase.Offered, rig.Coordinator.Phase, "the feed confirms it: offered");
        T.Check(rig.Log.Has("update-state Advert version=1.1.0"), "the advert is logged");
        T.Check(rig.Log.Has("update-state Available version=1.1.0 current=1.0.0"), "…and so is the offer");

        rig.Coordinator.OnLobbyAdvert("v1.1.0"); // the link dropped and came back: same advert again
        rig.Run(TimeSpan.FromSeconds(40));
        T.Eq(1, rig.Backend.Checks, "the same advert for a release already on offer does not ask again");

        var quiet = new Rig();
        quiet.Backend.NextOffer = Rig.OfferOf("1.1.0");
        quiet.Coordinator.OnLobbyAdvert("1.0.0");
        quiet.Tick();
        quiet.Coordinator.OnLobbyAdvert("0.9.0");
        quiet.Tick();
        quiet.Coordinator.OnLobbyAdvert("not a version");
        quiet.Run(TimeSpan.FromSeconds(30));
        T.Eq(0, quiet.Backend.Checks, "an advert that is not newer than this build (or not a version) rings nothing");
        T.Eq(UpdatePhase.Idle, quiet.Coordinator.Phase, "…and nothing is offered");

        T.Section("doorbell: the lobby was redeployed BEFORE the release finished publishing");
        var early = new Rig();
        early.Gate.Join(); // keep it in Offered once found, so the ladder is what is observed
        early.Coordinator.OnLobbyAdvert("1.1.0");
        early.Tick();
        T.Eq(1, early.Backend.Checks, "asked at once - the feed has nothing yet");
        (int AtSecond, int Checks)[] ladder = [(62, 2), (182, 3), (482, 4), (1082, 5), (1982, 6), (2882, 7)];
        int elapsed = 1;
        foreach (var (atSecond, checks) in ladder)
        {
            early.Run(TimeSpan.FromSeconds(atSecond - elapsed));
            elapsed = atSecond;
            T.Eq(checks, early.Backend.Checks, $"retry ladder 1/2/5/10/15/15 min: {checks} checks by t+{atSecond}s");
        }
        early.Backend.NextOffer = Rig.OfferOf("1.1.0");
        early.Run(TimeSpan.FromMinutes(16));
        T.Eq(8, early.Backend.Checks, "the release appears: the next retry finds it");
        T.Eq(UpdatePhase.Offered, early.Coordinator.Phase, "…offered");
        early.Run(TimeSpan.FromHours(2));
        T.Eq(8, early.Backend.Checks, "…and the ladder stops");

        T.Section("doorbell: a reconnect rings again");
        var again = new Rig();
        again.Coordinator.OnLobbyAdvert("1.1.0");
        again.Tick();
        again.Run(TimeSpan.FromSeconds(9));
        again.Coordinator.OnLobbyAdvert("1.1.0"); // t+10 s: the lobby link dropped and came back
        again.Tick();
        T.Eq(1, again.Backend.Checks, "a second advert inside 30 s is still ONE question (a burst of reconnects)");
        again.Run(TimeSpan.FromSeconds(25));
        T.Eq(
            2,
            again.Backend.Checks,
            "…asked once the spacing has passed: a reconnect is exactly when an update may be there"
        );

        T.Section("doorbell: mode off");
        var off = new Rig(AutoUpdateMode.Off);
        off.Backend.NextOffer = Rig.OfferOf("1.1.0");
        off.Coordinator.OnLobbyAdvert("1.1.0");
        off.Run(TimeSpan.FromHours(7));
        T.Eq(0, off.Backend.Checks, "off never asks - not on an advert, not on the safety net");
        T.Check(!off.Log.Has("update-state Available") && !off.Log.Has("update-state Warn"), "…and never says anything");
        T.Check(off.Log.Has("update-state Boot version=1.0.0 installed=True mode=off"), "…except the boot line");
    }

    static void SafetyNet()
    {
        T.Section("safety net: a server the lobby cannot reach");
        var unlisted = new Rig();
        unlisted.Gate.Join();
        unlisted.Backend.NextOffer = Rig.OfferOf("1.1.0");
        unlisted.Run(TimeSpan.FromSeconds(59));
        T.Eq(0, unlisted.Backend.Checks, "nothing for the first minute (a listed server hears from the lobby in that time)");
        unlisted.Run(TimeSpan.FromSeconds(2));
        T.Eq(1, unlisted.Backend.Checks, "no advert after 60 s: the safety net asks the feed itself");
        T.Eq(UpdatePhase.Offered, unlisted.Coordinator.Phase, "…and finds the release");

        var listed = new Rig(safetyNetSeconds: 100);
        listed.Coordinator.OnLobbyAdvert("1.0.0"); // the lobby is talking, nothing newer
        listed.Run(TimeSpan.FromSeconds(120));
        T.Eq(0, listed.Backend.Checks, "adverts are arriving: the tick is SKIPPED - the lobby does the watching");
        listed.Run(TimeSpan.FromSeconds(60));
        T.Eq(1, listed.Backend.Checks, "the lobby went quiet for a whole interval: the safety net is back");
        listed.Coordinator.OnLobbyAdvert("1.0.0");
        listed.Run(TimeSpan.FromSeconds(100));
        T.Eq(1, listed.Backend.Checks, "…and steps aside again as soon as adverts resume");

        var never = new Rig(safetyNetSeconds: 0);
        never.Backend.NextOffer = Rig.OfferOf("1.1.0");
        never.Run(TimeSpan.FromHours(2));
        T.Eq(0, never.Backend.Checks, "SIM_UPDATE_INTERVAL_SECONDS=0: no safety net at all");

        T.Section("safety net: following pre-releases (rehearsals)");
        var rehearsal = new Rig(prerelease: true, safetyNetSeconds: 100);
        rehearsal.Coordinator.OnLobbyAdvert("1.0.0"); // the lobby only ever advertises STABLE releases
        rehearsal.Tick();
        T.Eq(1, rehearsal.Backend.Checks, "every (re)connect advert rings, whatever its number says");
        rehearsal.Run(TimeSpan.FromSeconds(65));
        T.Eq(2, rehearsal.Backend.Checks, "…and the timer is never skipped");
    }

    static void WarnMode()
    {
        T.Section("warn: a source build is TOLD by the lobby and never asks anything itself");
        var source = new Rig(AutoUpdateMode.Warn, build: false);
        source.Backend.CanCheck = false;
        source.Backend.CanApply = false;
        source.Build();
        source.Coordinator.OnLobbyAdvert("1.1.0");
        source.Tick();
        T.Eq(
            1,
            source.Log.Count("update-state Warn version=1.1.0 current=1.0.0"),
            "the operator is warned from the advert alone"
        );
        T.Check(
            source.Log.Lines.Any(l =>
                l.Text.Contains("update-state Warn") && l.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            ),
            "…at Warning level"
        );
        source.Run(TimeSpan.FromHours(5));
        T.Eq(1, source.Log.Count("update-state Warn"), "…once, not every tick");
        source.Run(TimeSpan.FromHours(2));
        T.Eq(2, source.Log.Count("update-state Warn"), "…repeated every 6 h for as long as it stays behind");
        T.Eq(
            0,
            source.Backend.Checks + source.Backend.Downloads + source.Backend.Applies,
            "no feed request, no download, no apply - ever"
        );
        T.Eq(0, source.Gate.Notices.Count, "players are not told: this server does not want to restart");

        var unknown = new Rig(AutoUpdateMode.Warn, current: null, build: false);
        unknown.Backend.CanCheck = false;
        unknown.Backend.CanApply = false;
        unknown.Build();
        unknown.Coordinator.OnLobbyAdvert("9.9.9");
        unknown.Run(TimeSpan.FromHours(7));
        T.Check(
            !unknown.Log.Has("update-state Warn"),
            "a build that does not know its own version never warns (it cannot tell)"
        );
        T.Check(unknown.Log.Has("does not know its own version"), "…and says why, once, at boot");
        T.Check(
            unknown.Log.Has("update-state Boot version=unknown installed=False mode=warn"),
            "boot line: unknown / not installed / warn"
        );

        T.Section("warn: a packaged install in warn mode");
        var packaged = new Rig(AutoUpdateMode.Warn);
        packaged.Backend.NextOffer = Rig.OfferOf("1.1.0");
        packaged.Coordinator.OnLobbyAdvert("1.1.0");
        packaged.Run(TimeSpan.FromHours(2));
        T.Eq(1, packaged.Backend.Checks, "it asks its own feed once");
        T.Check(
            packaged.Log.Has("update-state Warn version=1.1.0") && packaged.Log.Has("SIM_AUTO_UPDATE=on"),
            "…warns, and says how to let it update itself"
        );
        T.Eq(0, packaged.Backend.Downloads, "…and downloads nothing, empty server or not");

        T.Section("warn: `on` without a packaged install degrades");
        var degraded = new Rig(AutoUpdateMode.On, build: false);
        degraded.Backend.CanCheck = false;
        degraded.Backend.CanApply = false;
        degraded.Backend.CannotApplyReason = "not a packaged install";
        degraded.Build();
        degraded.Coordinator.OnLobbyAdvert("1.1.0");
        degraded.Tick();
        T.Eq(AutoUpdateMode.Warn, degraded.Coordinator.EffectiveMode, "on → warn when this build cannot swap itself");
        T.Check(degraded.Log.Has("cannot update itself (not a packaged install)"), "…said once, with the reason");
        T.Check(
            degraded.Log.Has("update-state Boot version=1.0.0 installed=False mode=warn"),
            "…and the boot line shows the EFFECTIVE mode"
        );
        T.Check(degraded.Log.Has("update-state Warn version=1.1.0"), "it still warns");
        T.Eq(0, degraded.Gate.Notices.Count, "no player notice");
    }

    static void NoticeLifecycle()
    {
        T.Section("server notice");
        var rig = Offered(playerConnected: true);
        T.Eq<(byte, string)?>(
            (ServerNoticeMessage.KindUpdatePending, "1.1.0"),
            rig.Gate.StandingNotice,
            "offered: every player is told which release the server wants to restart onto"
        );
        T.Eq(1, rig.Gate.Announcements.Count, "…plus one chat line for the pilots in flight");
        T.Check(
            rig.Gate.Announcements[0].Contains("1.1.0") && rig.Gate.Announcements[0].Contains("everyone has left"),
            "…that says what and when"
        );
        rig.Run(TimeSpan.FromMinutes(30));
        T.Eq(1, rig.Gate.Notices.Count, "told ONCE - not every tick, not every check");
        T.Eq(1, rig.Gate.Announcements.Count, "…same for the chat line");

        rig.Backend.NextOffer = Rig.OfferOf("1.2.0");
        rig.Coordinator.OnLobbyAdvert("1.2.0");
        rig.Run(TimeSpan.FromSeconds(40));
        T.Eq<(byte, string)?>(
            (ServerNoticeMessage.KindUpdatePending, "1.2.0"),
            rig.Gate.StandingNotice,
            "a still newer release replaces the notice"
        );
        T.Eq(2, rig.Gate.Announcements.Count, "…and is announced");
        T.Eq("1.2.0", rig.Coordinator.Offer?.Version, "the older offer is dropped for it");
    }

    static void IdleWindowAndDownload()
    {
        T.Section("no update work while a player is connected");
        var rig = Offered(playerConnected: true);
        rig.Run(TimeSpan.FromMinutes(30));
        T.Eq(0, rig.Backend.Downloads + rig.Backend.Applies, "30 minutes with a player on: not even the download starts");
        T.Eq(1, rig.Log.Count("update-state Deferred players=1"), "deferred - logged once");

        rig.Gate.Leave();
        rig.Run(TimeSpan.FromSeconds(55));
        T.Eq(0, rig.Backend.Downloads, "empty for 55 s: still inside the idle window");
        rig.Run(TimeSpan.FromSeconds(10));
        T.Eq(1, rig.Backend.Downloads, "empty for the whole window: download");
        T.Eq(1, rig.Backend.Applies, "…then the swap");
        T.Seq([85], rig.Restarts, "…then exit 85 for the supervisor");
        T.Eq(UpdatePhase.Restarting, rig.Coordinator.Phase, "terminal: restarting");
        T.Eq(
            0,
            rig.Backend.Violations.Count,
            "invariant probe: no work with a player on, none inside the window, the swap behind a closed door"
        );

        T.Section("a pilot passes through while the server looks empty");
        var passing = Offered();
        passing.Run(TimeSpan.FromSeconds(30));
        passing.Gate.Join(); // in and out between two one-second looks
        passing.Gate.Leave();
        passing.Run(TimeSpan.FromSeconds(45));
        T.Eq(0, passing.Backend.Downloads, "a join+leave BETWEEN samples still resets the clock (AdmittedTotal)");
        passing.Run(TimeSpan.FromSeconds(20));
        T.Eq(1, passing.Backend.Downloads, "…and a full window later it proceeds");

        T.Section("a pilot joins during the download");
        var joining = Offered();
        joining.Backend.DuringDownload = () => joining.Gate.Join();
        joining.Run(TimeSpan.FromSeconds(70));
        T.Eq(1, joining.Backend.Downloads, "downloaded");
        T.Eq(0, joining.Backend.Applies, "…but NOT applied: somebody is on");
        T.Eq("1.1.0", joining.Backend.PendingVersion, "the package is kept");
        T.Check(joining.Log.Has("update-state Ready version=1.1.0"), "ready is logged");
        joining.Backend.DuringDownload = null;
        joining.Run(TimeSpan.FromMinutes(10));
        T.Eq(0, joining.Backend.Applies, "…for as long as they stay");
        joining.Gate.Leave();
        joining.Run(TimeSpan.FromSeconds(70));
        T.Eq(1, joining.Backend.Downloads, "after they leave: no second download");
        T.Eq(1, joining.Backend.Applies, "…a fresh idle window, then the swap");
        T.Eq(0, joining.Backend.Violations.Count, "invariant probe clean");
    }

    static void DrainAndApply()
    {
        T.Section("drain");
        var refused = Offered();
        refused.Run(TimeSpan.FromSeconds(58));
        refused.Gate.RefuseNextDrain = true; // a Hello slipped in: the hub says "somebody is here"
        refused.Run(TimeSpan.FromSeconds(10));
        T.Eq(1, refused.Backend.Downloads, "downloaded");
        T.Eq(0, refused.Backend.Applies, "the hub refused the drain: nothing is applied");
        T.Check(!refused.Gate.Draining, "…and the door is not left closed");
        refused.Run(TimeSpan.FromSeconds(70));
        T.Eq(1, refused.Backend.Applies, "a full idle window later it tries again and succeeds");

        var busy = Offered();
        busy.Gate.Quiescent = false; // the sim still has a match to wind down
        busy.Run(TimeSpan.FromSeconds(66));
        T.Check(busy.Gate.Draining, "draining: the door is closed while the sim goes quiet");
        T.Check(!busy.Gate.Join(), "…a Hello now is refused");
        T.Eq(0, busy.Backend.Applies, "…and nothing is applied yet");
        busy.Run(TimeSpan.FromSeconds(12));
        T.Check(
            !busy.Gate.Draining,
            "the sim never went idle: after 10 s the door REOPENS (refusing players forever is worse than updating late)"
        );
        T.Check(busy.Log.Has("update-state DrainAborted"), "…logged");
        T.Eq(UpdatePhase.Offered, busy.Coordinator.Phase, "back to offered");
        busy.Gate.Quiescent = true;
        busy.Run(TimeSpan.FromMinutes(3));
        T.Eq(1, busy.Backend.Applies, "…and it goes through once the sim is quiet");

        T.Section("restart");
        var exit = Offered();
        exit.Run(TimeSpan.FromSeconds(70));
        T.Seq(["download", "apply"], exit.Backend.Order, "restart=exit: download, apply - no Velopack relaunch");
        T.Seq([85], exit.Restarts, "…exit code 85");
        T.Check(exit.Gate.Draining, "the door STAYS closed: the next thing a player meets is the new build");
        T.Eq(1, exit.Attempts.Load()?.Attempts ?? 0, "the attempt marker is written BEFORE the swap");
        exit.Run(TimeSpan.FromMinutes(5));
        T.Seq([85], exit.Restarts, "restart is requested exactly once");
        T.Eq(1, exit.Backend.Applies, "…and nothing is applied twice");

        var relaunch = Offered(restart: UpdateRestart.Relaunch);
        relaunch.Run(TimeSpan.FromSeconds(70));
        T.Seq(
            ["download", "apply", "relaunch"],
            relaunch.Backend.Order,
            "restart=relaunch: Velopack is asked to start the new build BEFORE this one stops"
        );
        T.Seq([0], relaunch.Restarts, "…and this one exits 0");
        T.Eq(1, relaunch.Backend.Relaunches, "…exactly once");
    }

    static void Failures()
    {
        T.Section("failures");
        var flaky = Offered();
        flaky.Backend.CheckFails = new HttpRequestException("feed unreachable");
        flaky.Coordinator.OnLobbyAdvert("1.2.0");
        flaky.Run(TimeSpan.FromSeconds(40));
        T.Eq("1.1.0", flaky.Coordinator.Offer?.Version, "a failed check keeps the offer it already has");

        var noDownload = Offered();
        noDownload.Backend.DownloadFails = new IOException("disk full");
        noDownload.Run(TimeSpan.FromSeconds(70));
        T.Eq(1, noDownload.Backend.Downloads, "download failed");
        T.Check(noDownload.Log.Has("update-state DownloadFailed version=1.1.0 reason=disk full"), "…logged with the reason");
        noDownload.Run(TimeSpan.FromMinutes(4));
        T.Eq(1, noDownload.Backend.Downloads, "no hammering: it holds for 5 minutes");
        noDownload.Backend.DownloadFails = null;
        noDownload.Run(TimeSpan.FromMinutes(3));
        T.Eq(2, noDownload.Backend.Downloads, "…then tries again");
        T.Eq(1, noDownload.Backend.Applies, "…and goes through");

        var broken = Offered();
        broken.Backend.ApplySucceeds = false;
        broken.Run(TimeSpan.FromSeconds(70));
        T.Eq(1, broken.Backend.Applies, "the swap failed");
        T.Check(!broken.Gate.Draining, "…the door reopens");
        T.Eq(0, broken.Restarts.Count, "…no restart");
        T.Check(broken.Log.Has("update-state ApplyFailed version=1.1.0 attempt=1"), "…attempt 1 logged");
        T.Eq<(byte, string)?>(
            (ServerNoticeMessage.KindUpdatePending, "1.1.0"),
            broken.Gate.StandingNotice,
            "one failure: the notice stands"
        );
        broken.Run(TimeSpan.FromMinutes(7));
        T.Eq(2, broken.Backend.Applies, "second attempt after the hold");
        T.Eq("1.1.0", broken.Coordinator.SuspendedVersion, "two failures SUSPEND that release");
        T.Eq<(byte, string)?>(
            (ServerNoticeMessage.KindNone, ""),
            broken.Gate.StandingNotice,
            "…the notice is withdrawn: this server is not going to restart after all"
        );
        T.Check(broken.Log.Has("update-state Suspended version=1.1.0"), "…logged as an error");
        broken.Run(TimeSpan.FromHours(8));
        T.Eq(2, broken.Backend.Applies, "…and it is never tried again");
        T.Eq(1, broken.Backend.Downloads, "…nor downloaded again");

        broken.Backend.ApplySucceeds = true;
        broken.Backend.NextOffer = Rig.OfferOf("1.2.0");
        broken.Coordinator.OnLobbyAdvert("1.2.0");
        broken.Run(TimeSpan.FromMinutes(3));
        T.Seq([85], broken.Restarts, "a DIFFERENT release is not suspended: it installs");
        T.Eq(0, broken.Backend.Violations.Count, "invariant probe clean across all of it");
    }

    static void BootState()
    {
        T.Section("boot");
        var staged = new Rig(build: false);
        staged.Backend.PendingVersion = "1.1.0"; // an earlier run downloaded it and never got to apply
        staged.Build();
        staged.Tick();
        T.Eq(UpdatePhase.Offered, staged.Coordinator.Phase, "a package already on disk is on offer from the first tick");
        T.Eq<(byte, string)?>(
            (ServerNoticeMessage.KindUpdatePending, "1.1.0"),
            staged.Gate.StandingNotice,
            "…with its notice"
        );
        staged.Run(TimeSpan.FromSeconds(70));
        T.Eq(0, staged.Backend.Downloads, "…and is NOT downloaded again");
        T.Seq([85], staged.Restarts, "…just applied once the server has been empty for the window");

        var confirmed = new Rig(build: false);
        confirmed.Attempts.Save(new UpdateAttempt("v1.0.0", "0.9.0", 1));
        confirmed.Build();
        confirmed.Tick();
        T.Check(
            confirmed.Log.Has("update-state Confirmed version=v1.0.0 from=0.9.0"),
            "running the target of the last attempt = it worked"
        );
        T.Check(confirmed.Attempts.Load() is null, "…and the marker is cleared");

        var looping = new Rig(build: false);
        looping.Attempts.Save(new UpdateAttempt("1.1.0", "1.0.0", UpdateAttemptStore.MaxAttempts));
        looping.Build();
        looping.Backend.NextOffer = Rig.OfferOf("1.1.0");
        looping.Coordinator.OnLobbyAdvert("1.1.0");
        looping.Run(TimeSpan.FromHours(1));
        T.Eq(
            "1.1.0",
            looping.Coordinator.SuspendedVersion,
            "still on the old build after two attempts: suspended at boot, whatever the updater claimed"
        );
        T.Eq(
            0,
            looping.Backend.Applies + looping.Backend.Downloads,
            "…so a server whose supervisor keeps starting the OLD build does not restart itself forever"
        );
        T.Eq(0, looping.Gate.Notices.Count, "…and players are not told it wants to restart");

        var oneTry = new Rig(build: false);
        oneTry.Attempts.Save(new UpdateAttempt("1.1.0", "1.0.0", 1));
        oneTry.Build();
        oneTry.Backend.NextOffer = Rig.OfferOf("1.1.0");
        oneTry.Coordinator.OnLobbyAdvert("1.1.0");
        oneTry.Run(TimeSpan.FromSeconds(70));
        T.Eq(2, oneTry.Attempts.Load()?.Attempts ?? 0, "one earlier attempt: it may try once more, and counts it");
    }
}
