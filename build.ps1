<#
  Build cctv-overlay with the C# compiler that ships with Windows (.NET Framework 4.8) - nothing to install.

    .\build.ps1            real build: build\cctv-overlay.exe (CLI + foreground daemon) and
                           build\cctv-overlayw.exe (the same program without a console window, for the daemon)
    .\build.ps1 check      headless unit tests + control-pipe tests (stub video and overlay)
    .\build.ps1 smoke      real overlay + libmpv with test patterns (shows the overlay briefly)
    .\build.ps1 demo       demo build with synthetic cameras (see tools\demo\run-demo.ps1)
    .\build.ps1 clean      remove build\

  The program needs libmpv-2.dll next to the .exe at run time; install.ps1 downloads it.
#>
param([string]$Target = 'build')
$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Out = Join-Path $Root 'build'

function Find-Csc {
    $dirs = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319", "$env:WINDIR\Microsoft.NET\FrameworkArm64\v4.0.30319",
              "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319")
    foreach ($d in $dirs) { if (Test-Path "$d\csc.exe") { return "$d\csc.exe" } }
    throw "csc.exe (.NET Framework 4.x) was not found. Windows 10/11 include it; enable '.NET Framework 4.8 Advanced Services' in Windows Features if it is missing."
}

function Invoke-Csc([string]$OutFile, [string]$Kind, [string[]]$Defines, [string[]]$Files, [string]$Main) {
    $csc = Find-Csc
    New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
    $cscArgs = @('/nologo', '/optimize+', '/warn:4', '/codepage:65001', '/platform:anycpu', "/target:$Kind",
                 "/out:$OutFile", "/win32manifest:$Root\src\app.manifest",
                 '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
                 '/r:System.Management.dll')
    if ($Defines.Count -gt 0) { $cscArgs += "/define:$($Defines -join ';')" }
    if ($Main) { $cscArgs += "/main:$Main" }
    $cscArgs += $Files
    $output = & $csc @cscArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "build failed: $OutFile"
    }
    $output | Where-Object { "$_" -match 'warning' } | ForEach-Object { Write-Host $_ }
}

$Src = @(Get-ChildItem "$Root\src\*.cs" | ForEach-Object { $_.FullName })

function Build-Real {
    Invoke-Csc "$Out\cctv-overlay.exe" 'exe' @() $Src
    Invoke-Csc "$Out\cctv-overlayw.exe" 'winexe' @('WINEXE') $Src
    Write-Output "built build\cctv-overlay.exe and build\cctv-overlayw.exe"
}

function Build-Stub {
    Invoke-Csc "$Out\stub\cctv-overlay.exe" 'exe' @('STUB') $Src
    Invoke-Csc "$Out\stub\cctv-overlayw.exe" 'winexe' @('STUB', 'WINEXE') $Src
    $tests = @(Get-ChildItem "$Root\tests\*.cs" | ForEach-Object { $_.FullName })
    Invoke-Csc "$Out\stub\tests.exe" 'exe' @('STUB', 'TESTS') ($Src + $tests) 'CctvOverlay.Tests.Runner'
}

switch ($Target) {
    'build' { Build-Real }
    'demo' {
        # Demo build: also accepts `url = demo:<scene>` (synthetic cameras). Used by tools\demo\run-demo.ps1.
        $dll = @("$Out\libmpv-2.dll", "$Root\libmpv-2.dll", "$env:LOCALAPPDATA\Programs\cctv-overlay\libmpv-2.dll") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $dll) { throw 'libmpv-2.dll not found (run install.cmd first, or put it in build\ or the repository root)' }
        $demoSrc = $Src + @(Get-ChildItem "$Root\tools\demo\*.cs" | Where-Object { $_.Name -ne 'Director.cs' } | ForEach-Object { $_.FullName })
        Invoke-Csc "$Out\demo\cctv-overlay.exe" 'exe' @('DEMO') $demoSrc
        Invoke-Csc "$Out\demo\cctv-overlayw.exe" 'winexe' @('DEMO', 'WINEXE') $demoSrc
        if (Test-Path "$Root\tools\demo\Director.cs") {
            Invoke-Csc "$Out\demo\director.exe" 'exe' @('DEMO', 'DIRECTOR') ($demoSrc + "$Root\tools\demo\Director.cs") 'CctvOverlay.Demo.Director'
        }
        Copy-Item $dll "$Out\demo\libmpv-2.dll" -Force
        Write-Output "built build\demo"
    }
    'smoke' {
        # Developer check of the real overlay + libmpv (test patterns, no cameras). Shows the overlay briefly.
        $dll = @("$Out\libmpv-2.dll", "$Root\libmpv-2.dll", "$env:LOCALAPPDATA\Programs\cctv-overlay\libmpv-2.dll") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $dll) { throw 'libmpv-2.dll not found (put it in build\ or the repository root, or run install.cmd first)' }
        Invoke-Csc "$Out\smoke\smoke.exe" 'exe' @('SMOKE') ($Src + "$Root\tools\dev\VideoSmoke.cs") 'CctvOverlay.Dev.Smoke'
        Copy-Item $dll "$Out\smoke\libmpv-2.dll" -Force
        & "$Out\smoke\smoke.exe" "$Out\smoke"
        exit $LASTEXITCODE
    }
    'clean' { if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }; Write-Host 'clean' }
    'check' {
        Build-Stub
        Push-Location $Root
        try {
            & "$Out\stub\tests.exe"
            $unit = $LASTEXITCODE
            & powershell -NoProfile -ExecutionPolicy Bypass -File "$Root\tests\test_ipc.ps1" "$Out\stub\cctv-overlay.exe"
            $ipc = $LASTEXITCODE
        } finally { Pop-Location }
        if ($unit -ne 0 -or $ipc -ne 0) { Write-Host "check FAILED (unit=$unit, ipc=$ipc)"; exit 1 }
        Write-Host 'check passed'
    }
    default { throw "unknown target '$Target' (use build, check or clean)" }
}
