# tailwind

A download cache, not a tool you run. `public-lobby/PublicLobby.csproj`'s `EnsureTailwindCss`
target fetches the pinned standalone Tailwind CSS CLI (v4.3.3, the asset matching the current
OS/CPU) into this folder before every lobby build, `chmod +x`s it on unix, and runs it as
`-i Styles/app.css -o wwwroot/app.css --minify`. No Node or npm involved.

Output lands in `public-lobby/wwwroot/app.css` (gitignored). The downloaded binaries here are
gitignored too — only this README is tracked; delete a binary to force a re-download, and set
`TailwindAssetName` if you are on an OS/CPU the target does not recognise.
