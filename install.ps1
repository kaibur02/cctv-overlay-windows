<#
cctv-overlay installer for Windows: requirements, build, per-user install, video engine (libmpv), camera
configuration, keys, password (stored only in a secrets file readable by you alone), sign-in start and a live test.

  install.cmd                          interactive wizard (recommended for people; double-click it)
  install.cmd --answers FILE --yes     unattended (for scripts / AI assistants; see docs\ai-install.md)
  install.cmd --set-password           set or change the recorder password (always typed by the user)
  install.cmd --check                  check requirements only, change nothing
  install.cmd --test                   open the overlay once and report whether the cameras connect
  install.cmd --uninstall              remove the program, its sign-in start and PATH entry

Safety rules this script follows:
 - Never asks for, accepts or prints the password through arguments, environment or answer files. The
   password is typed by you (hidden console prompt, or a password box when there is no console) and written
   straight into secrets.ini, which only your account can read.
 - Never overwrites an existing config.ini unless told to (it is backed up first).
 - Installs into %LOCALAPPDATA%\Programs\cctv-overlay (no administrator rights) unless CCTV_OVERLAY_PREFIX is set.
 - Downloads only libmpv, from the mpv project's Windows builds (github.com/shinchiro/mpv-winbuild-cmake),
   after asking, and checks its SHA-256 against the release listing.

Answering a question wrongly never ends the installer: it explains the problem and asks again. Only an answers
file with an invalid value stops it (there is nobody to ask).
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Here = $PSScriptRoot
$Prefix = if ($env:CCTV_OVERLAY_PREFIX) { $env:CCTV_OVERLAY_PREFIX } else { Join-Path $env:LOCALAPPDATA 'Programs\cctv-overlay' }
$Bin = Join-Path $Prefix 'cctv-overlay.exe'
$BinW = Join-Path $Prefix 'cctv-overlayw.exe'
$ConfDir = Join-Path $env:APPDATA 'cctv-overlay'
$Conf = Join-Path $ConfDir 'config.ini'
$Secrets = Join-Path $ConfDir 'secrets.ini'
$StateDir = Join-Path $env:LOCALAPPDATA 'cctv-overlay'
$LogFile = Join-Path $StateDir 'daemon.log'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$TS = Get-Date -Format 'yyyyMMdd-HHmmss'
$Utf8 = New-Object System.Text.UTF8Encoding($false)

# ---------------------------------------------------------------- window handling
# Double-clicked install.cmd runs in a window that closes when the script ends; keep it open so the result (or an
# error) can be read.
function Test-DoubleClicked {
    try {
        $me = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($me.ParentProcessId)" -ErrorAction Stop
        if ($parent.Name -eq 'explorer.exe') { return $true } # "Run with PowerShell"
        if ($parent.Name -ne 'cmd.exe') { return $false }
        $grand = Get-CimInstance Win32_Process -Filter "ProcessId=$($parent.ParentProcessId)" -ErrorAction Stop
        return $grand -and $grand.Name -eq 'explorer.exe'
    } catch { return $false }
}
function Test-Console {
    try { return [Environment]::UserInteractive -and -not [Console]::IsInputRedirected } catch { return $false }
}
$script:PauseAtEnd = (Test-Console) -and (Test-DoubleClicked)

function Finish([int]$code) {
    if ($script:PauseAtEnd) {
        Write-Host ''
        Write-Host 'Press any key to close this window.' -ForegroundColor DarkGray
        try { [void][Console]::ReadKey($true) } catch { }
    }
    exit $code
}

# ---------------------------------------------------------------- output helpers
$script:StepNo = 0
$script:StepCount = 0
function Tilde([string]$m) { return $m.Replace($env:USERPROFILE, '~') }
function Say([string]$m) { Write-Host (Tilde $m) }
function Info([string]$m) { Write-Host ('  ' + (Tilde $m)) -ForegroundColor DarkGray }
function Step([string]$m) {
    Write-Host ''
    $script:StepNo++
    $n = if ($script:StepCount -gt 0) { "Step $($script:StepNo) of $($script:StepCount): " } else { '' }
    Write-Host ($n + (Tilde $m)) -ForegroundColor Cyan
}
function Okay([string]$m) { Write-Host '  ' -NoNewline; Write-Host 'OK' -ForegroundColor Green -NoNewline; Write-Host (' ' + (Tilde $m)) }
function Warn([string]$m) { Write-Host '  ' -NoNewline; Write-Host '!' -ForegroundColor Yellow -NoNewline; Write-Host (' ' + (Tilde $m)) }
function Bad([string]$m) { Write-Host '  ' -NoNewline; Write-Host 'X' -ForegroundColor Red -NoNewline; Write-Host (' ' + (Tilde $m)) }
function Die([string]$m) {
    Write-Host ''
    Write-Host 'The installer stopped: ' -ForegroundColor Red -NoNewline
    Write-Host (Tilde $m)
    Finish 1
}

function Show-Usage { (Get-Content $PSCommandPath -TotalCount 11 | Select-Object -Skip 1) | ForEach-Object { Write-Host $_ } }

# ---------------------------------------------------------------- arguments (Linux-style and PowerShell-style)
$Mode = 'install'; $AnswersFile = ''; $AssumeYes = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    switch -Regex ($args[$i]) {
        '^-{1,2}answers$' { if ($i + 1 -ge $args.Count) { Die '--answers needs a file name after it' }; $AnswersFile = $args[++$i] }
        '^(-{1,2}yes|-y)$' { $AssumeYes = $true }
        '^-{1,2}(set-password|setpassword)$' { $Mode = 'password' }
        '^-{1,2}check$' { $Mode = 'check' }
        '^-{1,2}test$' { $Mode = 'test' }
        '^-{1,2}uninstall$' { $Mode = 'uninstall' }
        '^(-{1,2}help|-h|/\?)$' { Show-Usage; Finish 0 }
        default { Die "unknown option '$($args[$i])'. Run install.cmd --help for the list of options." }
    }
}

# ---------------------------------------------------------------- answers file (never executed)
$A = @{}
if ($AnswersFile) {
    if (-not (Test-Path -LiteralPath $AnswersFile)) { Die "cannot read the answers file '$AnswersFile'" }
    foreach ($line in [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $AnswersFile).Path)) {
        if ($line -match '^\s*(#|$)') { continue }
        if ($line -notmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$') { Die "answers file: cannot read this line: $line" }
        $key = $Matches[1]; $val = $Matches[2]
        if ($val -match '^"(.*)"$') { $val = $Matches[1] }
        if ($key -match 'PASS|SECRET|TOKEN') {
            Die "the answers file must not contain a password ($key). Remove it; the user sets it with: install.cmd --set-password"
        }
        $A[$key] = $val
    }
}
# CCTV_INSTALL_INTERACTIVE=1 is a test hook: prompts read typed lines even when input is redirected.
$Interactive = (Test-Console) -or $env:CCTV_INSTALL_INTERACTIVE -eq '1'
$Unattended = [bool]$AnswersFile -or -not $Interactive
$CanCaptureKeys = (Test-Console) -and -not $AnswersFile

# ---------------------------------------------------------------- questions
function Read-Line([string]$prompt) {
    $v = Read-Host $prompt
    if ($null -eq $v) { return '' }
    return $v.Trim()
}

# Runs cctv-overlay.exe and discards its output; returns the exit code. Windows PowerShell 5.1 turns anything a
# program writes to stderr into an error, which 'Stop' would make fatal: these calls must never stop the installer.
function Invoke-Quiet {
    $ErrorActionPreference = 'Continue'
    & $Bin @args *> $null
    return $LASTEXITCODE
}
# The same, returning the output lines (stdout and stderr).
function Invoke-Lines {
    $ErrorActionPreference = 'Continue'
    return @(& $Bin @args 2>&1 | ForEach-Object { "$_" })
}

function Sentence([string]$m) { if ($m -match '[.!?]$') { return $m }; return "$m." }

function Stop-AfterTooManyTries([int]$tries, [string]$what) {
    if ($tries -ge 10) { Die "no valid answer for '$what' after 10 tries" } # e.g. input closed
}

# Ask: answer from the answers file, else a prompt that repeats until $check (returns $null or an error message)
# accepts the answer. $normalize runs first (trims "rtsp://", etc.).
function Ask([string]$q, [string]$def = '', [string]$key = '', [scriptblock]$check = $null, [scriptblock]$normalize = $null) {
    $tries = 0
    while ($true) {
        $fromFile = $key -and $A.ContainsKey($key)
        if ($fromFile) { $v = ([string]$A[$key]).Trim() }
        elseif ($Unattended) { $v = $def }
        else {
            $v = Read-Line $(if ($def) { "  $q [$def]" } else { "  $q" })
            if (-not $v) { $v = $def }
        }
        if ($normalize) { $v = [string](& $normalize $v) }
        $err = if ($check) { & $check $v } else { $null }
        if (-not $err) { return $v }
        if ($fromFile) { Die "answers file, $key = '$v': $err" }
        if ($Unattended) { Die "$q`: $err" }
        Warn "$(Sentence $err) Please try again."
        Stop-AfterTooManyTries (++$tries) $q
    }
}

function YesNo([string]$q, [string]$def, [string]$key = '') {
    $tries = 0
    while ($true) {
        $fromFile = $key -and $A.ContainsKey($key)
        if ($fromFile) { $v = [string]$A[$key] }
        elseif ($AssumeYes -or $Unattended) { $v = $def }
        else {
            $v = Read-Line ("  $q " + $(if ($def -eq 'y') { '[Y/n]' } else { '[y/N]' }))
            if (-not $v) { $v = $def }
        }
        if ($v -match '^(y|yes|true|1|j|ja)$') { return $true }
        if ($v -match '^(n|no|false|0|nein)$') { return $false }
        if ($fromFile) { Die "answers file, $key = '$v': use yes or no" }
        Warn 'Please type y for yes or n for no (or press Enter for the answer in capitals).'
        Stop-AfterTooManyTries (++$tries) $q
    }
}

# Choose: numbered menu. Each option is @(value, description). Accepts the number, the value, or the start of a
# value. $custom (optional) can accept other input: it returns the value to use, or $null.
function Choose([string]$q, [string]$def, [string]$key, [object[]]$opts, [scriptblock]$custom = $null, [string]$customHint = '') {
    $values = @($opts | ForEach-Object { $_[0] })
    $defIndex = [array]::IndexOf($values, $def) + 1
    $shown = $false
    $tries = 0
    while ($true) {
        $fromFile = $key -and $A.ContainsKey($key)
        if ($fromFile) { $v = ([string]$A[$key]).Trim() }
        elseif ($Unattended) { $v = $def }
        else {
            if (-not $shown) {
                Say "  $q"
                for ($n = 0; $n -lt $opts.Count; $n++) { Say ('    {0}) {1,-12} {2}' -f ($n + 1), $opts[$n][0], $opts[$n][1]) }
                if ($customHint) { Info "  $customHint" }
                $shown = $true
            }
            $v = Read-Line ("  Type a number" + $(if ($defIndex -gt 0) { " [$defIndex]" } else { '' }))
            if (-not $v) { $v = $def }
        }
        if ($v -match '^\d+$' -and [int]$v -ge 1 -and [int]$v -le $opts.Count) { return $values[[int]$v - 1] }
        $exact = @($values | Where-Object { $_ -eq $v })
        if ($exact.Count -eq 1) { return $exact[0] }
        $pre = @($values | Where-Object { $v -and $_.StartsWith($v, [StringComparison]::OrdinalIgnoreCase) })
        if ($pre.Count -eq 1) { return $pre[0] }
        if ($custom) { $c = & $custom $v; if ($c) { return $c } }
        if ($fromFile) { Die "answers file, $key = '$v': use one of: $($values -join ', ')" }
        Warn ("'$v' is not one of the choices. Type a number from 1 to $($opts.Count)" + $(if ($customHint) { ', or see the tip above.' } else { '.' }))
        Stop-AfterTooManyTries (++$tries) $q
    }
}

# ---- validators (return $null when fine, else a plain-language problem) ----
function Normalize-Address([string]$v) {
    $v = $v.Trim() -replace '^[A-Za-z][A-Za-z0-9+.-]*://', '' # pasted rtsp://... or http://...
    $v = $v -replace '^[^@/]*@', ''                           # user:pass@ (asked separately, never stored)
    return ($v -replace '/.*$', '')                          # path
}
$CheckAddress = { param($v)
    if (-not $v) { return 'The address is required, for example 192.168.1.100.' }
    if ($v -notmatch '^[A-Za-z0-9.:_\[\]-]+$') { return "'$v' does not look like an IP address or host name (example: 192.168.1.100)." }
    $null }
$CheckPort = { param($v)
    if ($v -notmatch '^\d{1,5}$' -or [int]$v -lt 1 -or [int]$v -gt 65535) { return "The port must be a number from 1 to 65535 (RTSP normally uses 554)." }
    $null }
$CheckNotEmpty = { param($v) if (-not $v) { 'This cannot be empty.' } elseif ($v -match "[`r`n]") { 'Use a single line.' } else { $null } }
$CheckPath = { param($v)
    if (-not $v -or $v -eq '/') { return 'Type the RTSP path, for example /live/ch{channel}.' }
    $bad = [regex]::Matches($v, '\{([^}]*)\}') | Where-Object { $_.Groups[1].Value -notin 'channel', 'channel02', 'subtype', 'stream', 'mainsub' } | Select-Object -First 1
    if ($bad) { return "'{$($bad.Groups[1].Value)}' is not a placeholder. Use {channel} {channel02} {subtype} {stream} {mainsub}." }
    if (([regex]::Matches($v, '\{')).Count -ne ([regex]::Matches($v, '\}')).Count) { return 'A { or } is missing.' }
    $null }
function Normalize-Path([string]$v) { $v = $v.Trim(); if ($v -and -not $v.StartsWith('/')) { $v = '/' + $v }; return $v }

# ---------------------------------------------------------------- requirements
function Find-Csc {
    foreach ($d in "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319", "$env:WINDIR\Microsoft.NET\FrameworkArm64\v4.0.30319",
                   "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319") {
        if (Test-Path "$d\csc.exe") { return "$d\csc.exe" }
    }
    return $null
}
function Get-MpvArch {
    $a = $env:PROCESSOR_ARCHITEW6432; if (-not $a) { $a = $env:PROCESSOR_ARCHITECTURE }
    switch ($a) { 'AMD64' { return 'x86_64' } 'ARM64' { return 'aarch64' } default { return $null } }
}

function Test-Requirements {
    $bad = $false
    Step 'Checking this computer'
    $v = [Environment]::OSVersion.Version
    if ($v.Major -ge 10) { Okay "Windows $($v.Major) (build $($v.Build))" } else { Bad "Windows 10 or 11 is required (this is $v)"; $bad = $true }
    if (Get-MpvArch) { Okay "64-bit Windows ($(Get-MpvArch))" } else { Bad '64-bit Windows (x64 or ARM64) is required'; $bad = $true }
    $rel = 0
    try { $rel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop).Release } catch { }
    if ($rel -ge 461808) { Okay '.NET Framework 4.7.2 or newer' } else { Bad '.NET Framework 4.7.2 or newer is required: install the latest Windows updates'; $bad = $true }
    if (Find-Csc) { Okay 'C# compiler (part of Windows)' } else { Bad "the C# compiler (csc.exe) is missing: turn on '.NET Framework 4.8 Advanced Services' in Windows Features"; $bad = $true }
    if ([Environment]::UserInteractive) { Okay 'running on your desktop' } else { Bad 'not running on a desktop: the overlay needs your Windows session (not a service or SSH)'; $bad = $true }
    if (Get-Command tar.exe -ErrorAction SilentlyContinue) { Okay 'tar.exe (to unpack the video engine)' }
    else { Warn 'tar.exe is missing; put libmpv-2.dll next to install.cmd before installing' }
    $mpv = @("$Prefix\libmpv-2.dll", "$Here\libmpv-2.dll", $(if ($A.ContainsKey('MPV_DLL')) { $A['MPV_DLL'] } else { '' })) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($mpv) { Okay 'video engine (libmpv) found' } else { Info '  The video engine (libmpv, about 30 MB) will be downloaded in step 2.' }
    try {
        $gpus = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object { $_.Name }) -join ', '
        if ($gpus) { Okay "graphics: $gpus" }
    } catch { }
    return -not $bad
}

# ---------------------------------------------------------------- build + install
function Stop-Daemon {
    if (Test-Path $Bin) { Invoke-Quiet --no-spawn quit | Out-Null; Start-Sleep -Milliseconds 300 }
    Get-Process cctv-overlayw, cctv-overlay -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
}

function Install-Libmpv {
    $dst = Join-Path $Prefix 'libmpv-2.dll'
    $local = if ($A.ContainsKey('MPV_DLL')) { $A['MPV_DLL'] } elseif (Test-Path "$Here\libmpv-2.dll") { "$Here\libmpv-2.dll" } else { '' }
    if ($local) {
        if (-not (Test-Path -LiteralPath $local)) { Die "MPV_DLL '$local' does not exist" }
        Copy-Item -LiteralPath $local $dst -Force
        Okay "video engine copied from $(Split-Path -Leaf $local) next to the installer"
        return
    }
    if ((Test-Path $dst) -and -not (YesNo 'The video engine (libmpv) is already installed. Download the newest version?' 'n' 'UPDATE_MPV')) {
        Okay 'keeping the installed video engine'
        return
    }
    Info 'cctv-overlay plays video with libmpv, the engine of the mpv player. It is downloaded from the'
    Info 'mpv project''s Windows builds (github.com/shinchiro/mpv-winbuild-cmake) and its checksum is verified.'
    if (-not (YesNo 'Download it now (about 30 MB)?' 'y' 'DOWNLOAD_MPV')) {
        Die "cctv-overlay cannot play video without libmpv-2.dll. Download it yourself (see docs\install.md), put it next to install.cmd, and run install.cmd again."
    }
    $arch = Get-MpvArch
    $tmp = Join-Path $env:TEMP ("cctv-mpv-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $ProgressPreference = 'SilentlyContinue'
        try {
            $rel = Invoke-RestMethod -UseBasicParsing -Headers @{ 'User-Agent' = 'cctv-overlay-installer' } `
                -Uri 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
        } catch {
            Die "could not reach github.com to download the video engine ($($_.Exception.Message)). Check your internet connection and run install.cmd again."
        }
        $asset = $rel.assets | Where-Object { $_.name -match "^mpv-dev-$arch-\d{8}-git-[0-9a-f]+\.7z$" } | Select-Object -First 1
        if (-not $asset) { Die "the latest mpv build has no mpv-dev-$arch package. Download libmpv-2.dll yourself (see docs\install.md) and put it next to install.cmd." }
        $file = Join-Path $tmp $asset.name
        Say "  Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)..."
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $file -Headers @{ 'User-Agent' = 'cctv-overlay-installer' }
        } catch {
            Die "the download failed ($($_.Exception.Message)). Check your internet connection and run install.cmd again."
        }
        $hash = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
        $digest = $null
        if ($asset.PSObject.Properties.Name -contains 'digest' -and $asset.digest) { $digest = ([string]$asset.digest) -replace '^sha256:', '' }
        if ($digest) {
            if ($hash -ne $digest.ToLowerInvariant()) { Die "the download is damaged (checksum mismatch). Run install.cmd again." }
            Okay 'download verified (SHA-256 matches)'
        } else {
            Warn "the release has no published checksum; SHA-256 of the download: $hash"
        }
        $out = Join-Path $tmp 'x'
        New-Item -ItemType Directory -Force $out | Out-Null
        $ErrorActionPreference = 'Continue'
        & tar.exe -xf $file -C $out libmpv-2.dll *> $null
        $ErrorActionPreference = 'Stop'
        if (-not (Test-Path "$out\libmpv-2.dll")) { Die "could not unpack libmpv-2.dll from $($asset.name)." }
        Copy-Item "$out\libmpv-2.dll" $dst -Force
        Okay "video engine installed ($($asset.name))"
    } finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

function Build-Install {
    Step 'Building and installing the program'
    Info "Building from source with the C# compiler included in Windows (a few seconds)..."
    try { & "$Here\build.ps1" build | Out-Null }
    catch { Die "the build failed: $($_.Exception.Message). The compiler messages are shown above." }
    $ver = & "$Here\build\cctv-overlay.exe" --version
    Okay "built $ver"
    Stop-Daemon # a running daemon keeps the old program and config; it is started again later
    New-Item -ItemType Directory -Force $Prefix, "$Prefix\examples", "$Prefix\docs" | Out-Null
    try {
        Copy-Item "$Here\build\cctv-overlay.exe", "$Here\build\cctv-overlayw.exe" $Prefix -Force
    } catch {
        Die "could not copy the program to $Prefix ($($_.Exception.Message)). Close any running cctv-overlay window or terminal and try again."
    }
    Copy-Item "$Here\examples\*" "$Prefix\examples" -Force
    Copy-Item "$Here\docs\*.md" "$Prefix\docs" -Force -ErrorAction SilentlyContinue
    Copy-Item "$Here\README.md", "$Here\LICENSE" $Prefix -Force -ErrorAction SilentlyContinue
    Okay "installed to $Prefix"
    Install-Libmpv
}

# ---------------------------------------------------------------- camera system presets
$Systems = @(
    @('dahua', 'Dahua, Amcrest, Lorex or another Dahua-based recorder (NVR/DVR)'),
    @('hikvision', 'Hikvision, HiLook, Annke or another Hikvision-based recorder'),
    @('reolink', 'Reolink recorder'),
    @('uniview', 'Uniview (UNV) recorder'),
    @('standalone', 'Separate IP cameras, no recorder (each camera has its own address)'),
    @('custom', 'Another recorder, and I know its RTSP path')
)
$SystemPath = @{ dahua = '/cam/realmonitor'; hikvision = '/Streaming/Channels/{channel}0{stream}'
                 reolink = '/h264Preview_{channel02}_{mainsub}'; uniview = '/unicast/c{channel}/s{subtype}/live' }
$CamBrands = @(
    @('dahua', 'Dahua or Amcrest camera'),
    @('hikvision', 'Hikvision camera'),
    @('reolink', 'Reolink camera'),
    @('tapo', 'TP-Link Tapo or VIGI camera'),
    @('uniview', 'Uniview camera'),
    @('custom', 'Another camera, and I know its RTSP path')
)
$CameraPath = @{ dahua = '/cam/realmonitor?channel=1&subtype={subtype}'; hikvision = '/Streaming/Channels/10{stream}'
                 reolink = '/h264Preview_01_{mainsub}'; tapo = '/stream{stream}'; uniview = '/media/video{stream}' }

function Get-Slug([string]$name) { # camera id from a name: lowercase letters/digits only
    $s = ($name.ToLowerInvariant() -replace '[^a-z0-9]', '')
    if (-not $s) { $s = 'cam' }
    return $s
}

function Get-Displays { # an array of @(name, description); returned with "," so one display is not flattened
    Add-Type -AssemblyName System.Windows.Forms
    $list = New-Object System.Collections.ArrayList
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        $n = $s.DeviceName -replace '^\\\\\.\\', ''
        [void]$list.Add(@($n, ('{0} x {1}{2}' -f $s.Bounds.Width, $s.Bounds.Height, $(if ($s.Primary) { ', your main display' } else { '' }))))
    }
    return , $list.ToArray()
}

# ---------------------------------------------------------------- keys
$KeyActions = @(
    @('toggle', 'Show / hide the overlay', 'Ctrl + Num *', 'BIND_TOGGLE'),
    @('next', 'Next camera', 'Num *', 'BIND_NEXT'),
    @('prev', 'Previous camera', 'Num /', 'BIND_PREV'),
    @('audio', 'Camera sound on / off', 'Ctrl + Num -', 'BIND_AUDIO')
)

# Validates a key combination with the program's own parser and checks that no other program uses it.
function Test-Key([string]$spec) {
    $out = Invoke-Lines check-key $spec
    $code = $LASTEXITCODE
    $r = @{ Ok = $false; Text = ''; Typing = $false; InUse = $false; Error = '' }
    if ($code -eq 0 -or $code -eq 3) {
        $r.Ok = $true; $r.Text = $out[0]; $r.Typing = ($out.Count -gt 1 -and $out[1] -eq 'typing'); $r.InUse = ($code -eq 3)
    } else {
        $r.Error = ($out -join ' ')
        if (-not $r.Error) { $r.Error = "'$spec' is not a key combination cctv-overlay understands." }
    }
    return $r
}

# Waits for a key press and returns it as text ("Ctrl + Shift + OemMinus"), the suggestion, 'none', or '' to type.
function Read-KeyPress([string]$suggest) {
    Write-Host '    Press the keys now: ' -NoNewline
    while ($true) {
        try { $k = [Console]::ReadKey($true) } catch { Write-Host ''; return '' }
        switch ($k.Key.ToString()) {
            'Enter' { Write-Host "$suggest (suggested)"; return $suggest }
            'Backspace' { Write-Host 'no key'; return 'none' }
            'Escape' { Write-Host '(type it instead)'; return '' }
        }
        $mods = @()
        if ($k.Modifiers -band [ConsoleModifiers]::Control) { $mods += 'Ctrl' }
        if ($k.Modifiers -band [ConsoleModifiers]::Alt) { $mods += 'Alt' }
        if ($k.Modifiers -band [ConsoleModifiers]::Shift) { $mods += 'Shift' }
        $spec = (@($mods) + $k.Key.ToString()) -join ' + '
        $t = Test-Key ($spec + ' ')
        Write-Host $(if ($t.Ok) { $t.Text } else { $spec }) # e.g. "Ctrl + Num -", not Windows' "Ctrl + Subtract"
        return $spec
    }
}

function Read-Keys {
    if ($CanCaptureKeys) {
        Info 'For each action, press the key combination you want, for example Ctrl + Shift + -.'
        Info 'Enter = use the suggestion   Backspace = no key for this action   Esc = type the keys instead'
    } elseif (-not $Unattended) {
        Info 'Type each key combination, for example Ctrl + Shift + -, Ctrl + Num *, Alt + F9, or none.'
        Info 'Press Enter to use the suggestion.'
    }
    Info 'Tip: keys with Ctrl or Alt are safest; Num * means the * key on the number pad.'
    while ($true) {
        $chosen = [ordered]@{}
        foreach ($act in $KeyActions) {
            $name = $act[0]; $label = $act[1]; $suggest = $act[2]; $key = $act[3]
            $fromFile = $A.ContainsKey($key)
            $tries = 0
            while ($true) {
                if ($fromFile) { $spec = [string]$A[$key] }
                elseif ($Unattended) { $spec = $suggest }
                elseif ($CanCaptureKeys) {
                    Write-Host ''
                    Say "  $label   (suggested: $suggest)"
                    $spec = Read-KeyPress $suggest
                    if ($spec -eq '') {
                        $spec = Read-Line "    Type the keys [$suggest]"
                        if (-not $spec) { $spec = $suggest }
                    }
                } else {
                    $spec = Read-Line "  $label [$suggest]"
                    if (-not $spec) { $spec = $suggest }
                }
                $problem = $null
                $t = Test-Key ($spec + ' ') # the trailing space keeps a final \ from escaping the quote
                if (-not $t.Ok) { $problem = $t.Error }
                elseif ($t.Text -ne 'none') {
                    $dup = @($chosen.Keys | Where-Object { $chosen[$_] -eq $t.Text })
                    if ($dup.Count -gt 0) {
                        $other = ($KeyActions | Where-Object { $_[0] -eq $dup[0] })[1]
                        $problem = "$($t.Text) is already your key for '$other'. Choose a different one."
                    } elseif ($t.InUse) {
                        $problem = "$($t.Text) is already used by another program, so Windows will not give it to cctv-overlay. Choose a different one."
                        if ($fromFile -or $Unattended) { Warn $problem; $problem = $null } # keep it; the log will say so
                    }
                }
                if ($problem) {
                    if ($fromFile) { Die "answers file, $key = '$spec': $problem" }
                    Warn "$(Sentence $problem) Please try again."
                    Stop-AfterTooManyTries (++$tries) $label
                    continue
                }
                if ($t.Typing -and -not ($fromFile -or $Unattended)) {
                    Warn "$($t.Text) has no Ctrl, Alt or Win key, so while cctv-overlay runs it stops typing $($t.Text) in every other program."
                    if (-not (YesNo 'Use it anyway?' 'n')) { continue }
                }
                $chosen[$name] = $t.Text
                break
            }
        }
        if ($Unattended) { return $chosen }
        Write-Host ''
        Say '  Your keys:'
        foreach ($act in $KeyActions) {
            $v = $chosen[$act[0]]
            Say ('    {0,-26} {1}' -f $act[1], $(if ($v -eq 'none') { '(no key)' } else { $v }))
        }
        if (YesNo 'Use these keys?' 'y') { return $chosen }
        Info 'OK, choose them again.'
    }
}

function Write-KeysSection($keys, [System.Collections.Generic.List[string]]$L) {
    $L.Add('# Global keys (MODIFIERS + KEY, none = not bound). They work while cctv-overlay runs in the background.')
    $L.Add('[keys]')
    foreach ($k in $keys.Keys) { $L.Add("$k = $($keys[$k])") }
}

# Replaces the [keys] section of an existing config.ini (or adds one), keeping everything else.
function Set-KeysInConfig($keys) {
    $lines = [IO.File]::ReadAllLines($Conf)
    $out = New-Object System.Collections.Generic.List[string]
    $inKeys = $false; $done = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*\[(.+)\]\s*$') {
            $inKeys = $Matches[1] -eq 'keys'
            $out.Add($line)
            if ($inKeys -and -not $done) { foreach ($k in $keys.Keys) { $out.Add("$k = $($keys[$k])") }; $done = $true }
            continue
        }
        if ($inKeys -and $line -match '^\s*(toggle|next|prev|audio)\s*=') { continue }
        $out.Add($line)
    }
    if (-not $done) { $out.Add(''); Write-KeysSection $keys $out }
    Copy-Item $Conf "$Conf.bak.$TS"
    [IO.File]::WriteAllText($Conf, (($out -join "`r`n") + "`r`n"), $Utf8)
    if ((Invoke-Quiet --config $Conf check) -eq 2) { Copy-Item "$Conf.bak.$TS" $Conf -Force; Die 'the keys could not be saved (your config.ini was restored).' }
    Okay "keys saved (backup of the old file: $(Split-Path -Leaf "$Conf.bak.$TS"))"
}

function Get-ConfigSummary {
    $out = Invoke-Lines --config $Conf check
    $s = @{ Cameras = '?'; Keys = 'none'; Valid = ($LASTEXITCODE -ne 2); Error = ($out -join ' ') }
    foreach ($l in $out) {
        if ($l -match '^cameras:\s*(\d+)') { $s.Cameras = $Matches[1] }
        if ($l -match '^keys:\s*(.*)$') { $s.Keys = $Matches[1] }
    }
    return $s
}

# ---------------------------------------------------------------- config
function Write-Config {
    Step 'Your cameras'
    if (Test-Path $Conf) {
        $s = Get-ConfigSummary
        if ($s.Valid) {
            Okay "you already have a setup with $($s.Cameras) camera(s) ($Conf)"
            if (-not (YesNo 'Set up the cameras again? (your current file is backed up first)' 'n' 'OVERWRITE_CONFIG')) {
                Okay 'keeping your camera setup'
                Step 'Overlay position and size'
                Okay 'keeping your current position and size'
                Step 'Keys'
                Say "  Your keys: $($s.Keys)"
                $change = if (@($KeyActions | Where-Object { $A.ContainsKey($_[3]) }).Count -gt 0) { $true }
                          else { YesNo 'Change the keys?' 'n' 'CHANGE_KEYS' }
                if ($change) { Set-KeysInConfig (Read-Keys) }
                return
            }
        } else {
            Warn "your current config.ini has a problem: $($s.Error)"
            Info 'It will be replaced (a backup is kept).'
        }
    }

    Info 'Press Enter to accept the suggestion shown in [brackets].'
    $system = Choose 'Which camera system do you have?' 'dahua' 'SYSTEM' $Systems
    $hostName = ''; $port = '554'; $pathT = ''
    if ($system -ne 'standalone') {
        Write-Host ''
        Info "Your recorder's IP address is in its network settings, or in your router's list of devices."
        $hostName = Ask 'Recorder IP address (for example 192.168.1.100)' '' 'HOST' $CheckAddress { param($v) Normalize-Address $v }
        $defPort = '554'
        if ($hostName -match '^([^:\[\]]+):(\d+)$') { $hostName = $Matches[1]; $defPort = $Matches[2] } # pasted address:port
        $port = Ask 'RTSP port' $defPort 'PORT' $CheckPort
    }
    Write-Host ''
    Info 'Tip: create a user that can only view live video on the recorder, and use it here.'
    $username = Ask 'User name for the recorder/cameras' 'admin' 'USERNAME' $CheckNotEmpty
    if ($system -eq 'custom') {
        Info 'Placeholders: {channel} {channel02} {subtype} (0 main / 1 sub) {stream} (1 main / 2 sub) {mainsub}'
        $pathT = Ask 'RTSP path (for example /live/ch{channel}_{subtype})' '' 'PATH_TEMPLATE' $CheckPath { param($v) Normalize-Path $v }
    } elseif ($system -ne 'standalone') {
        $pathT = $SystemPath[$system]
    }
    Write-Host ''
    $stream = Choose 'Which picture quality for a single camera?' 'main' 'STREAM' @(
        @('main', 'Main stream: sharp, and on most recorders it appears fastest (uses more network)'),
        @('sub', 'Sub stream: lighter on network and computer, lower resolution'))

    Write-Host ''
    $ncam = Ask 'How many cameras do you have?' '4' 'CAMERAS' { param($v)
        if ($v -notmatch '^\d+$' -or [int]$v -lt 1 -or [int]$v -gt 64) { 'Type a number from 1 to 64.' } else { $null } }
    $script:names = @(); $ids = @(); $chans = @(); $hosts = @(); $paths = @(); $lastBrand = 'dahua'
    for ($i = 1; $i -le [int]$ncam; $i++) {
        Write-Host ''
        Say "  Camera $i of $ncam"
        $n = Ask 'Name to show on the overlay' "Camera $i" "CAMERA_${i}_NAME" { param($v)
            $e = & $CheckNotEmpty $v
            if ($e) { return $e }
            if ($script:names | Where-Object { $_.ToLowerInvariant() -eq $v.ToLowerInvariant() }) { return "You already have a camera called '$v'. Each name must be different." }
            $null }
        $id = Get-Slug $n; $c = 2
        while ($ids -contains $id) { $id = (Get-Slug $n) + $c; $c++ }
        $script:names += $n; $ids += $id
        if ($system -eq 'standalone') {
            $h = Ask "IP address of '$n'" '' "CAMERA_${i}_HOST" $CheckAddress { param($v) Normalize-Address $v }
            $brand = Choose "Make of '$n'?" $lastBrand "CAMERA_${i}_BRAND" $CamBrands
            $lastBrand = $brand
            if ($brand -eq 'custom') {
                $pth = Ask "RTSP path of '$n' (for example /live/main)" '' "CAMERA_${i}_PATH" $CheckPath { param($v) Normalize-Path $v }
            } else {
                $pth = $CameraPath[$brand]
            }
            $hosts += $h; $paths += $pth; $chans += '1'
        } else {
            $ch = Ask "Channel number of '$n' on the recorder" "$i" "CAMERA_${i}_CHANNEL" { param($v)
                if ($v -notmatch '^\d+$' -or [int]$v -lt 1 -or [int]$v -gt 1024) { 'The channel is the camera''s number on the recorder: a number from 1 to 1024.' } else { $null } }
            $chans += $ch; $hosts += ''; $paths += ''
        }
    }

    Step 'Overlay position and size'
    $displays = Get-Displays
    $monitorOpts = @(@('primary', 'Your main display'), @('focused', 'The display you are working on when you open it')) + $displays
    $monitor = Choose 'Which display should show the overlay?' 'primary' 'MONITOR' $monitorOpts { param($v)
        if ($v -match '^(\\\\\.\\)?DISPLAY\d+$') { return ($v -replace '^\\\\\.\\', '').ToUpperInvariant() }
        $null }
    Write-Host ''
    $size = Choose 'How big should the overlay be?' '720x405' 'SIZE' @(
        @('480x270', 'small'), @('600x338', 'medium'), @('720x405', 'large (recommended for the 4-camera grid)'), @('960x540', 'extra large')) { param($v)
        $v = $v -replace '\s', '' -replace '[*X]', 'x'
        if ($v -match '^(\d+)x(\d+)$' -and [int]$Matches[1] -ge 64 -and [int]$Matches[1] -le 7680 -and [int]$Matches[2] -ge 64 -and [int]$Matches[2] -le 4320) { return $v }
        $null } 'You can also type your own size as WIDTHxHEIGHT, for example 800x450.'
    $w, $hgt = $size -split 'x'
    Write-Host ''
    $anchor = Choose 'Which corner of the screen?' 'top-right' 'ANCHOR' @(
        @('top-right', ''), @('top-left', ''), @('bottom-right', ''), @('bottom-left', ''))

    Step 'Keys'
    $keys = Read-Keys

    New-Item -ItemType Directory -Force $ConfDir | Out-Null
    if (Test-Path $Conf) { Copy-Item $Conf "$Conf.bak.$TS"; Okay "your previous setup was saved as $(Split-Path -Leaf "$Conf.bak.$TS")" }
    $L = New-Object System.Collections.Generic.List[string]
    $L.Add("# cctv-overlay configuration, written by install.ps1 on $(Get-Date -Format 'yyyy-MM-dd').")
    $L.Add('# No secrets here: the password lives in secrets.ini (readable only by you). Re-run install.cmd to change')
    $L.Add('# the setup, or edit this file and run: cctv-overlay reload. Comments must be on their own line.')
    $L.Add('')
    $L.Add('[general]'); $L.Add('log_level = info'); $L.Add("monitor = $monitor"); $L.Add('')
    $L.Add('[overlay]'); $L.Add("width = $w"); $L.Add("height = $hgt"); $L.Add("anchor = $anchor")
    $L.Add('margin_x = 24'); $L.Add('margin_y = 24'); $L.Add('layer = overlay'); $L.Add('')
    $L.Add('[stream]'); $L.Add('transport = tcp'); $L.Add('# 0 = main stream, 1 = sub stream')
    $L.Add('subtype = ' + $(if ($stream -eq 'sub') { '1' } else { '0' }))
    $L.Add('hwdec = d3d11va'); $L.Add('reconnect = true')
    $L.Add('# Audio starts off; toggle it with: cctv-overlay audio'); $L.Add('audio = false'); $L.Add('')
    $L.Add('[recorder]')
    if ($system -ne 'standalone') { $L.Add("host = $hostName"); $L.Add("port = $port") }
    $L.Add("username = $username")
    if ($system -ne 'standalone') { $L.Add("# camera system: $system"); $L.Add("path = $pathT") }
    $L.Add('')
    Write-KeysSection $keys $L
    $L.Add('')
    $L.Add('# The grid (overview) view opens first on every toggle; next/prev then step through the cameras.')
    $L.Add('[grid]'); $L.Add('enabled = true')
    $L.Add('# cameras = ' + (($ids | Select-Object -First 4) -join ', '))
    $L.Add('')
    $L.Add('# Cameras, in cycling order.')
    for ($i = 0; $i -lt $script:names.Count; $i++) {
        $L.Add("[camera.$($ids[$i])]"); $L.Add("name = $($script:names[$i])")
        if ($hosts[$i]) { $L.Add("host = $($hosts[$i])"); $L.Add("path = $($paths[$i])") } else { $L.Add("channel = $($chans[$i])") }
        $L.Add('')
    }
    $tmp = Join-Path $ConfDir ".config.$TS.tmp"
    [IO.File]::WriteAllText($tmp, ($L -join "`r`n"), $Utf8)
    Move-Item -Force $tmp $Conf
    Okay "setup saved: $ncam camera(s), $w x $hgt, $anchor corner ($Conf)"
}

# ---------------------------------------------------------------- password
function Read-PasswordDialog([string]$user) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $f = New-Object System.Windows.Forms.Form
    $f.Text = 'cctv-overlay: recorder password'; $f.FormBorderStyle = 'FixedDialog'; $f.MaximizeBox = $false; $f.MinimizeBox = $false
    $f.StartPosition = 'CenterScreen'; $f.TopMost = $true; $f.ClientSize = New-Object System.Drawing.Size(400, 185)
    $lbl = New-Object System.Windows.Forms.Label
    $who = if ($user) { "of the recorder user '$user'" } else { 'of your recorder/cameras' }
    $lbl.Text = "Type the password $who.`nIt is saved only in a file that just your Windows account can read."
    $lbl.SetBounds(12, 10, 380, 36)
    $p1 = New-Object System.Windows.Forms.TextBox; $p1.UseSystemPasswordChar = $true; $p1.SetBounds(110, 54, 276, 24)
    $p2 = New-Object System.Windows.Forms.TextBox; $p2.UseSystemPasswordChar = $true; $p2.SetBounds(110, 86, 276, 24)
    $l1 = New-Object System.Windows.Forms.Label; $l1.Text = 'Password:'; $l1.SetBounds(12, 57, 95, 20)
    $l2 = New-Object System.Windows.Forms.Label; $l2.Text = 'Type it again:'; $l2.SetBounds(12, 89, 95, 20)
    $msg = New-Object System.Windows.Forms.Label; $msg.ForeColor = 'Firebrick'; $msg.SetBounds(12, 118, 380, 18)
    $okb = New-Object System.Windows.Forms.Button; $okb.Text = 'Save'; $okb.SetBounds(226, 146, 75, 28)
    $cb = New-Object System.Windows.Forms.Button; $cb.Text = 'Cancel'; $cb.SetBounds(311, 146, 75, 28); $cb.DialogResult = 'Cancel'
    $okb.Add_Click({
        if ($p1.Text.Length -eq 0) { $msg.Text = 'The password is empty. Please type it.'; return }
        if ($p1.Text -ne $p2.Text) { $msg.Text = 'The two passwords are different. Please type them again.'; $p2.Clear(); $p2.Focus(); return }
        $f.DialogResult = 'OK'; $f.Close()
    })
    $f.AcceptButton = $okb; $f.CancelButton = $cb
    $f.Controls.AddRange(@($lbl, $l1, $p1, $l2, $p2, $msg, $okb, $cb))
    $f.Add_Shown({ $f.Activate(); $p1.Focus() })
    $r = $f.ShowDialog()
    $pw = if ($r -eq 'OK') { $p1.Text } else { $null }
    $p1.Clear(); $p2.Clear(); $f.Dispose()
    return $pw
}

function ConvertFrom-Secure($s) {
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

# Returns the password, or $null when the user chose to skip.
function Read-PasswordConsole([string]$user) {
    $who = if ($user) { "of the recorder user '$user'" } else { 'of your recorder/cameras' }
    Say "  Type the password $who. It is saved only in a file that just your"
    Say '  Windows account can read, and it is never shown (each character appears as *).'
    while ($true) {
        $t1 = ConvertFrom-Secure (Read-Host '  Password' -AsSecureString)
        if (-not $t1) {
            if (YesNo 'The password is empty. Skip it for now? (set it later with install.cmd --set-password)' 'n') { return $null }
            continue
        }
        $t2 = ConvertFrom-Secure (Read-Host '  Type it again' -AsSecureString)
        if ($t1 -ne $t2) { Warn 'The two passwords are different. Please try again.'; continue }
        return $t1
    }
}

function Set-PrivateAcl([string]$path) {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetOwner($me)
    $acl.SetAccessRuleProtection($true, $false) # no inherited entries
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'Allow')))
    [IO.File]::SetAccessControl($path, $acl)
}

function Get-ConfigUser {
    if (-not (Test-Path $Conf)) { return '' }
    $m = Select-String -Path $Conf -Pattern '^\s*username\s*=\s*(.*?)\s*$' | Select-Object -First 1
    if ($m) { return $m.Matches[0].Groups[1].Value }
    return ''
}

# Returns $true when a password was saved.
function Set-Password {
    $user = Get-ConfigUser
    if (Test-Console) { $pw = Read-PasswordConsole $user }
    elseif ([Environment]::UserInteractive) {
        Say '  A password box is open on your desktop (it may be behind other windows). Type the password there.'
        $pw = Read-PasswordDialog $user
        if ($null -eq $pw) { Warn 'cancelled; the password was not changed'; return $false }
    } else {
        Die "there is no screen to ask for the password. Run this yourself in a terminal: $Here\install.cmd --set-password"
    }
    if ($null -eq $pw) { Warn 'skipped. Set it later with: install.cmd --set-password'; return $false }
    New-Item -ItemType Directory -Force $ConfDir | Out-Null
    if ($pw -match '^\s|\s$') {
        Warn 'the password starts or ends with a space, which is ignored when it is read. If logging in fails, see docs\configuration.md (CCTV_OVERLAY_PASSWORD).'
    }
    # Keep an optional username line from an existing secrets file (never its password).
    $suser = ''
    if (Test-Path $Secrets) {
        $m = Select-String -Path $Secrets -Pattern '^\s*username\s*=\s*(.*?)\s*$' | Select-Object -First 1
        if ($m) { $suser = $m.Matches[0].Groups[1].Value }
    }
    $tmp = Join-Path $ConfDir ".secrets.$([guid]::NewGuid().ToString('N').Substring(0, 8)).tmp"
    [IO.File]::WriteAllText($tmp, '', $Utf8)
    Set-PrivateAcl $tmp # restrict before the password is written
    $text = "# Written by install.ps1. Readable only by you. Never commit or share this file.`r`n[recorder]`r`n"
    if ($suser) { $text += "username = $suser`r`n" }
    $text += "password = $pw`r`n"
    [IO.File]::WriteAllText($tmp, $text, $Utf8)
    $pw = $null; $text = $null
    if (Test-Path $Secrets) { Remove-Item -Force $Secrets }
    Move-Item $tmp $Secrets # same volume: keeps the private permissions
    Okay "password saved (only your Windows account can read $(Tilde $Secrets))"
    if (Test-Path $Bin) { Invoke-Quiet --no-spawn reload | Out-Null } # a running daemon re-reads secrets on reload
    return $true
}

function Test-HasPassword { return (Test-Path $Secrets) -and (Select-String -Path $Secrets -Pattern '^\s*password\s*=\s*\S' -Quiet) }

# ---------------------------------------------------------------- sign-in start, PATH
# The user PATH is edited as stored (REG_EXPAND_SZ, %VARIABLES% unexpanded), never through a round trip that
# would expand or retype the other entries.
function Get-UserPath {
    $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    try { $raw = [string]$k.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $k.Close() }
    return @($raw -split ';' | Where-Object { $_ })
}
function Set-UserPath([string[]]$parts) {
    $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try { $k.SetValue('Path', ($parts -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString) }
    finally { $k.Close() }
    # Removing a variable that does not exist broadcasts WM_SETTINGCHANGE, so new terminals see the change.
    [Environment]::SetEnvironmentVariable('CCTV_OVERLAY_PATH_REFRESH', $null, 'User')
}
function Remove-FromPath {
    $parts = @(Get-UserPath)
    if ($parts -contains $Prefix) { Set-UserPath @($parts | Where-Object { $_ -ne $Prefix }) }
}

function Set-Autostart {
    Step 'Start with Windows'
    Info 'Your keys only work while cctv-overlay runs in the background. While the overlay is hidden it uses'
    Info 'no network and no video at all.'
    if (YesNo 'Start cctv-overlay automatically when you sign in to Windows?' 'y' 'AUTOSTART') {
        Set-ItemProperty -Path $RunKey -Name 'cctv-overlay' -Value "`"$BinW`" daemon"
        Okay 'it starts when you sign in (you can turn this off in Settings > Apps > Startup: cctv-overlayw)'
    } else {
        Remove-ItemProperty -Path $RunKey -Name 'cctv-overlay' -ErrorAction SilentlyContinue
        Warn "not started at sign-in: after a restart, your keys work once you run any 'cctv-overlay' command"
    }
    Write-Host ''
    if (YesNo "Make the 'cctv-overlay' command available in new terminal windows (adds it to PATH)?" 'y' 'ADD_TO_PATH') {
        $parts = @(Get-UserPath)
        if ($parts -notcontains $Prefix) { Set-UserPath ($parts + $Prefix); Okay "added (open a new terminal to use 'cctv-overlay')" }
        else { Okay 'already available' }
    }
}

function Start-Daemon {
    # 'ping' starts the windowless daemon
    if ((Invoke-Quiet ping) -ne 0) { Warn "cctv-overlay did not start; run '$Bin daemon' in a terminal to see why"; return }
    Start-Sleep -Milliseconds 300
    $keyWarn = @()
    if (Test-Path $LogFile) { $keyWarn = @(Get-Content $LogFile -Tail 40 | Where-Object { $_ -match 'cannot use key' }) }
    if ($keyWarn.Count -gt 0) {
        $keyWarn | ForEach-Object { Warn (($_ -replace '^.*cctv-overlay: warning: ', '')) }
    } else { Okay 'cctv-overlay is running in the background and your keys are active' }
}

# ---------------------------------------------------------------- live test
function Test-Live {
    if (-not (Test-HasPassword) -and -not $env:CCTV_OVERLAY_PASSWORD) {
        Warn 'no password set yet: run install.cmd --set-password, then install.cmd --test'; return $true
    }
    if ((Invoke-Quiet toggle) -ne 0) { Warn "cctv-overlay did not start; run '$Bin daemon' in a terminal to see why"; return $false }
    Write-Host '  The overlay is open. Connecting to your cameras' -NoNewline
    $st = $null
    for ($i = 1; $i -le 40; $i++) {
        try { $st = (Invoke-Lines --no-spawn status --json | Where-Object { $_.StartsWith('{') }) | ConvertFrom-Json } catch { }
        if ($st -and $i -gt 4 -and @($st.tiles | Where-Object { $_.state -in 'connecting', 'reconnecting' }).Count -eq 0) { break }
        Write-Host '.' -NoNewline
        Start-Sleep -Milliseconds 500
    }
    Write-Host ''
    $live = 0
    if ($st) {
        foreach ($t in $st.tiles) {
            if ($t.state -eq 'live') { $live++; Okay "$($t.camera): live" }
            elseif ($t.state -in 'connecting', 'reconnecting') { Warn "$($t.camera): no picture yet (still trying)" }
            else { Bad "$($t.camera): $($t.state)" }
        }
        if ($st.error) { Warn "last problem reported: $($st.error)" }
    }
    Start-Sleep -Seconds 2
    Invoke-Quiet --no-spawn hide | Out-Null
    if ($live -gt 0) { Okay 'your cameras are reachable'; return $true }
    Warn 'no camera connected. Check the recorder address, user name and password (install.cmd --set-password),'
    Warn 'that RTSP is turned on in the recorder''s network settings, and your firewall.'
    Info "More detail: $LogFile, or docs\troubleshooting.md"
    return $false
}

function Show-Done {
    $s = Get-ConfigSummary
    Write-Host ''
    Write-Host 'All done.' -ForegroundColor Green
    $keys = $s.Keys -replace 'toggle = ', 'show/hide: ' -replace 'next = ', 'next: ' -replace 'prev = ', 'previous: ' -replace 'audio = ', 'sound: '
    Say "  Keys:    $($keys -replace ', ', '   ')"
    Say '           The overlay opens on the 4-camera grid; next/previous step through the cameras.'
    Say "  Config:  $Conf   (after editing it, run: cctv-overlay reload)"
    Say '  Change:  run install.cmd again at any time. Password: install.cmd --set-password'
    Say "  Logs:    $LogFile"
}

# ---------------------------------------------------------------- modes
try {
    switch ($Mode) {
        'check' { if (Test-Requirements) { Write-Host ''; Okay 'this computer is ready for cctv-overlay'; Finish 0 } else { Finish 1 } }
        'password' {
            Step 'Recorder password'
            if (Set-Password) { Finish 0 } else { Finish 1 }
        }
        'test' {
            if (-not (Test-Path $Bin)) { Die 'cctv-overlay is not installed yet: run install.cmd first' }
            Step 'Testing your cameras'
            if (Test-Live) { Finish 0 } else { Finish 1 }
        }
        'uninstall' {
            Step 'Uninstalling cctv-overlay'
            if (-not (YesNo 'Remove cctv-overlay from this computer?' 'y' 'UNINSTALL')) { Okay 'nothing was changed'; Finish 0 }
            Stop-Daemon
            Remove-ItemProperty -Path $RunKey -Name 'cctv-overlay' -ErrorAction SilentlyContinue
            Remove-FromPath
            if (Test-Path $Prefix) { Remove-Item -Recurse -Force $Prefix }
            Okay 'removed the program, its start at sign-in and its PATH entry'
            if (YesNo "Also delete your camera setup and password ($(Tilde $ConfDir)) and the logs?" 'n' 'DELETE_CONFIG') {
                Remove-Item -Recurse -Force $ConfDir, $StateDir -ErrorAction SilentlyContinue
                Okay 'deleted your setup and password'
            } else { Okay "kept your setup in $ConfDir (used again if you reinstall)" }
            Finish 0
        }
    }

    Write-Host 'cctv-overlay installer' -ForegroundColor Cyan
    Say 'This sets up cctv-overlay for your Windows account. No administrator rights are needed, and nothing'
    Say 'is changed until a step says so. It takes a few minutes.'
    if (-not $Unattended) { Info 'Press Enter to accept a suggestion shown in [brackets]. Press Ctrl+C to stop at any time.' }
    $script:StepCount = 8

    if (-not (Test-Requirements)) { Die 'this computer is missing something cctv-overlay needs (see the X lines above, and docs\requirements.md)' }
    Build-Install
    Write-Config
    $s = Get-ConfigSummary
    if (-not $s.Valid) { Die "the configuration is not valid: $($s.Error)" }

    Step 'Recorder password'
    if (Test-HasPassword) {
        Okay 'a password is already saved'
        if (-not $Unattended -and (YesNo 'Change it?' 'n')) { Set-Password | Out-Null }
    } elseif (-not $Unattended) {
        Set-Password | Out-Null
    } else {
        Warn 'not set. The user must type it themselves (never in an AI chat or a file). Run:'
        Say "    $Here\install.cmd --set-password"
        Say "    then test the cameras with: $Here\install.cmd --test"
    }

    Set-Autostart
    Start-Daemon

    Step 'Testing your cameras'
    if (-not (Test-HasPassword)) { Info 'Skipped: no password yet.' }
    elseif (YesNo 'Open the overlay now to check that every camera connects?' 'y' 'RUN_TEST') { Test-Live | Out-Null }
    else { Info 'Skipped. Run install.cmd --test at any time.' }

    Show-Done
    Finish 0
} catch {
    Write-Host ''
    Write-Host 'Something went wrong: ' -ForegroundColor Red -NoNewline
    Write-Host $_.Exception.Message
    Info "($($_.InvocationInfo.PositionMessage -replace '\s+', ' '))"
    Say 'Nothing you set up so far is lost. Run install.cmd again; it keeps what is already done.'
    Finish 1
}
