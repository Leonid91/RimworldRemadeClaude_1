# Source from Git Bash:  source tools/env.sh
# Puts the portable toolchain (D:\Experiments\ExperimentTools) on PATH.
TOOLS=/d/Experiments/ExperimentTools
export DOTNET_ROOT='D:\Experiments\ExperimentTools\dotnet'
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export PATH="$TOOLS/dotnet:$PATH"
export GODOT="$TOOLS/Godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
export GODOT_GUI="$TOOLS/Godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64.exe"
export PY="$TOOLS/pyenv/Scripts/python.exe"
export PROJECT_ROOT=/d/Experiments/RimworldRemadeClaude_1
export GAME_DIR=$PROJECT_ROOT/game
