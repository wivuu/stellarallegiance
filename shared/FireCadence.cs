namespace StellarAllegiance.Shared;

// Per-mount gun cadence — THE single eligibility rule for "does weapon mount i fire at tick T".
// Three mirrors consume it and must never drift (same pattern as FlightModel.SpreadDirection):
//   - server Simulation.TryFire        (authoritative: fires the bolt, stamps MountLastFire)
//   - client PredictionController      (local ship: predicts the same bolts + stamps its shadow)
//   - client WorldRenderer.SpawnBoltFor (remote ships: derives WHICH mounts fired at an observed
//     LastFireTick from a per-ship shadow of last-fire ticks — the wire carries no per-mount data)
// The rule is input-independent at an observed fire tick: the event itself proves firing was held,
// so eligibility depends only on (tick, mount's last fire tick, mount's interval). A shadow that
// sees every LastFireTick change stays in lockstep with the server; a lossy far-tier shadow drifts
// and self-corrects — visual only.
public static class FireCadence
{
    // lastFireTick == 0 means "never fired" and is always eligible (mirrors the pre-loadout
    // single-gate rule; tick 0 itself is pre-match, no ship fires on it).
    public static bool MountFires(uint tick, uint lastFireTick, uint intervalTicks) =>
        lastFireTick == 0 || tick - lastFireTick >= intervalTicks;

    // Cargo-fed launchers/dispensers (missile rack, chaff/mine/probe) also have to LOAD the next
    // charge out of the hold, which takes the expendable's authored load time. The two gates are ONE
    // window, not two: whichever is longer decides when the slot is usable again — so an authored
    // reload-ticks of 0 is exactly the legacy cadence-only behavior, and an author can never
    // double-count a load against a cadence. Mirrors (must never drift):
    //   - server Simulation.TryFireMissile / TryDropChaff / TryDeployMine / TryDeployProbe (authority)
    //   - client WeaponsPanel (the RELOADING / CYCLE readout and its progress bar)
    public static uint LoadIntervalTicks(uint fireIntervalTicks, uint reloadTicks) =>
        reloadTicks > fireIntervalTicks ? reloadTicks : fireIntervalTicks;
}
