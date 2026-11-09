<#
  End-to-end test: real daemon + real client over a named pipe, stub video/overlay (build.ps1 check).
  Uses fake credentials only and a private pipe name, so a running real daemon is not touched.
  Usage: tests\test_ipc.ps1 [path-to-stub-cctv-overlay.exe]
#>
param([string]$Bin = "$PSScriptRoot\..\build\stub\cctv-overlay.exe")
$ErrorActionPreference = 'Continue'
$Bin = (Resolve-Path $Bin).Path

$PW = 'p@ss:w/rd%1'
$PW_ENC = 'p%40ss%3Aw%2Frd%251'
$T = Join-Path $env:TEMP ("cctv-ipc-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$T\cfg", "$T\state" | Out-Null
$env:CCTV_OVERLAY_STATE_DIR = "$T\state"   # never touch the real log
Remove-Item Env:CCTV_OVERLAY_PASSWORD, Env:CCTV_OVERLAY_USERNAME -ErrorAction SilentlyContinue
$env:CCTV_OVERLAY_STUB_DELAY_MS = '30'
$env:CCTV_OVERLAY_BACKOFF_SCALE = '0.01'
$Pipe = "cctv-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
$CFG = "$T\cfg\config.ini"
$utf8 = New-Object System.Text.UTF8Encoding($false)

[IO.File]::WriteAllText($CFG, @"
[general]
log_level = debug
[recorder]
host = 192.168.1.100
username = tester
[camera.front]
name = Front Door
channel = 1
[camera.driveway]
name = Driveway
channel = 2
[camera.garden]
name = Garden
channel = 3
[grid]
enabled = false
"@, $utf8)
function Write-Secrets { [IO.File]::WriteAllText("$T\cfg\secrets.ini", "[recorder]`npassword = $PW`n", $utf8) }
Write-Secrets

$script:fails = 0; $script:checks = 0; $script:D = $null
function ok($m) { $script:checks++; Write-Host "ok   - $m" }
function fail($m) { $script:checks++; $script:fails++; Write-Host "FAIL - $m" }
function expect_eq($m, $got, $want) { if ("$got" -eq "$want") { ok $m } else { fail "$m (got '$got', want '$want')" } }

function C { & $Bin --socket $Pipe --config $CFG @args; return $LASTEXITCODE }
function Cq { & $Bin --socket $Pipe --config $CFG @args *> $null; return $LASTEXITCODE }
function St { (& $Bin --socket $Pipe --config $CFG --no-spawn status --json | ConvertFrom-Json) }
function PipeExists { Test-Path "\\.\pipe\$Pipe" }
function WaitFor([double]$sec, [scriptblock]$cond) {
    $end = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 20 }
    return $false
}
function Start-Daemon([string]$cfg, [string]$tag) {
    $p = Start-Process -FilePath $Bin -ArgumentList @('--socket', $Pipe, 'daemon', '--config', "`"$cfg`"") -PassThru -NoNewWindow `
        -RedirectStandardOutput "$T\out$tag.log" -RedirectStandardError "$T\err$tag.log"
    $null = $p.Handle   # keep the handle open, or ExitCode is lost once the process ends
    return $p
}
function Stop-Daemon { if ($script:D -and -not $script:D.HasExited) { $script:D.WaitForExit(3000) | Out-Null }; $script:D = $null }

try {
    # ---------- exit codes without a daemon ----------
    expect_eq "no daemon, --no-spawn -> exit 3" (Cq --no-spawn status) 3
    & $Bin frobnicate *> $null; expect_eq "unknown command -> exit 2" $LASTEXITCODE 2
    & $Bin --bogus-flag *> $null; expect_eq "unknown flag -> exit 2" $LASTEXITCODE 2
    & $Bin *> $null; expect_eq "no command -> exit 2" $LASTEXITCODE 2
    & $Bin --socket $Pipe --config "$T\missing.ini" status *> $null; expect_eq "missing config on spawn -> exit 2" $LASTEXITCODE 2
    & $Bin --socket $Pipe --config "$T\missing.ini" daemon *> $null; expect_eq "daemon with missing config -> exit 2" $LASTEXITCODE 2
    expect_eq "quit with no daemon -> exit 3 (never spawns)" (Cq --no-spawn quit) 3
    if (PipeExists) { fail "quit must not have spawned a daemon" } else { ok "quit did not spawn a daemon" }
    & $Bin --socket 'bad|name' --config $CFG --no-spawn status *> $null; expect_eq "invalid pipe name -> exit 2" $LASTEXITCODE 2

    # ---------- start daemon ----------
    $script:D = Start-Daemon $CFG ''
    expect_eq "pipe appears" (WaitFor 5 { PipeExists }) $true
    expect_eq "ping -> exit 0" (Cq --no-spawn ping) 0
    expect_eq "version in reply" ((& $Bin --socket $Pipe --no-spawn --json ping | ConvertFrom-Json).version) "0.1.0"
    $s = St
    expect_eq "initial state hidden" $s.state hidden
    expect_eq "initial visible false" $s.visible False
    expect_eq "camera_count" $s.camera_count 3
    expect_eq "initial error null" ($null -eq $s.error) True

    # pipe ACL: only this account, network denied
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
    $c.Connect(2000)
    $acl = $c.GetAccessControl()
    $c.Dispose()
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $allow = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.AccessControlType -eq 'Allow' } | ForEach-Object { $_.IdentityReference.Value })
    $deny = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.AccessControlType -eq 'Deny' } | ForEach-Object { $_.IdentityReference.Value })
    expect_eq "pipe ACL allows only you" ($allow -join ',') $me
    expect_eq "pipe ACL denies network logons" ($deny -join ',') 'S-1-5-2'

    # second daemon refused
    $p2 = Start-Process -FilePath $Bin -ArgumentList @('--socket', $Pipe, 'daemon', '--config', "`"$CFG`"") -PassThru -NoNewWindow -Wait `
        -RedirectStandardOutput "$T\out2.log" -RedirectStandardError "$T\err2.log"
    expect_eq "second daemon exits 1" $p2.ExitCode 1
    if ((Get-Content "$T\err2.log" -Raw) -match 'already running') { ok "second daemon says 'already running'" } else { fail "second daemon message" }
    expect_eq "first daemon unaffected" (Cq --no-spawn ping) 0

    # ---------- toggle / next / prev / hide ----------
    expect_eq "toggle -> exit 0" (Cq --no-spawn toggle) 0
    expect_eq "visible after toggle" (St).visible True
    expect_eq "reaches live (stub)" (WaitFor 3 { (St).state -eq 'live' }) $true
    expect_eq "camera is Front Door" (St).camera "Front Door"
    Cq --no-spawn next | Out-Null; expect_eq "next" (St).camera_index 1
    Cq --no-spawn next | Out-Null; expect_eq "next again" (St).camera_index 2
    Cq --no-spawn next | Out-Null; expect_eq "next wraps to 0" (St).camera_index 0
    Cq --no-spawn prev | Out-Null; expect_eq "prev wraps to 2" (St).camera_index 2
    expect_eq "prev camera name" (St).camera Garden
    expect_eq "live after switching" (WaitFor 3 { (St).state -eq 'live' }) $true
    expect_eq "hide -> exit 0" (Cq --no-spawn hide) 0
    expect_eq "hidden after hide" (St).state hidden
    expect_eq "visible false after hide" (St).visible False
    Cq --no-spawn next | Out-Null; expect_eq "next while hidden opens" (St).visible True
    expect_eq "next while hidden selects neighbour" (St).camera "Front Door"
    Cq --no-spawn hide | Out-Null
    Cq --no-spawn show Driveway | Out-Null; expect_eq "show by name" (St).camera_index 1
    Cq --no-spawn show 2 | Out-Null; expect_eq "show by index" (St).camera Garden
    Cq --no-spawn show front | Out-Null; expect_eq "show by id" (St).camera "Front Door"
    $e = & $Bin --socket $Pipe --config $CFG --no-spawn show nosuchcam 2>&1 | Out-String
    expect_eq "show unknown -> exit 1" $LASTEXITCODE 1
    if ($e -match 'unknown camera') { ok "unknown camera message" } else { fail "unknown camera message ($e)" }
    Cq --no-spawn toggle | Out-Null; expect_eq "toggle from visible hides" (St).state hidden

    # ---------- status/list formats ----------
    if ((& $Bin --socket $Pipe --no-spawn status | Out-String) -match '(?m)^hidden:') { ok "human status line" } else { fail "human status line" }
    $l = & $Bin --socket $Pipe --no-spawn --json list | ConvertFrom-Json
    expect_eq "list --json count" $l.cameras.Count 3
    expect_eq "list order" (($l.cameras | ForEach-Object { $_.name }) -join ',') "Front Door,Driveway,Garden"
    if ((& $Bin --socket $Pipe --no-spawn list | Out-String) -match 'Driveway') { ok "human list" } else { fail "human list" }
    $raw = & $Bin --socket $Pipe --no-spawn --json status
    $fields = 'ok', 'version', 'visible', 'state', 'camera', 'camera_index', 'camera_count', 'error'
    $missing = $fields | Where-Object { $raw -notmatch ('"' + $_ + '":') }
    if (-not $missing) { ok "status json has required fields" } else { fail "status json fields: $missing" }

    # ---------- reload ----------
    Cq --no-spawn show driveway | Out-Null
    expect_eq "reload -> exit 0" (Cq --no-spawn reload) 0
    expect_eq "reload keeps current camera" (St).camera Driveway
    Copy-Item $CFG "$CFG.bak"
    [IO.File]::WriteAllText($CFG, "[overlay]`nwidth = 3`n", $utf8)
    $e = & $Bin --socket $Pipe --config $CFG --no-spawn reload 2>&1 | Out-String
    expect_eq "reload with bad config -> exit 1" $LASTEXITCODE 1
    if ($e -match 'width') { ok "reload error is actionable" } else { fail "reload error message ($e)" }
    expect_eq "daemon still serving old config" (St).camera Driveway
    Move-Item -Force "$CFG.bak" $CFG
    Cq --no-spawn hide | Out-Null

    # ---------- protocol robustness ----------
    function Raw-Conn {
        $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut,
            [System.IO.Pipes.PipeOptions]::None, [System.Security.Principal.TokenImpersonationLevel]::Identification)
        $c.Connect(2000); return $c
    }
    function Raw-Line($c) {
        $ms = New-Object System.IO.MemoryStream; $b = New-Object byte[] 4096
        while ($true) { $n = $c.Read($b, 0, $b.Length); if ($n -le 0) { break }; $ms.Write($b, 0, $n); if ($b[$n - 1] -eq 10) { break } }
        return [Text.Encoding]::UTF8.GetString($ms.ToArray())
    }
    function Raw-Send($c, [string]$s) { $d = [Text.Encoding]::UTF8.GetBytes($s); $c.Write($d, 0, $d.Length); $c.Flush() }
    $bad = 0
    $c = Raw-Conn; Raw-Send $c "not json`n"; $r = Raw-Line $c | ConvertFrom-Json; if ($r.ok -ne $false) { $bad++ }; $c.Dispose()
    $c = Raw-Conn; Raw-Send $c "{`"cmd`":`"ping`"}`n"; $r = Raw-Line $c | ConvertFrom-Json; if ($r.ok -ne $true) { $bad++ }; $c.Dispose()
    $c = Raw-Conn; Raw-Send $c ('x' * 5000); $r = Raw-Line $c | ConvertFrom-Json; if ($r.ok -ne $false -or $r.error -notmatch 'too large') { $bad++ }; $c.Dispose()
    $sw = [Diagnostics.Stopwatch]::StartNew(); $c = Raw-Conn; $n = $c.Read((New-Object byte[] 10), 0, 10); $dt = $sw.Elapsed.TotalSeconds; $c.Dispose()
    if (-not ($n -eq 0 -and $dt -gt 1.5 -and $dt -lt 3.5)) { $bad++; Write-Host "   idle drop: n=$n dt=$dt" }
    $c = Raw-Conn; Raw-Send $c "{`"cmd`":`"ping`"}`n"; $r = Raw-Line $c | ConvertFrom-Json; if ($r.ok -ne $true) { $bad++ }; $c.Dispose()
    expect_eq "malformed/oversized/idle connections handled" $bad 0

    # ---------- no secret leakage while the daemon is up ----------
    Cq --no-spawn toggle | Out-Null; Start-Sleep -Milliseconds 200; Cq --no-spawn next | Out-Null
    $leak = 0
    foreach ($f in "$T\out.log", "$T\err.log", "$T\out2.log", "$T\err2.log") {
        $txt = Get-Content $f -Raw -ErrorAction SilentlyContinue
        if ($txt -and ($txt.Contains($PW) -or $txt.Contains($PW_ENC))) { $leak = 1 }
    }
    expect_eq "password absent from daemon stdout/stderr" $leak 0
    $errlog = Get-Content "$T\err.log" -Raw -ErrorAction SilentlyContinue
    if ($errlog) { ok "daemon produced debug logs to scan ($(($errlog -split "`n").Count) lines)" } else { fail "no daemon logs captured" }
    $cl = (Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and ($_.CommandLine.Contains($PW) -or $_.CommandLine.Contains($PW_ENC)) }).Count
    expect_eq "password absent from process command lines" $cl 0
    $sj = & $Bin --socket $Pipe --no-spawn --json status
    expect_eq "password absent from status JSON" ($sj -match [regex]::Escape('p@ss')) False
    if ($errlog -match 'rtsp://') { fail "a URL appeared in the daemon log" } else { ok "no rtsp:// URL in daemon log" }

    # ---------- quit ----------
    expect_eq "quit -> exit 0" (Cq --no-spawn quit) 0
    expect_eq "daemon process exits after quit" (WaitFor 3 { $script:D.HasExited }) $true
    expect_eq "daemon exit status 0" $script:D.ExitCode 0; $script:D = $null
    if (PipeExists) { fail "pipe removed on quit" } else { ok "pipe removed on quit" }
    expect_eq "after quit -> exit 3" (Cq --no-spawn status) 3

    # ---------- taskkill (WM_CLOSE, the SIGTERM equivalent) shuts down cleanly ----------
    $script:D = Start-Daemon $CFG '3'
    expect_eq "daemon restarts cleanly" (WaitFor 5 { PipeExists }) $true
    & taskkill.exe /pid $script:D.Id *> $null
    expect_eq "taskkill -> daemon exits" (WaitFor 5 { $script:D.HasExited }) $true
    expect_eq "taskkill -> exit 0" $script:D.ExitCode 0; $script:D = $null
    if (PipeExists) { fail "pipe removed on taskkill" } else { ok "pipe removed on taskkill" }

    # ---------- a killed daemon leaves nothing stale ----------
    $script:D = Start-Daemon $CFG '4'
    WaitFor 5 { PipeExists } | Out-Null
    Stop-Process -Id $script:D.Id -Force; $script:D.WaitForExit(); $script:D = $null
    expect_eq "auto-spawn after a killed daemon works" (Cq status) 0
    Cq --no-spawn quit | Out-Null
    WaitFor 3 { -not (PipeExists) } | Out-Null

    # ---------- auto-spawn ----------
    if (PipeExists) { fail "pipe should be gone before auto-spawn test" } else { ok "clean slate" }
    $j = & $Bin --socket $Pipe --config $CFG --json status
    expect_eq "client auto-spawns daemon, exit 0" $LASTEXITCODE 0
    expect_eq "auto-spawned daemon answers" ($j | ConvertFrom-Json).state hidden
    expect_eq "second client reuses it" (St).camera_count 3
    $sp = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($Pipe) -and $_.CommandLine -match ' daemon ' }
    if ($sp -and $sp.Name -eq 'cctv-overlayw.exe' -and $sp.CommandLine.Contains($CFG)) { ok "spawned the windowless daemon with the config path only" } else { fail "spawned daemon not found ($($sp.CommandLine))" }
    Cq --no-spawn quit | Out-Null
    expect_eq "auto-spawned daemon quit" (WaitFor 3 { -not (PipeExists) }) $true
    if (Test-Path "$T\state\daemon.log") { ok "auto-spawned daemon logged to the private log file" } else { fail "no daemon.log" }

    # ---------- grid view, cycle ring and audio ----------
    $GCFG = "$T\cfg\grid.ini"
    [IO.File]::WriteAllText($GCFG, ((Get-Content $CFG -Raw) -replace '(?s)\[grid\].*$', ''), $utf8)
    function G { & $Bin --socket $Pipe --config $GCFG --no-spawn @args *> $null }
    function Gs { & $Bin --socket $Pipe --no-spawn --json status | ConvertFrom-Json }
    $script:D = Start-Daemon $GCFG 'g'
    WaitFor 5 { PipeExists } | Out-Null
    G toggle
    expect_eq "toggle opens the grid view" (Gs).view grid
    expect_eq "grid has one tile per camera (max 4)" (Gs).tiles.Count 3
    G next; expect_eq "next from grid -> first camera" (Gs).camera "Front Door"
    expect_eq "single view" (Gs).view camera
    G next; G next; expect_eq "next -> last camera" (Gs).camera Garden
    G next; expect_eq "next from last camera wraps to grid" (Gs).view grid
    G prev; expect_eq "prev from grid -> last camera" (Gs).camera Garden
    G show "Front Door"; G prev; expect_eq "prev from first camera -> grid" (Gs).view grid
    expect_eq "audio starts off" (Gs).audio False
    G audio; expect_eq "audio toggles on" (Gs).audio True
    G audio off; expect_eq "audio off" (Gs).audio False
    & $Bin --socket $Pipe --no-spawn audio loud *> $null; expect_eq "audio with a bad mode -> exit 2" $LASTEXITCODE 2
    if ((& $Bin --socket $Pipe --no-spawn status | Out-String) -match 'grid view') { ok "human status shows the grid" } else { fail "human status grid line" }
    G toggle; expect_eq "toggle hides from the grid" (Gs).state hidden
    G quit; Stop-Daemon

    # ---------- missing password: daemon works, stream start reports an error ----------
    Remove-Item "$T\cfg\secrets.ini"
    WaitFor 3 { -not (PipeExists) } | Out-Null
    $script:D = Start-Daemon $CFG '5'
    WaitFor 5 { PipeExists } | Out-Null
    expect_eq "daemon starts without password" (St).state hidden
    expect_eq "list works without password" (Cq --no-spawn list) 0
    Cq --no-spawn toggle | Out-Null
    expect_eq "toggle without password -> error state" (St).state error
    if ((St).error -match 'password') { ok "error mentions the missing password" } else { fail "error text" }
    Cq --no-spawn toggle | Out-Null
    Cq --no-spawn hide | Out-Null
    expect_eq "hide from error -> hidden" (St).state hidden
    Cq --no-spawn quit | Out-Null; Stop-Daemon

    # ---------- environment password also works (and never leaks) ----------
    WaitFor 3 { -not (PipeExists) } | Out-Null
    $env:CCTV_OVERLAY_PASSWORD = $PW
    $script:D = Start-Daemon $CFG '6'
    Remove-Item Env:CCTV_OVERLAY_PASSWORD
    WaitFor 5 { PipeExists } | Out-Null
    Cq --no-spawn toggle | Out-Null
    expect_eq "env password reaches live" (WaitFor 3 { (St).state -eq 'live' }) $true
    Cq --no-spawn quit | Out-Null; Stop-Daemon
    $txt = (Get-Content "$T\out6.log", "$T\err6.log" -Raw -ErrorAction SilentlyContinue) -join ''
    if ($txt.Contains($PW)) { fail "env password leaked into logs" } else { ok "env password not in logs" }
}
finally {
    if ($script:D -and -not $script:D.HasExited) { Stop-Process -Id $script:D.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($Pipe) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 200
    Remove-Item -Recurse -Force $T -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "$($script:checks) checks, $($script:fails) failed"
if ($script:fails -eq 0) { exit 0 } else { exit 1 }
