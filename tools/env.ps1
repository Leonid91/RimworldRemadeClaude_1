# Dot-source to put the portable toolchain on PATH:  . .\tools\env.ps1
$Tools = 'D:\Experiments\ExperimentTools'
$env:DOTNET_ROOT = "$Tools\dotnet"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:PATH = "$Tools\dotnet;$env:PATH"
$global:Godot = "$Tools\Godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
$global:GodotConsole = "$Tools\Godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
$global:ProjectRoot = Split-Path -Parent $PSScriptRoot
$global:GameDir = Join-Path $global:ProjectRoot 'game'
