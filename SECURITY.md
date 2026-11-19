# Exo Cinema security

## Reporting a vulnerability

Report security problems privately through the form at <https://getexo.app/help>; say it is a security report. Don't post them in public. Say what you found, how to reproduce it and which version you used (**Settings > About**). You'll get an answer, and a fix ships in the next release with a line in the changelog.

## How Exo Cinema is put together

- **One process, your rights.** `ExoCinema.exe` runs as your Windows user (`asInvoker`) and installs per user; nothing runs as administrator or as a service.
- **The interface is Exo Cinema's own page.** It is served from the app folder (`app\wwwroot`) under its own address in WebView2, not from the web. It reaches your server only through the app: the page asks, and the app makes the request with your sign-in.
- **One place decides where it connects.** Every web request passes a checkpoint (`Net/Egress.cs`) for what it is for: updates, the Exo account, reports, your Jellyfin server, or a film's own link. The library and its pictures may reach only the server you signed in to; a film's link may lead anywhere on the web except Exo's own hosts. Nothing else is reachable, and a test fails if a new kind of request is added without being listed there and in PRIVACY.md.
- **Your sign-ins.** Exo Cinema signs in to your Jellyfin server with your user name and password once and keeps only the access token the server gives it, sealed with Windows' data protection (DPAPI) for your Windows user. The Exo account's session is sealed the same way. Neither password is ever stored, and neither token is written to a log or a report.
- **The player.** Video is decoded and drawn by libmpv in the app's own process, with hardware decoding where the graphics card offers it. Its key bindings and on-screen controller are off and it reads no configuration files: it plays only what Exo Cinema gives it.
- **Updates** come only from Exo's update address (`updates.getexo.app`), which serves Exo's own copy of each release. A downloaded Setup, or a changed file of a file-level update, must come from that host and match the SHA-256 published with the release (Setup must also carry Exo Cinema Setup's version details) before it is used. The new version is staged beside the old one and swapped in whole, and the old one is put back if anything fails.
