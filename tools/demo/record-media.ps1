<#
  Re-creates the screenshots, GIFs and MP4s in docs\media from the synthetic demo cameras. It takes over the
  screen for about two minutes: do not touch the mouse or keyboard while it runs.

    tools\demo\record-media.ps1 [usage|install|encode|all]     (default: all)

  - usage:   a full-screen editor backdrop, the overlay driven through its keys (captions show the keys), and the
             hero / grid / single-camera stills.
  - install: install.cmd in its own console, in a throwaway profile folder (your real setup, PATH and sign-in
             entries are not touched), answered by typing and by pressing real key combinations.
  - encode:  MP4 (H.264) and GIF versions, made with libmpv's encoder.

  Your installed cctv-overlay (if it is running) is paused so its keys do not interfere, and started again after.
#>
param([string]$What = 'all')
$ErrorActionPreference = 'Continue' # Windows PowerShell 5.1 turns a program's stderr output into errors
$Root = (Resolve-Path "$PSScriptRoot\..\..").Path
$Media = "$Root\docs\media"
$Work = Join-Path $env:TEMP 'cctv-overlay-media'
$Director = "$Root\build\demo\director.exe"
$Real = Join-Path $env:LOCALAPPDATA 'Programs\cctv-overlay\cctv-overlay.exe'
New-Item -ItemType Directory -Force $Media, $Work | Out-Null

$realWasRunning = $false
if (Test-Path $Real) { & $Real --no-spawn ping *> $null; $realWasRunning = $LASTEXITCODE -eq 0; & $Real --no-spawn quit *> $null }

try {
    if ($What -in 'usage', 'all') {
        & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\run-demo.ps1" start primary
        & $Director usage $Work "$Root\src\App.cs"
        Write-Host "usage: exit $LASTEXITCODE"
        & powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\run-demo.ps1" stop
        foreach ($f in 'hero.jpg', 'grid.png', 'single-audio.png') { if (Test-Path "$Work\$f") { Copy-Item "$Work\$f" $Media -Force } }
    }
    if ($What -in 'install', 'all') {
        # A throwaway profile: the installer shows ~\AppData\... and writes nothing outside it (answers: no
        # sign-in start, no PATH change).
        $Prof = Join-Path $Work 'profile'
        if (Test-Path $Prof) { Remove-Item -Recurse -Force $Prof }
        $Repo = "$Prof\cctv-overlay-windows"
        New-Item -ItemType Directory -Force "$Prof\AppData\Roaming", "$Prof\AppData\Local", $Repo | Out-Null
        foreach ($d in 'src', 'examples', 'docs', 'tools', 'tests') { Copy-Item -Recurse "$Root\$d" $Repo }
        Copy-Item "$Root\install.ps1", "$Root\install.cmd", "$Root\build.ps1", "$Root\README.md", "$Root\LICENSE" $Repo
        Copy-Item "$Root\build\demo\libmpv-2.dll" $Repo
        # The registry is not part of the throwaway profile: keep the real sign-in entry exactly as it is.
        $RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $realRun = (Get-ItemProperty $RunKey -Name cctv-overlay -ErrorAction SilentlyContinue)
        $saved = @{ USERPROFILE = $env:USERPROFILE; APPDATA = $env:APPDATA; LOCALAPPDATA = $env:LOCALAPPDATA }
        $env:USERPROFILE = $Prof; $env:APPDATA = "$Prof\AppData\Roaming"; $env:LOCALAPPDATA = "$Prof\AppData\Local"
        try {
            & $Director install $Work $Repo
            Write-Host "install: exit $LASTEXITCODE"
            $sx = "$Prof\AppData\Local\Programs\cctv-overlay\cctv-overlay.exe"
            if (Test-Path $sx) { & $sx --no-spawn quit *> $null }
        } finally {
            foreach ($k in $saved.Keys) { Set-Item "env:$k" $saved[$k] }
            if ($realRun) { Set-ItemProperty $RunKey -Name cctv-overlay -Value $realRun.'cctv-overlay' }
            else { Remove-ItemProperty $RunKey -Name cctv-overlay -ErrorAction SilentlyContinue }
        }
        Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Prof*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
        Remove-Item -Recurse -Force $Prof -ErrorAction SilentlyContinue
    }
    if ($What -in 'encode', 'all') {
        & $Director encode "$Work\usage-frames" "$Media\usage.mp4" 15 1280
        & $Director encode "$Work\usage-frames" "$Media\usage.gif" 15 800
        & $Director encode "$Work\install-frames" "$Media\install.mp4" 12 0
        & $Director encode "$Work\install-frames" "$Media\install.gif" 12 800
        Get-ChildItem $Media | Format-Table Name, Length -AutoSize
    }
} finally {
    if ($realWasRunning) { & $Real ping *> $null; Write-Host 'your cctv-overlay was started again' }
    Get-Content "$Work\director.log" -Tail 15 -ErrorAction SilentlyContinue
}
