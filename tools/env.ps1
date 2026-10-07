# Dot-source to put the toolchain on PATH:  . .\tools\env.ps1
# Uses the portable toolchain (D:\Experiments\ExperimentTools) when present, otherwise $env:GODOT or Godot/dotnet on PATH.
$Tools = 'D:\Experiments\ExperimentTools'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$global:ProjectRoot = Split-Path -Parent $PSScriptRoot
$global:GameDir = Join-Path $global:ProjectRoot 'game'
if (Test-Path "$Tools\Godot") {
    $env:DOTNET_ROOT = "$Tools\dotnet"
    $env:PATH = "$Tools\dotnet;$env:PATH"
    $global:Godot = "$Tools\Godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
    $global:GodotConsole = "$Tools\Godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
} else {
    $global:Godot = $env:GODOT
    if (-not $global:Godot) {
        $found = Get-Command godot, godot_mono, Godot_v4.7.2-stable_mono_win64 -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $global:Godot = $found.Source }
    }
    if (-not $global:Godot) {
        $found = Get-ChildItem $global:ProjectRoot -Recurse -Depth 2 -Filter 'Godot_v4*_mono_win64.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '*console*' } | Select-Object -First 1
        if ($found) { $global:Godot = $found.FullName }
    }
    if (-not $global:Godot) { Write-Host 'Godot 4.7 .NET not found: put its folder next to Play.bat, add it to PATH or set GODOT.'; exit 1 }
    $global:GodotConsole = $global:Godot
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host '.NET SDK not found: install it from https://dotnet.microsoft.com/download'; exit 1 }
