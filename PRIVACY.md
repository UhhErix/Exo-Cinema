# Exo Cinema privacy statement

**Exo Cinema plays the films and shows on your Jellyfin server. It has no ads and no usage tracking. It sends diagnostic reports when something breaks, on by default; the section "Crash, freeze and error reports" says exactly what each one holds. One Exo account, an email and a password, works in every Exo app; the first run asks for it.**

Exo's servers that Exo Cinema talks to are its update address, which tells it when a new version is out, the report service, and the Exo account service. What you watch is between this PC and your Jellyfin server: none of it is sent to Exo or its developer.

## What stays on this PC

Exo Cinema keeps its data in `%LocalAppData%\ExoCinema`, beside the app (in `app`), never inside it, so updates never touch it:

- `settings.json`: whether the first run is done, the last version whose What's New you saw, and whether the account sheet was shown;
- `hdr-films.json`: only while Exo Cinema has switched a screen's HDR on for a film, that screen's device name, so the next start can switch HDR back off if this run ended before it could. It is removed once HDR is back off;
- the interface's own storage (`WebView2`): your choices in Settings, the volume, the languages you picked for each show, and a copy of Home and the title pages you opened, so they draw at once next time;
- the pictures of films and shows from your server (`cache\images`), so Home draws at once, and the seek bar's pictures of the videos you played (`cache\scrub`, 256 MB at most), so they show at once next time. **Settings > Storage > Clear** empties both;
- the player's shaders built for your graphics card (`cache\shaders`), so it doesn't build them again at every start, and the poster of what's playing (`cache\now-playing-…jpg`) for Windows' media controls, replaced with each title;
- `link.txt`: how fast films have come in on your connection, one number, so Play picks a version your connection carries smoothly;
- `jellyfin.json`: your server's address and name, your user name and ID, an ID for this PC, and the key the server hands back, sealed with Windows' own protection for your Windows user. Your password is never kept. None of it goes to your Exo account;
- `sync\cinema.dat`: the choices your Exo account carries to your other PCs (Play next episode, and the audio and subtitle languages you pick for films), sealed the same way. The languages you pick for a show on your server stay in the interface's own storage, on this PC;
- `watch\ledger.dat`, only on a PC that used Exo Cinema before 1.5: Exo Watch, what you watched then and what you saved, by each title's IMDb or TMDB ID and its name. See "What Exo Cinema kept before 1.5" below;
- `account-session.bin` and `account-sync.json`, if you sign in to your Exo account: the sealed session, and the list of items waiting to sync;
- `app.log`, `app.prev.log`, `crash.log` and `window-placement.json`: what Exo Cinema did (timings, buffering, errors, answers the server didn't give), what went wrong if it crashed, and where its window was. Each run starts a new `app.log`; the run before is kept as `app.prev.log`, and each file stops at 512 KB. `app.log` never holds a web address, a key, your Jellyfin server's address, a title or a file name: each address is cut to its kind of file (`https://…/*.mkv`), and a file name is replaced by `<file>`;
- `reports` and `reports-state.json`: diagnostic reports waiting to be sent, and how many were sent today. A report is deleted once it is sent, or once it is dropped.
- While the player is open, the title, the show and the poster of what's playing go to Windows' media controls on this PC, which show them in Windows' media flyouts and to apps that read them, such as Exo Launcher. They don't leave the PC; closing the player clears them.
- WebView2's form memory is off: nothing you type into Exo Cinema (your passwords above all) is kept by it.

Uninstalling with **Also remove my sign-in and settings** deletes the whole folder; without it, the folder stays for a reinstall.

## What leaves your PC

### Your Jellyfin server

Exo Cinema's films and shows come from the Jellyfin server you sign in to, and from nowhere else. Until you sign in there is no library: every room shows the sign-in.

The sign-in's server field starts as `https://jlly.cc`, filled in and editable; type your own server's address over it if you have one. Nothing is sent to any server, that one included, until you choose **Connect**. **Connect** sends your username and password to the address in the field, and to nowhere else, over the connection that address uses: an `http://` address that isn't on this PC or your own network is not encrypted, and Settings says so. An address typed without `https://` or `http://` is tried over https, and over http only if it's on your own network.

Connected, Exo Cinema asks that server, and no other, for your libraries, Continue Watching, Next Up, Airing soon, your favorites (the Watchlist), what you played (History), each title and episode you open, what you search for, and every picture it shows. It tells the server what you play and where you are in it, at its start, every 30 seconds, on a seek, on pause and at its stop, and what you mark watched or favorite, with this PC's name and an ID for it, which the server shows among its devices. That record is the server's, under its owner's rules, and other apps on your account can read it.

It plays the original file from the server. When the server keeps a file somewhere else, it plays from the address the server gives, without your key; that host sees your IP address, as any website does. A look at such a link, and the player's own connection to it, go over IPv6 first where your connection has it. **Sign out** ends the session on the server and forgets the sign-in here.

**Create an account**, **Set up AIOStreams** and **Get PublicMetaDB** on the first run's Jellyfin step open jlly.cc, AIOStreams' set-up page and publicmetadb.com in your default browser. Exo Cinema sends nothing to them and stores nothing from them.

### Your Exo account

One email and password account works in Exo Launcher, Exo Cinema and Exo Browser (`accounts.getexo.app`, on Exo's Cloudflare account). The first run asks you to sign in or create one; it goes on without one only while the service can't be reached, and asks again the next time. Exo Cinema asks the service who is signed in, signs you in or creates the account, and, when you press those buttons, changes the password, makes a new recovery code, lists and removes signed-in PCs, saves a copy of your data or deletes the account. It sends the email as typed, the password on the press that needs it, and this PC's device ID (32 random characters, made once). The service sees your IP address while it answers.

Signed in, Exo Cinema syncs these items with the account, so your other PCs have them. They are stored by the service as they are, not end-to-end encrypted:

- your choices (Play next episode), the audio and subtitle languages you pick for films, and the languages a PC chose for a show before 1.5, by the show's IMDb or TMDB ID;
- on a PC that used Exo Cinema before 1.5, what Exo Watch kept there: each title watched and when, where one was left, and the titles saved, by ID and name.

What you watch on your Jellyfin server is not among them: the server keeps that, and nothing of it goes to Exo. No card is stored, and no Jellyfin address, name or key.

The session token stays sealed with Windows DPAPI for this Windows user in `account-session.bin`, never in settings, logs or a plain backup. The password is never kept, and the recovery code is shown once and stored nowhere. **Sign out** ends the session here and at the service; what is on this PC stays. **Export my data** saves a copy of the account's items to a file you pick. **Delete account** deletes the account and every item synced to it; what is on this PC stays.

### What Exo Cinema kept before 1.5

Up to 1.4, Exo Cinema could also take films from other sources, and kept its own record of what you watched (Exo Watch). 1.5 plays from your Jellyfin server alone. Nothing it kept is deleted:

- Exo Watch (`watch\ledger.dat`, and its items in your Exo account) stays as it is. Its Watchlist and History still show in those rooms beside your server's, each title by its name; pressing one looks the name up on your server. Nothing is added to it any more.
- The list of sources a PC had (their addresses, which could hold their owner's keys) stays in `sync\cinema.dat`, and the copy in your Exo account stays there. 1.5 neither reads, sends nor deletes either. **Delete account** removes the account's copy; uninstalling with **Also remove my sign-in and settings** removes this PC's.

1.5 asks none of the hosts those sources used: no public catalog, no picture host of theirs, no lookup service.

### Crash, freeze and error reports (on by default)
Exo Cinema sends a diagnostic report to Exo's report service (`reports.getexo.app`, on Exo's Cloudflare account) when:
- it crashes; the report is sent at its next start;
- its window doesn't answer for five seconds (a freeze); it is sent straight away;
- a film ends with a playback error from the player, or every version of a film is refused by its host; sent straight away;
- the Jellyfin server doesn't answer three times in a row; sent straight away.

A report holds these five things and nothing else: the app's name (`cinema`), Exo Cinema's version, the Windows build (for example `10.0.26300.0`), the name of the error (for example `InvalidOperationException`, `Hang.Ui`, `PlaybackFailed` or `ServerUnreachable`), and up to about 8 KB of log lines from around the error. Before anything leaves this PC, the lines lose every address (your Jellyfin server's, a film's link, any web address), file paths, file names, titles, your Windows user name and your PC's name, email addresses, numbers of nine or more digits, and anything that looks like a token. A line that still looks personal is removed whole. The service checks each report again and refuses one that still holds any of these.

Not sent, and not kept: an install ID, your Exo account or its email, a watch history, what you searched for. The report service sees the IP address a report comes from while it answers, as any server does; it does not store it. Each error name is sent at most once a day, and this PC sends at most five reports a day.

A report is stored for 30 days, then deleted. Two things outlast that: the service counts reports by app, version, error name and day, and keeps those counts for 90 days; and it emails Exo's developer the first, 10th, 100th and 1000th report of an error in a version (the app, version, Windows build, error name and the last 1,500 characters of the stripped log lines) and a daily summary of the counts. Exo uses reports only to find and fix what breaks in Exo Cinema.
### Microsoft

Exo Cinema's interface runs on Microsoft Edge WebView2, part of Windows. WebView2 updates itself and its components through Microsoft, and may send diagnostic data according to your Windows privacy settings. If WebView2 is missing, Setup downloads Microsoft's installer from `go.microsoft.com` and checks Microsoft's signature on it.

## Updates

As Exo Cinema opens, and only then, it asks Exo's update address (`updates.getexo.app`, run by Exo on Cloudflare) which release is the newest, and when a newer one is out, reads that release's list of changes (`whats-new.json`). If that address doesn't answer, nothing else is asked and there is no update that time. **Settings > About > Check for updates** asks at once. The update address sees your IP address while it answers, and "ExoCinema/" with the version you run; nothing else is sent. The update address keeps no logs and stores nothing about you.

When you choose **Install update**, Exo Cinema downloads that release's Setup and its SHA-256 from the update address, which hands the files over from Exo's own copy on Cloudflare. No other host is asked. It checks the file against that checksum and against its own version details, keeps it in `%LocalAppData%\ExoCinema\updates` and runs it. Setup installs over the version you have and keeps your data; the downloaded Setup is deleted the next time Exo Cinema starts.

From 1.5 on, an update first asks the same update address for the new release's list of files (each file's path, size and SHA-256) and downloads only the files that differ from the ones on this PC, checking each against that list before anything is replaced. When a file can't be fetched or doesn't match, the whole Setup is downloaded as above. Nothing more is sent than for Setup: your IP address is seen while the address answers, with "ExoCinema/" and the version you run.

## Questions

Ask at <https://getexo.app/help>. If you write about a problem, leave out anything personal, your server's address included.
