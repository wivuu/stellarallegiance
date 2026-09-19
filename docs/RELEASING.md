# Releasing

A release is a git tag. Pushing `vX.Y.Z` runs `.github/workflows/release.yml`, which publishes — per
desktop OS — a **Velopack package** (Game Launcher + Godot client): an installer, a self-updating
portable zip, and the update feed installed clients read. Installed copies then update **themselves**.
The sim-server image goes to GHCR in the same run.

```
prepare ──► package (linux | macos | windows) ──┐
        └─► server-image ───────────────────────┴─► publish
```

## Cutting a release

1. *(optional)* Create the GitHub release for the tag as a **draft** with a hand-written changelog. Its
   body becomes the launcher's **PATCH NOTES**; without it, GitHub's generated notes are used.
2. `git tag v0.0.13 && git push origin v0.0.13`
3. Wait for `publish`. Nothing is visible before it: the release stays a draft while the OS jobs run.

**Pre-releases:** a tag containing `-` (e.g. `v0.1.0-rc.1`) is published as a pre-release, does not move
the `:latest` image tag, and is offered only to players who set the launcher to the **BETA** channel.
Use **dotted** labels (`-rc.1`, not `-rc1`): SemVer compares `rc10` < `rc2` as text.

**Never reuse a version.** Velopack refuses to pack a version that is not greater than the newest one in
its output folder, and clients never "update" to the same version.

## What ends up on the release

| Asset | For |
|---|---|
| `StellarAllegiance-win-Setup.exe` | Windows install (per-user, no admin prompt, shortcuts, uninstall entry) |
| `StellarAllegiance-osx-Portable.zip` | **macOS install** — unzip, drag into Applications |
| `StellarAllegiance-osx-Setup.pkg` | macOS alternative. *Install for all users* (the Installer's default) writes the app **root-owned** into `/Applications`, so every later update asks for an admin password; *Install for me only* goes to `~/Applications` and avoids that. The zip sidesteps the choice — prefer it |
| `StellarAllegiance.AppImage` | Linux (replaces itself when it updates — keep it somewhere writable) |
| `StellarAllegiance-win-Portable.zip` | Windows without installing |
| `*-full.nupkg`, `*-delta.nupkg` | What updates download. Deltas are tiny (0.0.11→0.0.12: macOS 343 MiB → 9 MiB, Windows 282 MiB → 0.8 MiB) |
| `releases.<win\|osx\|linux>.json`, `assets.*.json`, `RELEASES` | The update feed |

The file names are stable, so `https://github.com/wivuu/stellarallegiance/releases/latest/download/<name>`
is a permanent download link.

## How clients update

The launcher reads the 10 newest releases through the GitHub API (one unauthenticated call per check —
60/hour/IP, failures are silent and never block PLAY), chains up to 10 deltas, verifies checksums, and
falls back to the full package if any delta is missing or fails. A fresh macOS/Linux install has no cached
base package, so its **first** update is a full download; Windows' `Setup.exe` seeds the cache.

Things that would quietly break updating — the workflow asserts all of them:

- **`zstd` must be installed** on the macOS/Linux build machine. Without it `vpk` silently writes *bsdiff*
  deltas, which the 1.2.0 updater rejects → every update becomes a full download.
- **No single file ≥ 1 GiB** in a package (delta generation hard-fails). On Linux the package *is* one
  file — the whole AppImage — so that limit applies to the full image there.
- **`vpk` and the `Velopack` NuGet must be the same version** (root `dotnet-tools.json` ↔
  `launcher/Core/StellarLauncher.Core.csproj`).
- A launcher that cannot update itself strands every player on that version **forever** — which is why
  `scripts/launcher-e2e.ps1` runs in every `package` job before anything is packed.

## Locally

```sh
scripts/launcher-e2e.ps1                                  # prove install → update → play on this machine
scripts/launcher-e2e.ps1 -Versions 0.0.13-ci.1, 0.0.13-ci.2, 0.0.13   # …with a release candidate's numbering
scripts/package-clients.ps1 -Version 0.0.13                # this OS's real package → build/releases/<channel>
scripts/package-clients.ps1 -Version 0.0.13 -FakeGame       # …with the stub game (seconds instead of minutes)
```

Host-OS only: macOS packages need `codesign`/`pkgbuild`, and the launcher is NativeAOT on Windows + macOS
(no cross-OS AOT). Before touching `launcher/`, the package script or the Velopack version, run the
**Package dry-run** workflow — it runs on any pull request that touches those paths (and by hand once it
is on the default branch), runs the e2e on all three OSes, publishes nothing,
and is the only way Windows gets verified without a Windows machine.

## Rehearsing the pipeline

The dry-run packs a stub game and never talks to GitHub Releases, so it cannot prove the Godot export, the
upload or a real update from GitHub. After changing `release.yml`, the package script's export path or the
Velopack version, rehearse with throw-away **pre-release** tags. A tag runs the workflow file of the commit
it points at, so this works from a branch — nothing has to be merged first.

```sh
git tag v0.0.13-ci.1 && git push origin v0.0.13-ci.1   # export + package + upload + publish, no delta yet
# install it, set the launcher to BETA (pre-releases are hidden on STABLE), play once, then:
git tag v0.0.13-ci.2 && git push origin v0.0.13-ci.2   # delta-base download, a real delta, a real update from GitHub
```

Stable players never see any of it: old zip clients read `/releases/latest`, the launcher's STABLE channel
filters pre-releases out, and the `:latest` image tag does not move. A machine left on a rehearsal build
updates to the real `0.0.13` by itself (`0.0.13-ci.2` < `0.0.13`) — that exact sequence is what
`scripts/launcher-e2e.ps1 -Versions 0.0.13-ci.1, 0.0.13-ci.2, 0.0.13` runs locally.

Clean up afterwards, so the first real release starts from an empty feed:

```sh
gh release delete v0.0.13-ci.1 --cleanup-tag -y
gh release delete v0.0.13-ci.2 --cleanup-tag -y
git tag -d v0.0.13-ci.1 v0.0.13-ci.2
```

(The run also pushes `stellarallegiance-sim:0.0.13-ci.N` images to GHCR; delete those package versions in
the GitHub UI if they bother you.)

## If a release run fails

Fix forward and **re-run the failed jobs** — `publish` first deletes whatever assets a previous attempt
uploaded. If a bad release did get published: delete the release (players on it keep playing; the launcher
just stops offering it) and tag the next patch version. Do not re-tag the same version.

## Signing (optional — switched on by secrets, no workflow edits)

| Secret | Effect |
|---|---|
| `MAC_APP_IDENTITY` | Developer ID Application identity → hardened-runtime signing of launcher **and** nested game (`launcher/macos/*.entitlements`). Default `-` = ad-hoc. |
| `MAC_INSTALL_IDENTITY`, `MAC_NOTARY_PROFILE` | Signed `.pkg` + notarization (needs a keychain/notarytool profile on the runner). |
| `AZURE_TRUSTED_SIGN_FILE` or `WIN_SIGN_PARAMS` | Windows signing (the game's ~200 .NET runtime dlls are excluded via `--signExclude`). |

Today's builds are ad-hoc (macOS) / unsigned (Windows): the **first** install needs the quarantine
(`xattr -dr com.apple.quarantine …`) or SmartScreen (*More info → Run anyway*) step; updates after that
are silent, because files written by the updater are never quarantined.
