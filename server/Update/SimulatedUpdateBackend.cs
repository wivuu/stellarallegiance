namespace SimServer.Update;

// SIM_UPDATE_SIMULATE=<version> - DEV ONLY. Pretends that release is on the feed and that installing it
// works, so the whole player-facing path (Server Notice in the Game Lobby, the wait for an empty
// server, the drain that refuses joins, the exit with code 85) can be exercised from `dotnet run`
// without packaging anything. Nothing is downloaded or installed; under a supervisor that restarts on
// 85 the "new" build offers the same update again, forever - hence dev only.
public sealed class SimulatedUpdateBackend(string version) : IServerUpdateBackend
{
    public bool CanCheck => true;
    public bool CanApply => true;
    public string? CannotApplyReason => null;
    public string? PendingVersion => null;

    public Task<ServerUpdateOffer?> CheckAsync(CancellationToken ct) =>
        Task.FromResult<ServerUpdateOffer?>(new ServerUpdateOffer(version, DownloadBytes: 0, IsDelta: false));

    public Task DownloadAsync(ServerUpdateOffer offer, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(1), ct);

    public Task<bool> ApplyAsync(ServerUpdateOffer? offer, CancellationToken ct) => Task.FromResult(true);

    public void RelaunchAfterExit(string[] args) { }
}
