# Builds the game and runs it. Extra arguments go to the game, e.g.  .\tools\run.ps1 --windowed --play
# -Editor opens the Godot editor instead.
param([switch]$Editor, [Parameter(ValueFromRemainingArguments = $true)] $GameArgs)
. "$PSScriptRoot\env.ps1"
$logDir = Join-Path $ProjectRoot 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$buildLog = Join-Path $logDir ("build_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".log")
dotnet build "$GameDir\RimworldRemade.csproj" --nologo -v q *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content $buildLog | Select-String "error"; Write-Host "Build failed, see $buildLog"; exit $LASTEXITCODE }
if ($Editor) { & $Godot --path $GameDir -e; exit 0 }
& $Godot --path $GameDir -- @GameArgs
