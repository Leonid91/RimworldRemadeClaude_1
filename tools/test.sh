#!/usr/bin/env bash
# Unit tests: deterministic logic in isolation. Must pass before any integration run.
set -e
source "$(dirname "$0")/env.sh"
cd "$PROJECT_ROOT"
dotnet test tests/Remade.Sim.Tests/Remade.Sim.Tests.csproj --nologo -v q 2>&1 | tee "$PROJECT_ROOT/logs/unittests_$(date +%Y%m%d_%H%M%S).log" | grep -E "Passed!|Failed!|error|  Failed "
