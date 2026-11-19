# Source import security review

This repository imports the scrubbed Exo Cinema 1.5.1 tree without the original Git history.

Gitleaks flagged synthetic credential inputs and generated identity-vector fields:

- `tests/ExoCinema.Tests/CrashReportTests.cs`
- `tests/ExoCinema.Tests/LocalFileHostTests.cs`
- `tests/ExoCinema.Tests/IdentityVectorsTests.cs`
- `src/Exo.Identity/Vectors/exo-identity-vectors.json`

Generated fields use placeholders. The tests rebuild the canonical vectors and check the
original SHA-256. The canonical fixture uses LF on all platforms; a CRLF checkout caused
a scanner false positive on a synthetic public key.
