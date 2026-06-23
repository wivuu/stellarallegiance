// The client's release version — the thing the startup update check compares against the latest
// GitHub release tag (see UpdateChecker). CI stamps the constant from the git tag at release-build
// time (the "Stamp client version" step in .github/workflows/release.yml replaces the sentinel).
//
// Local/dev builds (and the manual build-godot-client.yml, which has no tag) keep the "-dev"
// sentinel; UpdateChecker treats any "dev" version as "don't nag" and stays silent.
//
// Keep the literal "0.0.0-dev" exactly as-is — the CI sed step matches on it.
public static class BuildInfo
{
    public const string Version = "0.0.0-dev";
}
