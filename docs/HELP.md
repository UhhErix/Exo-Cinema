# Getting started with Exo Cinema

## Install

1. Download **ExoCinema-Setup.exe** from [getexo.app/cinema](https://getexo.app/cinema) and check its SHA-256 against the one shown there:

   ```powershell
   Get-FileHash .\ExoCinema-Setup.exe -Algorithm SHA256
   ```

2. Run it and choose **Install**. **Options** adds a desktop shortcut. The installer isn't code-signed yet, so Windows SmartScreen may warn the first time: choose **More info**, then **Run anyway**.
3. Choose **Open Exo Cinema**.

Exo Cinema installs for your Windows user only, in `%LocalAppData%\ExoCinema\app`, with no administrator rights. It needs Windows 11 x64 and WebView2, which Windows 11 has; Setup installs WebView2 if it's missing.

## First run

- **Exo account:** sign in, or choose **Create an account**: one email and password for every Exo app. A new account shows its recovery code once; keep it, type it back and choose **Confirm**. If the account service can't be reached, **Continue offline** goes on without one and Exo Cinema asks again next time.
- **Jellyfin:** Exo Cinema plays the films and shows on your Jellyfin server. The server's address starts as `https://jlly.cc`; type your own server's address over it if you have one, then your username and password, and choose **Connect**. **Create an account**, **Set up AIOStreams** and **Get PublicMetaDB** open those sites in your browser. "Connected as you on your server" means it really worked, and that one sign-in is all it takes: your library is there from then on.

Leave the Jellyfin step without connecting and Home, Movies and Shows show the same sign-in until you do: there is no library without a server.
## Jellyfin

**Settings › Jellyfin**, and the sign-in that every room shows until you connect, sign you in to your server. If **Connect** can't, it says why:

| It says | What to do |
| --- | --- |
| Can't reach the address | Check the address and that the server is up. Use `https://` or `http://` if yours needs it. |
| That address isn't a Jellyfin server | Something else answered there. Check the address and any path after it. |
| Wrong username or password | Type them again. The password is cleared each time. |
| The server answered with an error | The server or what's in front of it has a problem. Try again, or look at the server. |

- **Connected,** Home, Movies, Shows and search are your server's. **Watched** and **Watchlist** on a title are the server's played and favorites; an episode's check mark shows as you point at its row.
- **Keeping in step:** where you left off, what's played and your favorites are on the server. Cinema reads them each time and reports what you play, so another Jellyfin app sees it, if that app also reads from and reports to the server. To check an app: watch a few minutes in it, and see whether Cinema's Continue Watching shows the place.
- **It plays the original file,** never a conversion, so it needs a connection that carries the file's bitrate. The chevron beside **Play** lists a title's versions when the server has more than one; **Play** picks the best one for this PC and connection.
- **"The stream host made this link for another connection":** your server keeps its films with a stream service that ties each link to one connection. Exo Cinema opens such links over IPv6 first, which is what lets them play; if your connection has no working IPv6 (some VPNs switch it off), turn the VPN off or switch IPv6 on and play again.
- **Not encrypted** beside your name means your server's address is plain `http://`, so anyone on the way can read what passes. Use `https://` if you can.
- **Sign out** ends the session on the server and forgets it here. Signed out by the server (a password change, say), Settings asks you to connect again.

## The library

- **Home:** Continue watching, Next up, Airing soon and your Watchlist, then the newest in each of your server's libraries. It draws at once from what Exo Cinema kept last time, then fills in.
- **Rows** page with the arrows beside their names.
- **Movies and Shows** in the Bar show one of your server's libraries at a time, as a grid that grows as you scroll. **Watchlist** (your server's favorites) and **History** (what you played on it, by day) join them once they hold something.
- **Titles from before 1.5:** if you used Exo Cinema when it played from other sources, what it kept then is still in Watchlist and History, each title by its name. Press one and Exo Cinema looks the name up on your server.
- **A title's page:** a show lists its seasons and episodes; **Watched** marks a film watched, **Watchlist** saves a title, **From the start** plays it from the beginning.
- **Search:** Ctrl+K (or /) searches your Jellyfin server as you type. Arrows pick a result, Enter opens it.
- **Back:** Backspace or your mouse's back button.

## The player

| Key | Does |
| --- | --- |
| Space or K | Play or pause |
| ← and → | Back or forward 5 seconds |
| J and L | Back or forward 10 seconds |
| ↑ and ↓ | Volume |
| M | Mute |
| F, or a double click | Full screen |
| I | What's playing and how (picture, decoder, video, frame rate) |
| Esc | Leave full screen, or go back |

- **Media keys:** while the player is open, your keyboard's play, pause and next keys work even with another window in front, and Windows' media controls (and Exo Launcher's Now Playing) show what's playing. Closing the player gives the keys back to your music.
- **The seek bar:** hover it for a picture of that moment, drawn on the spot from the video.
- **Audio, subtitles and speed:** the sliders button at the top right. A video starts on its best recording in the language you'd expect (Japanese for anime), with full subtitles in your language when the audio isn't, or only signs and songs for a dub. Each show remembers the languages you pick for its next episodes.
- **Skip intro** and **Skip credits** show while an opening or credits play, when the file names them in its chapters.
- **Up Next** shows near the end of an episode. With **Play next episode** on, the next one plays when the count runs out; **Not now** stops it.
- **The picture** is picked for your PC as it plays, with no setting: High quality (mpv's high-quality scaling) on a dedicated graphics card on mains power; Balanced on integrated graphics, on battery, and for the rest of a video that keeps dropping frames. Video is decoded on the graphics card, and in software when the card can't decode it. **I** shows which.
- **Anime upscaling:** on High quality, anime is redrawn by ArtCNN at up to 4K and shrunk back to your screen. Exo Cinema builds it for your graphics card once, in the background, and uses the driver's upscaler until then. **I** shows ArtCNN when it's on.
- **AI upscaling:** on High quality, an NVIDIA RTX card enlarges a film or show smaller than your screen with RTX Video Super Resolution, when that's on in Exo Launcher's PC > Display (or NVIDIA's own settings). Both step aside while a game runs.
- **HDR for films:** automatic, with no setting. A film or show with an HDR picture turns HDR on for the screen its window is on, when that screen can show HDR and has it off, and no game is running. HDR goes back off when the title ends, or when you close the player or Exo. When the next HDR title, such as the next episode, starts within about ten seconds of the last one ending, HDR stays on through the change so it doesn't flicker. A screen already in HDR, or one without it, is never touched. _Checked in code only._
- **Buffering:** the player reads ahead as far as memory allows and waits for a few seconds of picture before playing on. If it buffers often, `%LocalAppData%\ExoCinema\app.log` notes each wait with how fast the link was and how fast the video needs it.

## Settings

The gear at the top right. Five pages:

- **Account:** your Exo account. Signed out, sign in or create one. Signed in: **Sync now**, **Change password** (other PCs are signed out), **New recovery code** (the old one stops working), **Signed-in PCs**, **Export my data**, **Sign out** and **Delete account**.
- **Playback:** **Play next episode**.
- **Jellyfin:** who you're connected as, and **Sign out**; signed out, the sign-in.
- **Storage:** the pictures and seek-bar pictures kept on this PC, and **Clear** to empty them.
- **About:** your version, **Changelog**, **Check for updates**, and **Report a problem**, **Privacy** and **Licenses**. Crash, freeze and error reports are on by default and there is no switch for them; the privacy statement says what each one holds and how long it is kept.

Exo Cinema follows the look and **Reduce motion** set in Exo Launcher, when Launcher is installed.
## Updates

When a new version is out, Exo Cinema offers it as it opens: **Update available** shows the version, and **Update now** downloads it, checks it and installs it; Exo Cinema closes and opens again on the new version, with everything as you left it, and What's New shows what changed. It never interrupts while it's open: a version that comes out then is offered at the next open. If an update fails, the sheet says why, with **Copy logs** for a problem report, **Try again** and **Not now**. **Settings > About > Check for updates** looks at any time.

## Uninstall

Windows **Settings > Apps > Installed apps > Exo Cinema > Uninstall**. Your sign-ins and settings stay for a reinstall unless you choose **Also remove my sign-in and settings**.

## Something wrong?

[Ask for help or report a problem](https://getexo.app/help). Say what you were watching (the kind of file, not its name, is enough) and attach the end of `%LocalAppData%\ExoCinema\app.log`. Exo Cinema keeps your server's address, links, keys and titles out of it.

Exo Cinema also sends a crash, freeze or playback error report by default, with a few stripped log lines (the PRIVACY.md section "Crash, freeze and error reports" lists every field and what is removed). Such a report is never a usage record, and a crash is reported at the next start.
