// The client's release version — the thing the startup update check compares against the latest
// GitHub release tag (see UpdateChecker). scripts/package-clients.ps1 stamps the constant with the
// release version for the duration of the Godot export (Export-Game) and restores it afterwards.
//
// Local/dev builds (and the tester zips from scripts/export-clients.ps1, which have no tag) keep the
// "-dev" sentinel; UpdateChecker treats any "dev" version as "don't nag" and stays silent.
//
// Keep the literal "0.0.0-dev" exactly as-is — the package script matches on it.
public static class BuildInfo
{
    public const string Version = "0.0.0-dev";
}
