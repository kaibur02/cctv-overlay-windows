<#
  Try cctv-overlay without any cameras: five synthetic "cameras" (tools\demo\DemoScenes.cs, rendered by libmpv)
  shown by a separate demo daemon with its own pipe, config and log, so a real setup is not touched.
  This is how the screenshots and GIFs in docs\media were made.

    tools\demo\run-demo.ps1 start [DISPLAY]               build the demo and start its daemon (DISPLAY default: primary)
    tools\demo\run-demo.ps1 ctl toggle|next|prev|audio|status ...   control the demo daemon
    tools\demo\run-demo.ps1 stop

  Needs libmpv-2.dll (run install.cmd once, or put it in build\). No credentials are involved.
#>
param([Parameter(Position = 0)][string]$Command = '', [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest = @())
# 'Continue': Windows PowerShell 5.1 turns a program's stderr output into errors, which 'Stop' would make fatal.
$ErrorActionPreference = 'Continue'
$Root = (Resolve-Path "$PSScriptRoot\..\..").Path
$Run = Join-Path $env:TEMP 'cctv-overlay-demo'
$Pipe = 'cctv-overlay-demo'
$Exe = "$Root\build\demo\cctv-overlay.exe"
$ExeW = "$Root\build\demo\cctv-overlayw.exe"

function Ctl { & $Exe --socket $Pipe --config "$Run\config.ini" --no-spawn @args }

switch ($Command) {
    'start' {
        $mon = if ($Rest.Count -gt 0) { $Rest[0] } else { 'primary' }
        if (Test-Path $Exe) { & $Exe --socket $Pipe --no-spawn quit *> $null; Start-Sleep -Milliseconds 500 }
        & powershell -NoProfile -ExecutionPolicy Bypass -File "$Root\build.ps1" demo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'demo build failed' }
        New-Item -ItemType Directory -Force $Run | Out-Null
        $cams = @(@('front', 'Front Door'), @('driveway', 'Driveway'), @('backyard', 'Backyard'), @('garage', 'Garage'), @('alley', 'Side Alley'))
        $L = @('[general]', "monitor = $mon", '[overlay]', 'width = 720', 'height = 405', 'anchor = top-right',
               '[stream]', 'subtype = 0')
        foreach ($c in $cams) { $L += "[camera.$($c[0])]"; $L += "name = $($c[1])"; $L += "url = demo:$($c[0])" }
        [IO.File]::WriteAllText("$Run\config.ini", ($L -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
        Ctl quit *> $null
        $env:CCTV_OVERLAY_STATE_DIR = "$Run\state"
        Start-Process -FilePath $ExeW -ArgumentList @('--socket', $Pipe, 'daemon', '--config', "`"$Run\config.ini`"")
        for ($i = 0; $i -lt 50; $i++) { Ctl ping *> $null; if ($LASTEXITCODE -eq 0) { break }; Start-Sleep -Milliseconds 100 }
        if ($LASTEXITCODE -ne 0) { Get-Content "$Run\state\daemon.log" -Tail 20 -ErrorAction SilentlyContinue; throw 'demo daemon did not start' }
        Write-Host "demo running on '$mon'. Try: tools\demo\run-demo.ps1 ctl toggle"
    }
    'ctl' { Ctl @Rest; exit $LASTEXITCODE }
    'stop' { Ctl quit *> $null; Write-Host 'demo stopped' }
    default { Get-Content $PSCommandPath -TotalCount 11 | Select-Object -Skip 1 | ForEach-Object { Write-Host $_ }; exit 2 }
}
