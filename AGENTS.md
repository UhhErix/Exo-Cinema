# Exo Cinema — agent instructions

Exo Cinema plays the films and shows on your Jellyfin server in Exo's look, on Windows 11 x64: a WinUI 3 / .NET 10 host, libmpv drawing the picture, and one WebView2 page over it. [`docs/BUILDING.md`](docs/BUILDING.md) maps the source; read it before changing anything.

## Rules

- Jellyfin only: the library, its pictures and playback reports come from the one server you signed in to. No other sources, catalogs or picture hosts.
- Every place Exo Cinema connects passes `src/ExoCinema/Net/Egress.cs`, and each fixed destination is named in `PRIVACY.md` (`EgressTests` checks). A new host needs both.
- No secrets, tokens, server addresses or user names in commits. Test data stays synthetic (`SECURITY-SCRUB.md`).
- The look is Exo Launcher's: its [`DESIGN.md`](https://github.com/UhhErix/Exo-Launcher/blob/main/apps/launcher/DESIGN.md) (a map; the full rules are `apps/launcher/.impeccable/design-full.md`) and the `exo-design` skill in Exo-Launcher's `apps/launcher/.agents/skills/`. Words in the app are plain and short; no hint lines under controls.
- `libmpv-2.dll` is not in the repository; `lib/mpv/SOURCE.txt` names the build and its hash.

## Build and test

From the repository root (the .NET SDK is pinned in `global.json`):

```powershell
dotnet build src/ExoCinema/ExoCinema.csproj -c Release -p:Platform=x64
dotnet test --project tests/ExoCinema.Tests -c Release -p:Platform=x64
node --test tests/
```

`EXO_CINEMA_DATA` points a copy at its own data folder so it never touches an installed Exo Cinema; the tests set it themselves.

## Verify and ship

- Every CHANGELOG entry ends with how it was checked, in italics: seen on screen, which tests cover it, or code only. Unsure means code only.
- Playback needs a Jellyfin server and a real screen. If you did not play the change against a server, mark the entry as checked in tests or code only and say so in the PR.
- Releases: `tools/Build-Release.ps1` writes `artifacts\ExoCinema-Setup.exe`, its `.sha256` and `whats-new.json`; `RELEASES.md` records the version and checksum.

## Pull requests

One change per PR against `main`. Fill in the template: what changed and how it was tested, and say plainly what was not checked.

CI: `cinema-ci.yml` runs `node --test tests/*.test.mjs` and `dotnet test` on every PR and push to `main`; `announce-release.yml` posts a published release to Discord.

## Reviews

Focus on correctness, security and privacy (a new network destination, a secret, a server address or user name in a commit or test) and the rules above. Skip formatting the compiler or analyzers already enforce.
