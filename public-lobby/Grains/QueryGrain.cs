using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Data;

namespace PublicLobby.Grains;

// Per-silo read side (ADR-0002): lookups by non-key columns and, later, aggregates/lists over a
// no-tracking context. Never writes. Point lookups here are uncached (they must observe a row a
// grain just wrote); list/aggregate methods added by later packages carry a short cache.
public interface IQueryGrain : IGrainWithIntegerKey
{
    Task<string?> FindDeviceCodeByUserCode(string userCode);
    Task<Guid?> FindPlayerIdByDisplayName(string displayName);
}

[StatelessWorker(1)]
public sealed class QueryGrain(IDbContextFactory<LobbyDbContext> dbFactory) : Grain, IQueryGrain
{
    public async Task<string?> FindDeviceCodeByUserCode(string userCode)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db
            .DeviceCodes.AsNoTracking()
            .Where(d => d.UserCode == userCode && d.Status == DeviceCodeStatus.Pending)
            .Select(d => d.Code)
            .SingleOrDefaultAsync();
    }

    public async Task<Guid?> FindPlayerIdByDisplayName(string displayName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // citext column: equality is case-insensitive on the database side.
        var id = await db
            .Players.AsNoTracking()
            .Where(p => p.DisplayName == displayName)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync();
        return id;
    }
}
