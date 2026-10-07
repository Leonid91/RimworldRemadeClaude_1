#!/usr/bin/env bash
# Builds (C#) and launches Rimworld Remade on macOS or Linux. Extra arguments go to the game, e.g.  ./play.sh --windowed
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
GODOT="${GODOT:-}"
if [ -z "$GODOT" ]; then
  for g in "$ROOT/Godot_mono.app/Contents/MacOS/Godot" "/Applications/Godot_mono.app/Contents/MacOS/Godot" \
           "$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot"; do
    [ -x "$g" ] && GODOT="$g" && break
  done
fi
[ -z "$GODOT" ] && GODOT="$(command -v godot || command -v godot-mono || true)"
[ -z "$GODOT" ] && { echo "Godot 4.7 .NET not found: put Godot_mono.app in Applications or set GODOT=/path/to/godot"; exit 1; }
command -v dotnet >/dev/null || { echo ".NET SDK not found: install it from https://dotnet.microsoft.com/download"; exit 1; }
mkdir -p "$ROOT/logs"
dotnet build "$ROOT/game/RimworldRemade.csproj" --nologo -v q > "$ROOT/logs/build_last.log" 2>&1 \
  || { grep -i error "$ROOT/logs/build_last.log"; echo "Build failed, see logs/build_last.log"; exit 1; }
exec "$GODOT" --path "$ROOT/game" -- "$@"
