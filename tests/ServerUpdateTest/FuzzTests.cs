using SimServer.Update;
using static SimServer.Update.ServerUpdateCoordinator;

// THE invariant, under random timelines: no update WORK - download or apply - ever starts while a player
// is connected or before the server has been empty for the whole idle window, and the package is only
// ever swapped in behind a closed front door. The probe lives in FakeBackend (it records a violation at
// the moment work STARTS); this throws joins, leaves, adverts, feed answers, failures, a busy sim and
// clock jumps at the coordinator and then reads the probe. Same idea as the Game Launcher's
// "no update work while the game is alive" fuzz (tests/LauncherTest/FlowTests.cs InvariantFuzz).
static class FuzzTests
{
    public static void Run()
    {
        T.Section("fuzz: no update work while a player is connected");
        const int Seeds = 400;
        int violations = 0,
            downloads = 0,
            applies = 0,
            restarts = 0,
            deferred = 0,
            refusedJoins = 0,
            doorLeftClosed = 0,
            failedApplies = 0;
        string? firstViolation = null;

        for (int seed = 0; seed < Seeds; seed++)
        {
            var random = new Random(seed);
            var rig = new Rig(idleSeconds: 20, safetyNetSeconds: random.Next(2) == 0 ? 0 : 90);
            string[] versions = ["1.0.0", "1.1.0", "1.2.0", "0.9.0", "junk"];

            for (int step = 0; step < 260 && rig.Coordinator.Phase != UpdatePhase.Restarting; step++)
            {
                switch (random.Next(20))
                {
                    case < 7:
                        rig.Run(TimeSpan.FromSeconds(random.Next(1, 12)));
                        break;
                    case < 9:
                        rig.Run(TimeSpan.FromSeconds(random.Next(25, 140))); // long enough for a window to pass
                        break;
                    case < 12:
                        if (!rig.Gate.Join())
                            refusedJoins++;
                        break;
                    case < 15:
                        rig.Gate.Leave();
                        break;
                    case < 17:
                        rig.Coordinator.OnLobbyAdvert(versions[random.Next(versions.Length)]);
                        break;
                    case 17:
                        rig.Backend.NextOffer = random.Next(4) switch
                        {
                            0 => null,
                            1 => Rig.OfferOf("1.2.0", delta: true),
                            _ => Rig.OfferOf("1.1.0"),
                        };
                        break;
                    case 18:
                        rig.Gate.Quiescent = random.Next(4) != 0;
                        rig.Backend.ApplySucceeds = random.Next(5) != 0;
                        rig.Backend.CheckFails = random.Next(6) == 0 ? new HttpRequestException("blip") : null;
                        rig.Backend.DownloadFails = random.Next(8) == 0 ? new IOException("blip") : null;
                        break;
                    default:
                        // A pilot shows up exactly while the package is coming down.
                        rig.Backend.DuringDownload = random.Next(2) == 0 ? () => rig.Gate.Join() : null;
                        break;
                }
            }

            violations += rig.Backend.Violations.Count;
            firstViolation ??= rig.Backend.Violations.FirstOrDefault() is { } v ? $"seed {seed}: {v}" : null;
            downloads += rig.Backend.Downloads;
            applies += rig.Backend.Applies;
            restarts += rig.Restarts.Count;
            deferred += rig.Log.Count("update-state Deferred");
            failedApplies += rig.Log.Count("update-state ApplyFailed");
            // The door may only stay closed when the server is actually restarting (or mid-drain).
            if (rig.Gate.Draining && rig.Coordinator.Phase is not (UpdatePhase.Restarting or UpdatePhase.Draining))
                doorLeftClosed++;
            if (rig.Restarts.Count > 1)
                violations++;
        }

        T.Check(
            violations == 0,
            $"no update work with a player connected, inside the idle window, or with the door open ({firstViolation ?? "0 violations"})"
        );
        T.Eq(0, doorLeftClosed, "the front door is never left closed outside a drain / restart");
        T.Check(downloads > 100 && applies > 100, $"the run really downloaded ({downloads}) and applied ({applies})");
        T.Check(restarts > 50, $"…and really restarted ({restarts} of {Seeds} timelines)");
        T.Check(deferred > 50, $"…and really deferred to players ({deferred} times)");
        T.Check(
            failedApplies > 5 && refusedJoins > 0,
            $"…and really hit failures ({failedApplies}) and joins refused mid-drain ({refusedJoins})"
        );
    }
}
