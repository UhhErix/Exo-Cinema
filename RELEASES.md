# Planned GitHub releases

The current 64-bit Windows 11 installer was verified against getexo.app.
Publish it as a [release asset](https://github.com/UhhErix/Exo-Cinema/releases), not in Git.

| App | Version | Release asset | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| Exo Cinema | 1.5.2 | `ExoCinema-1.5.2-Setup.exe` | 63,045,208 | `6e6f5742ca89bd3b068e4a7ad8297bd84f7bdc6bdb18266d7cd115e5bc9b1b53` |

Verify the installer with `Get-FileHash -Algorithm SHA256 <path>` in PowerShell.
The existing in-app update endpoint stays unchanged until a compatible cutover is verified.

## Discord announcements

Publish the matching version and its release notes to the Cloudflare update mirror before
publishing a stable `vMAJOR.MINOR.PATCH` release on GitHub. GitHub then calls the existing
Cloudflare announcer, which skips duplicate posts. If the mirror wasn't ready, rerun
the failed announcement job after publishing to Cloudflare.

Tag a commit that includes the announcement workflow. Publish through the GitHub UI
or use a GitHub App or personal access token in automation.
Publishing with the default Actions `GITHUB_TOKEN` does not trigger this workflow.
