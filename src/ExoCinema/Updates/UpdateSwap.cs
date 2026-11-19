using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExoCinema.Updates;

/// <summary>
/// Where a file-level update stands, in app.update.json beside the app folder. The swap writes it at each step, so the
/// next start (and Windows' next sign-in, after a power cut) knows what was going on.
/// </summary>
internal sealed record UpdateState(string Version, string State, string Reason)
{
    public const string FileName = "app.update.json";
    public const string Staged = "staged", Swapping = "swapping", Done = "done", Failed = "failed";

    public static UpdateState? Read(string root)
    {
        try
        {
            var n = JsonNode.Parse(File.ReadAllText(Path.Combine(root, FileName)));
            return (string?)n?["version"] is { } version && (string?)n?["state"] is { } state ? new(version, state, (string?)n?["reason"] ?? "") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    public void Write(string root)
    {
        var json = new JsonObject { ["schema"] = 1, ["version"] = Version, ["state"] = State, ["reason"] = Reason, ["at"] = DateTime.UtcNow.ToString("o") };
        var temp = Path.Combine(root, FileName + ".new");
        File.WriteAllText(temp, json.ToJsonString());
        File.Move(temp, Path.Combine(root, FileName), overwrite: true);
    }
}

/// <summary>
/// The swap of a file-level update (misc\updates\file-level-updates.md). The new app folder is already complete beside
/// the installed one; swapping is two folder renames, done by a small PowerShell script (Windows' own, as Setup's checks
/// use) once Exo Cinema has closed, because a folder can't be renamed while the app runs from it. The script checks the
/// staged folder against the release's list once more, renames, and opens Exo Cinema again, the new one or, if anything
/// went wrong, the one that was there. Every step can be picked up after a crash or a power cut: the same script, run
/// with -Recover at the next sign-in (a RunOnce entry it sets before the first rename) or by the next start, puts the
/// previous folder back when the app folder is missing.
/// </summary>
internal static class UpdateSwap
{
    public const string ScriptName = "app.update.ps1";
    public const string ReadyName = "app.update.ready";

    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>A path <see cref="Arm"/> returned: the swap is already waiting, so there is no Setup to run.</summary>
    public static bool IsToken(string path) => path.EndsWith(ScriptName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the swap, which waits for the process <paramref name="appPid"/> to exit (0: nothing to wait for). Returns
    /// once the script has said it is running; if it doesn't within 15 s (PowerShell blocked on this PC), the staged
    /// folder is removed and <see cref="FileUpdateUnavailable"/> thrown, with nothing installed changed.
    /// </summary>
    public static string Arm(StagedUpdate staged, int appPid, string exe = "ExoCinema.exe", bool launch = true, IEnumerable<string>? more = null)
    {
        var script = Path.Combine(staged.Root, ScriptName);
        var ready = Path.Combine(staged.Root, ReadyName);
        Process? process = null;
        try
        {
            File.WriteAllText(script, Script, Encoding.ASCII);
            File.Delete(ready);
            new UpdateState(staged.Version, UpdateState.Staged, "").Write(staged.Root);
            List<string> arguments = ["-Version", staged.Version, "-Incoming", Path.GetFileName(staged.Incoming), "-AppPid", appPid.ToString()];
            if (!launch) arguments.Add("-NoLaunch");
            if (more is not null) arguments.AddRange(more);
            process = Process.Start(StartInfo(script, staged.Root, exe, arguments));
            var until = Stopwatch.StartNew();
            while (until.Elapsed < TimeSpan.FromSeconds(15) && process is { HasExited: false })
            {
                if (File.Exists(ready)) return script;
                Thread.Sleep(50);
            }
            // It may have finished already (nothing to wait for): its state says so.
            if (process is { HasExited: true } && UpdateState.Read(staged.Root) is { State: UpdateState.Done }) return script;
            throw new FileUpdateUnavailable("swap-not-started", "The update's last step couldn't start on this PC.");
        }
        catch (Exception e) when (e is not FileUpdateUnavailable && (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException))
        {
            Abandon(staged, process);
            throw new FileUpdateUnavailable("swap-not-started", "The update's last step couldn't start on this PC.", e);
        }
        catch (FileUpdateUnavailable)
        {
            Abandon(staged, process);
            throw;
        }
        finally { process?.Dispose(); }
    }

    private static void Abandon(StagedUpdate staged, Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        FileUpdater.TryDelete(staged.Incoming);
        try { new UpdateState(staged.Version, UpdateState.Failed, "swap-not-started").Write(staged.Root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// At each start: when a swap was left unfinished (its state says staged or swapping and its script is gone), or its
    /// folders are still beside the app, the script's -Recover tidies up and records how it ended. Nothing runs otherwise.
    /// </summary>
    public static void OnStart(string appFolder, Action<string> log, string exe = "ExoCinema.exe")
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(appFolder).TrimEnd('\\'));
            if (root is null) return;
            var ready = Path.Combine(root, ReadyName);
            if (SwapRunning(ready)) return;
            var state = UpdateState.Read(root);
            var unfinished = state is { State: UpdateState.Staged or UpdateState.Swapping };
            var leftovers = Directory.EnumerateDirectories(root, FileUpdater.IncomingPrefix + "u*").Any() || Directory.EnumerateDirectories(root, FileUpdater.PreviousPrefix + "u*").Any();
            if (!unfinished && !leftovers) { File.Delete(ready); return; }
            var script = Path.Combine(root, ScriptName);
            File.WriteAllText(script, Script, Encoding.ASCII);
            log($"update: an unfinished file-level update is being tidied up ({state?.State ?? "folders left"})");
            Process.Start(StartInfo(script, root, exe, ["-Recover"]))?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // The ready file names the script's process: while that PowerShell is alive, a swap is in hand and nothing is touched.
    private static bool SwapRunning(string ready)
    {
        try
        {
            if (!File.Exists(ready) || !int.TryParse(File.ReadAllText(ready).Trim(), out var pid)) return false;
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static ProcessStartInfo StartInfo(string script, string root, string exe, IEnumerable<string> more)
    {
        var start = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        foreach (var a in (string[])["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", script, "-Root", root, "-Exe", exe, .. more])
            start.ArgumentList.Add(a);
        return start;
    }

    /// <summary>The swap script, for Windows PowerShell 5.1. Plain ASCII; written beside the app folder when it is needed.</summary>
    public const string Script = """
# Exo's file-level update, last step (misc\updates\file-level-updates.md in the Exo folder). Written by the app.
#   -Root <folder>     the folder that holds app\ (and, staged, app.incoming-u<n>\)
#   normal run         waits for -AppPid to exit, checks the staged folder against its file list (size and SHA-256 of
#                      every file, nothing extra), renames app\ aside and the staged folder to app\, opens the app.
#   -Recover           after a crash or a power cut: puts the previous app folder back if app\ is missing, records how
#                      the update ended, removes what is left over. Safe to run at any time, any number of times.
# Every step is one whole-folder rename, so app\ is always a complete old version, a complete new one, or missing with
# the old one kept beside it; -Recover (the next start, or the RunOnce entry set before the first rename) covers the last.
param(
    [Parameter(Mandatory)][string]$Root,
    [string]$Exe = 'ExoCinema.exe',
    [string]$Version = '',
    [string]$Incoming = '',
    [int]$AppPid = 0,
    [switch]$Recover,
    [switch]$NoLaunch,
    [int]$CloseSeconds = 30,
    [int]$RenameTries = 40,
    [string]$RunOnceKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
    [string]$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
    # Tests only: stop dead after a step, as a crash or a power cut would.
    [string]$StopAfter = ''
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$app = Join-Path $Root 'app'
$appExe = Join-Path $app $Exe
$stateFile = Join-Path $Root 'app.update.json'
$readyFile = Join-Path $Root 'app.update.ready'
$self = $MyInvocation.MyCommand.Path
$rootHash = [BitConverter]::ToString((New-Object Security.Cryptography.SHA256CryptoServiceProvider).ComputeHash([Text.Encoding]::UTF8.GetBytes($Root.ToLowerInvariant()))).Replace('-', '').Substring(0, 12)
$runName = "ExoUpdate-$rootHash"
$script:previousName = ''

function Write-State([string]$State, [string]$Reason = '') {
    $json = [ordered]@{ schema = 1; version = $Version; state = $State; reason = $Reason; incoming = $Incoming; previous = $script:previousName; swapper = $PID; at = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Compress
    $temp = "$stateFile.new"
    [IO.File]::WriteAllText($temp, $json)
    if ([IO.File]::Exists($stateFile)) { [IO.File]::Delete($stateFile) }
    [IO.File]::Move($temp, $stateFile)
}

function Read-State {
    try { return [IO.File]::ReadAllText($stateFile) | ConvertFrom-Json } catch { return $null }
}

function Stop-Test([string]$Step) { if ($StopAfter -eq $Step) { [Diagnostics.Process]::GetCurrentProcess().Kill() } }

function Move-Folder([string]$From, [string]$To, [int]$Tries) {
    for ($i = 0; $i -lt $Tries; $i++) {
        try { [IO.Directory]::Move($From, $To); return $true } catch { if ($i + 1 -lt $Tries) { Start-Sleep -Milliseconds 500 } }
    }
    return $false
}

# app\ back from the kept previous folder when it is missing. 'present', 'restored' or 'missing'.
function Restore-App {
    if ([IO.File]::Exists($appExe)) { return 'present' }
    $kept = @(Get-ChildItem -LiteralPath $Root -Directory -Filter 'app.previous-*' | Where-Object { [IO.File]::Exists((Join-Path $_.FullName $Exe)) } | Sort-Object LastWriteTimeUtc -Descending)
    if ($kept.Count -eq 0) { return 'missing' }
    # A folder with no app in it is set aside, never deleted, before the kept one takes its place.
    if ([IO.Directory]::Exists($app) -and -not (Move-Folder $app (Join-Path $Root "app.broken-$PID") 4)) { return 'missing' }
    if (Move-Folder $kept[0].FullName $app $RenameTries) { return 'restored' }
    return 'missing'
}

function Remove-Leftovers {
    if (-not [IO.File]::Exists($appExe)) { return }
    foreach ($pattern in 'app.incoming-u*', 'app.previous-u*', 'app.broken-*') {
        Get-ChildItem -LiteralPath $Root -Directory -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
            try { [IO.Directory]::Delete($_.FullName, $true) } catch { }
        }
    }
}

function Set-RunOnce {
    try {
        $command = "powershell -nop -w Hidden -ep Bypass -File `"$self`" -Root `"$Root`" -Exe `"$Exe`" -Recover"
        # Windows ignores a RunOnce entry longer than 260 characters; the next start's -Recover covers that case.
        if ($command.Length -gt 255) { return }
        if (-not (Test-Path -LiteralPath $RunOnceKey)) { New-Item -Path $RunOnceKey -Force | Out-Null }
        Set-ItemProperty -LiteralPath $RunOnceKey -Name $runName -Value $command
    } catch { }
}

function Remove-RunOnce {
    try { Remove-ItemProperty -LiteralPath $RunOnceKey -Name $runName -ErrorAction SilentlyContinue } catch { }
}

# Installed apps shows the new version: the entry whose InstallLocation is this app folder.
function Set-InstalledVersion([string]$To) {
    try {
        foreach ($key in Get-ChildItem -LiteralPath $UninstallKey -ErrorAction SilentlyContinue) {
            $entry = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if ($entry.InstallLocation -and [StringComparer]::OrdinalIgnoreCase.Equals(([string]$entry.InstallLocation).TrimEnd('\'), $app)) {
                Set-ItemProperty -LiteralPath $key.PSPath -Name DisplayVersion -Value $To
            }
        }
    } catch { }
}

function Get-AppVersion {
    try { return [string]([IO.File]::ReadAllText((Join-Path $app 'payload-manifest.json')) | ConvertFrom-Json).version } catch { return '' }
}

# '' when the folder is exactly the release its own file list describes, else what is wrong.
function Test-Staged([string]$Folder) {
    if (-not [IO.Directory]::Exists($Folder)) { return 'no staged folder' }
    if (@(Get-ChildItem -LiteralPath $Folder -Recurse -Force -Attributes ReparsePoint).Count -gt 0) { return 'a link' }
    $manifest = [IO.File]::ReadAllText((Join-Path $Folder 'payload-manifest.json')) | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or [string]$manifest.version -ne $Version -or @($manifest.files).Count -eq 0) { return 'the file list' }
    $extras = 'payload-manifest.json', 'Uninstall.exe', 'ExoSetup.exe'
    $want = @{}
    foreach ($entry in $manifest.files) { $want[([string]$entry.path).Replace('/', '\')] = $entry }
    $sha = New-Object Security.Cryptography.SHA256CryptoServiceProvider
    $count = 0
    foreach ($file in [IO.Directory]::GetFiles($Folder, '*', 'AllDirectories')) {
        $relative = $file.Substring($Folder.Length + 1)
        if ($extras -contains $relative) { continue }
        $entry = $want[$relative]
        if (-not $entry) { return "an extra file ($relative)" }
        if ((New-Object IO.FileInfo $file).Length -ne [long]$entry.size) { return "a size ($relative)" }
        $stream = [IO.File]::OpenRead($file)
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose() }
        if ($hash -ne [string]$entry.sha256) { return "a checksum ($relative)" }
        $count++
    }
    if ($count -ne $want.Count -or -not [IO.File]::Exists((Join-Path $Folder $Exe))) { return 'a missing file' }
    return ''
}

# The app's WebView2 helpers let go of its folders a moment after it closes: up to 5 s for them, never stopping one.
function Wait-WebView {
    try {
        $target = (Join-Path $Root 'WebView2')
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $left = @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -Property ProcessId, CommandLine | Where-Object {
                    $_.CommandLine -and $_.CommandLine.IndexOf($target, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
            if ($left.Count -eq 0) { return }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
    } catch { }
}

function Open-App {
    if ($NoLaunch -or -not [IO.File]::Exists($appExe)) { return }
    try { Start-Process -FilePath $appExe -WorkingDirectory $app } catch { }
}

# Nothing new installed: the app that was there is put back if it was moved, and opened again.
function Stop-Update([string]$Reason) {
    $result = Restore-App
    try { Write-State 'failed' $Reason } catch { }
    if ($result -ne 'missing') { Remove-RunOnce; Remove-Leftovers }
    try { [IO.File]::Delete($readyFile) } catch { }
    Open-App
    exit 1
}

if ($Recover) {
    $state = Read-State
    $result = Restore-App
    if ($state -and ($state.state -eq 'staged' -or $state.state -eq 'swapping')) {
        $Version = [string]$state.version
        $Incoming = [string]$state.incoming
        if ($result -eq 'present' -and $state.state -eq 'swapping' -and (Get-AppVersion) -eq $Version) {
            Write-State 'done'
            Set-InstalledVersion $Version
        } else {
            Write-State 'failed' 'interrupted'
        }
    }
    if ($result -ne 'missing') { Remove-RunOnce; Remove-Leftovers }
    try { [IO.File]::Delete($readyFile) } catch { }
    if ($result -eq 'missing') { exit 1 } else { exit 0 }
}

try {
    if (-not $Version -or $Incoming -notmatch '^app\.incoming-u\d+$') { throw 'Nothing staged was named.' }
    $staged = Join-Path $Root $Incoming
    $script:previousName = "app.previous-u$PID"
    $previous = Join-Path $Root $script:previousName
    [IO.File]::WriteAllText($readyFile, "$PID")
    Stop-Test 'ready'

    # The app closes itself after starting this. One that hasn't gone is ended, as Setup does, if it is this app.
    if ($AppPid -gt 0) {
        $process = Get-Process -Id $AppPid -ErrorAction SilentlyContinue
        $ours = $false
        try { $ours = $process -and [StringComparer]::OrdinalIgnoreCase.Equals($process.MainModule.FileName, $appExe) } catch { }
        if ($ours -and -not $process.WaitForExit($CloseSeconds * 1000)) {
            try { $process.Kill() } catch { }
            if (-not $process.WaitForExit(5000)) { Stop-Update 'still-running' }
        }
        Wait-WebView
    }

    $wrong = Test-Staged $staged
    if ($wrong) { Stop-Update "staged-mismatch: $wrong" }
    Stop-Test 'verified'

    Set-RunOnce
    Write-State 'swapping'
    Stop-Test 'journal'
    if (-not (Move-Folder $app $previous $RenameTries)) { Stop-Update 'in-use' }
    Stop-Test 'renamed-old'
    if (-not (Move-Folder $staged $app 4)) { Stop-Update 'swap-failed' }
    Stop-Test 'renamed-new'

    Write-State 'done'
    Remove-RunOnce
    Set-InstalledVersion $Version
    Remove-Leftovers
    try { [IO.File]::Delete($readyFile) } catch { }
    Open-App
    exit 0
} catch {
    Stop-Update 'error'
}
""";
}
