using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  AssetVerify.cs — `--verify-assets[=<report file>]`: does THIS BUILD carry what the game needs to
//  render and collide with every model it can be asked to field? Answers with an exit code, a marker
//  line and (optionally) a report file, then quits — before any menu, account or network code matters.
//
//      <game> --headless --verify-assets=/tmp/report.txt        exit 0 = OK, 1 = FAIL
//
//  It exists for the packaging scripts (scripts/verify-game-assets.ps1, called by package-clients.ps1 and
//  export-clients.ps1): they run the EXPORTED game with it and refuse to package a build that fails. A
//  check of the real loader inside the real artifact, because that is the only place the failure shows:
//  the project runs fine from source (res:// is the project folder, every raw .glb is on disk) while the
//  same code inside a package finds nothing to read. See CollisionModels for what that costs in play.
//
//  The REPORT FILE is the verdict the scripts trust, not the exit code: headless Godot .NET has been
//  seen to die in shutdown (exit 139) after doing its job, and a gate must not confuse that with a
//  failed — or a passed — check. First line is always `ASSET_VERIFY: OK …` or `ASSET_VERIFY: FAIL …`.
//
//  A game flag, so it goes BEFORE any `--` (OS.GetCmdlineArgs; see the "client CLI flags" convention).
// =====================================================================
public static class AssetVerify
{
    public const string Flag = "--verify-assets";
    public const string Marker = "ASSET_VERIFY:";

    private static readonly (bool Requested, string? ReportPath) _request = Parse();

    // True when this process was started to verify its assets and exit. Nodes that would otherwise
    // reach out at boot (AuthSession's token refresh) stand down when it is.
    public static bool Requested => _request.Requested;

    private static (bool, string?) Parse()
    {
        foreach (string a in OS.GetCmdlineArgs())
        {
            if (a == Flag)
                return (true, null);
            if (a.StartsWith(Flag + "="))
                return (true, a.Substring(Flag.Length + 1));
        }
        return (false, null);
    }

    // Build every model synchronously through the SAME loader gameplay uses, and judge the result.
    // `bases` and `ships` are what the asset folders list; `asteroids` is the wire catalog (every
    // variant the server can roll). Returns the process exit code.
    public static int Run(IReadOnlyList<string> bases, IReadOnlyList<string> ships, IReadOnlyList<string> asteroids)
    {
        var failures = new List<string>();
        int ok = 0;

        // An EMPTY list is a failure of its own: a package that dropped a whole folder has nothing to
        // fault on, and would otherwise pass with flying colours.
        if (bases.Count == 0)
            failures.Add("res://assets/bases: no base models are listed in this build");
        if (ships.Count == 0)
            failures.Add("res://assets/ships: no ship models are listed in this build");

        void Check(string path, Quat pre)
        {
            // The scene first: an un-imported GLB exports "successfully" and then renders as a
            // procedural placeholder — the other way a model goes missing without a sound.
            if (!ResourceLoader.Exists(path))
            {
                failures.Add($"{path}: no imported scene (the GLB was not imported before the export)");
                return;
            }
            if (CollisionModels.Load(path, pre) is null)
                return; // recorded in the ledger with its reason; collected below
            ok++;
        }

        foreach (string p in bases)
            Check(p, CollisionConfig.BaseModelRotation);
        foreach (string p in ships)
            Check(p, default);
        foreach (string p in asteroids)
            Check(p, default);

        foreach (var f in CollisionModels.Faults)
            failures.Add($"{f.Path}: no collision model — {f.Reason}");

        int total = bases.Count + ships.Count + asteroids.Count;
        var lines = new List<string>
        {
            failures.Count == 0
                ? $"{Marker} OK {ok}/{total} models render and collide ({bases.Count} bases, {ships.Count} ships, {asteroids.Count} asteroid variants; "
                    + $"{CollisionModels.FromSidecarCount} from collision sidecars, {CollisionModels.BuiltCount - CollisionModels.FromSidecarCount} from raw .glb)"
                : $"{Marker} FAIL {ok}/{total} models render and collide — {failures.Count} problem(s)",
        };
        foreach (string f in failures)
            lines.Add($"FAIL {f}");

        // Raw GD.Print, not Log.Print: the scripts match the marker at the START of a line.
        foreach (string line in lines)
            GD.Print(line);

        if (_request.ReportPath is { Length: > 0 } report)
            try
            {
                System.IO.File.WriteAllLines(report, lines);
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"{Marker} could not write the report to '{report}': {e.Message}");
                return 2; // the caller asked for a report and has none — never a pass
            }
        return failures.Count == 0 ? 0 : 1;
    }
}
