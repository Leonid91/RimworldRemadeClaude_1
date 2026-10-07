#!/usr/bin/env bash
# Builds the simulation library, the unit tests, the integration runner and the game. Output: logs/build_*.log
set -e
source "$(dirname "$0")/env.sh"
mkdir -p "$PROJECT_ROOT/logs"
LOG="$PROJECT_ROOT/logs/build_$(date +%Y%m%d_%H%M%S).log"
cd "$PROJECT_ROOT"
{
  echo "== build $(date '+%Y-%m-%d %H:%M:%S') commit $(git rev-parse --short HEAD 2>/dev/null || echo nogit)"
  dotnet build tests/Remade.Sim.Tests/Remade.Sim.Tests.csproj --nologo -v q
  dotnet build tests/Remade.Integration/Remade.Integration.csproj -c Release --nologo -v q
  dotnet build game/RimworldRemade.csproj --nologo -v q
} 2>&1 | tee "$LOG" | grep -E "error|Warn|Build succeeded" || true
grep -q " error " "$LOG" && { echo "BUILD FAILED (see $LOG)"; exit 1; }
echo "build ok ($LOG)"
