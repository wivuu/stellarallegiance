using SpacetimeDB;

// =====================================================================
//  Bases.cs — the Base table + base content (Phase-1 M2, .PLAN/CONFIG.md)
//
//  Base radius and max health now come from the BaseDef row instead of the
//  BaseRadius / BaseMaxHealth constants. There is a single base type this phase, so
//  every Base maps to BaseTypeId 0 (per-base types are a later phase — the Base row
//  doesn't yet carry a type id); BaseRadiusOf / BaseMaxHealthOf read that def and
//  fall back to the compiled-in constants if it isn't seeded. SeedDefaults seeded the
//  def from those same constants, so a fresh DB is bit-identical while an operator can
//  retune the base at runtime (UpsertBaseDef) — a bumped MaxHealth takes effect on the
//  next match restart (ResetWorld reads the def). Joins the existing partial Module class.
// =====================================================================

[SpacetimeDB.Table(Accessor = "Base", Public = true)]
public partial struct Base
{
    [PrimaryKey]
    [AutoInc]
    public ulong BaseId;
    public byte Team;
    public uint SectorId;       // which sector this base sits in
    public float PosX;
    public float PosY;
    public float PosZ;
    public float Health;        // <= 0 => base destroyed => match ends
}

public static partial class Module
{
    // The single base type this phase. BaseDef is keyed by BaseTypeId for future per-base
    // types; until the Base row carries one, every base resolves to this def.
    private const byte DefaultBaseTypeId = 0;

    // Collision/render radius for a base, from its BaseDef (falls back to the constant).
    private static float BaseRadiusOf(ReducerContext ctx)
        => ctx.Db.BaseDef.BaseTypeId.Find(DefaultBaseTypeId) is BaseDef d ? d.Radius : BaseRadius;

    // Full/restored hull for a base, from its BaseDef (falls back to the constant).
    private static float BaseMaxHealthOf(ReducerContext ctx)
        => ctx.Db.BaseDef.BaseTypeId.Find(DefaultBaseTypeId) is BaseDef d ? d.MaxHealth : BaseMaxHealth;

    // Seed the two team bases at opposite ends of the Core sector. Positions (±500) are
    // authored map layout (not world-scale — see .PLAN/CONFIG.md); starting hull comes from
    // the BaseDef (seeded just before this in Init). Called from Init.
    private static void SeedBases(ReducerContext ctx)
    {
        float hp = BaseMaxHealthOf(ctx);
        ctx.Db.Base.Insert(new Base { BaseId = 0, Team = 0, SectorId = HomeSector, PosX = -800f, PosY = 0f, PosZ = 0f, Health = hp });
        ctx.Db.Base.Insert(new Base { BaseId = 0, Team = 1, SectorId = HomeSector, PosX = 800f, PosY = 0f, PosZ = 0f, Health = hp });
    }

    // Apply this tick's accumulated base damage. A base reaching 0 health ends the match,
    // the winner being the OTHER team — the side that destroyed the enemy base. Once Ended
    // we never reopen the match (SpawnShip already refuses in the Ended phase). Operates on
    // the post-integration base snapshot + the per-tick baseId->damage accumulator.
    private static void ApplyBaseDamage(ReducerContext ctx, List<Base> bases, Dictionary<ulong, float> baseDamage)
    {
        foreach (var b in bases)
        {
            if (!baseDamage.TryGetValue(b.BaseId, out var bd))
                continue;

            float hp = MathF.Max(0f, b.Health - bd);
            ctx.Db.Base.BaseId.Update(b with { Health = hp });

            if (hp <= 0f)
            {
                var m = ctx.Db.Match.Id.Find(0);
                if (m is Match mm && mm.Phase != MatchPhase.Ended)
                {
                    byte winner = (byte)(b.Team == 0 ? 1 : 0);
                    ctx.Db.Match.Id.Update(mm with { Phase = MatchPhase.Ended, Winner = winner });
                    Log.Info($"[Match] base {b.BaseId} (team {b.Team}) destroyed -> team {winner} wins");
                }
            }
        }
    }
}
