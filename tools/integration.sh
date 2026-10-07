#!/usr/bin/env bash
# Integration testing. 1) unit tests (gate) 2) part 1: big systems at full scale with performance budgets
# 3) part 2: the real game driven by the AutoPilot through its real UI and input, with screenshots.
# Usage: tools/integration.sh [quick]     Report: test-results/game_<timestamp>.md
source "$(dirname "$0")/env.sh"
cd "$PROJECT_ROOT"
QUICK=$1
STAMP=$(date +%Y%m%d_%H%M%S)
REPORT="$PROJECT_ROOT/test-results/game_$STAMP.md"
mkdir -p test-results logs screenshots
FAILS=0

tools/build.sh || exit 1
echo "== unit tests"
tools/test.sh | grep -q "Passed!" || { echo "UNIT TESTS FAILED - not launching the game"; exit 1; }

echo "== integration part 1"
dotnet run -c Release --no-build --project tests/Remade.Integration -- ${QUICK:+--quick} | tail -20 || FAILS=$((FAILS+1))

echo "# Game integration runs — $STAMP" > "$REPORT"
echo "" >> "$REPORT"
echo "| Scenario | Result | Notes |" >> "$REPORT"
echo "|---|---|---|" >> "$REPORT"

run() {  # name, timeout, args...
  local name=$1; local tmo=$2; shift 2
  rm -f screenshots/*.png
  local out="$PROJECT_ROOT/logs/run_${name}_$STAMP.txt"
  timeout "$tmo" "$GODOT" --path game -- --windowed "$@" > "$out" 2>&1
  local code=$?
  rm -rf "screenshots/$name"; mkdir -p "screenshots/$name"; mv screenshots/*.png "screenshots/$name/" 2>/dev/null
  local result=$(grep -o "AUTOPILOT RESULT: [A-Z]*.*" "$out" | head -1)
  local errors=$(grep -c "\[ERROR\]" "$out")
  local perf=$(grep "\[perf\]" "$out" | tail -1 | grep -oE "fps [0-9]+|frame [0-9,]+ ms|draws [0-9]+|prims [0-9]+k|vram [0-9]+ MB" | tr '\n' ' ')
  if [[ $code -eq 0 && "$result" == *PASS* && $errors -eq 0 ]]; then status=PASS; else status=FAIL; FAILS=$((FAILS+1)); fi
  echo "$status  $name  (exit $code, $errors errors) $perf"
  echo "| $name | $status | exit $code, $errors logged errors, ${result:-no autopilot result} $perf |" >> "$REPORT"
}

echo "== integration part 2 (real game)"
run ui_flow 420 --auto="wait=3;shot=menu;click=Play;wait=1;shot=scenario;click=Next;wait=2;shot=colonists;click=Randomize;wait=1;click=Next;wait=1;shot=worldgen;click=Generate;waitfor=planet;wait=1.5;shot=planet;select_tile=start;wait=2;hover=start;hovertext=Temperate;shot=planet_selected;click=Temperature;wait=3;hovertext=°C;shot=overlay_temperature;click=Precipitation;wait=3;hovertext=mm/day;shot=overlay_precipitation;click=Elevation;wait=1.5;hovertext= m;shot=overlay_elevation;click=Biomes;hover=lake;wait=2;hovertext=Lake;shot=lake;click=Next;wait=1;shot=map_sizes;mapsize=300;click==Play;waitfor=game;wait=5;shot=colony;world;waitfor=globe;wait=2;click=Temperature;wait=3;shot=ingame_planet_temperature;world;wait=1;key=menu;wait=1;click=Options;wait=1;shot=options;key=menu;wait=0.5;quit"
run goal 420 --play --map=250 --hour=11 --weather=clear --auto="waitfor=game;wait=3;control=0;wait=1;shot=control;walkto=door;interact=Open door;wait=1;shot=door_open;expect=dooropen;walkto=item:bow;interact=bow;wait=0.5;expect=bow;walkto=item:arrow;interact=arrow;walkto=item:arrow;interact=arrow;expect=arrows;tab=Equipment;wait=1;shot=equipment;inv=putaway;expect=bowaway;shot=bow_put_away;inv=equip:bow;expect=bow;shot=bow_reequipped;tab=Bio;wait=0.5;shot=bio;tab=Needs;wait=0.5;shot=needs;tab=Health;wait=0.5;shot=health;tab=Health;walkto=outside;deer_near=9;wait=0.5;shot=deer;cam=8;aim=on;wait=1.2;shot=aiming_close;cam=150;wait=2;shot=aiming_far;aim=off;cam=30;wait=1;aimshoot=40;wait=1;shot=after_shot;expect=deerdead;walkto=item:venison;interact=venison;wait=0.5;expect=meat;shot=meat;quit"
run mining 300 --play --map=250 --hour=11 --weather=clear --auto="waitfor=game;wait=2;control=0;walkto=rock;wait=0.5;shot=at_rock;interact=Mine;wait=0.5;expect=mined;shot=mined;quit"
run saveload 420 --play --map=300 --auto="waitfor=game;wait=2;speed=3;wait=4;key=menu;wait=0.5;click=Save game;wait=1;shot=saved;click=Main menu;waitfor=menu;click=Load game;wait=1;shot=load_menu;click=New Hope;wait=0.3;click==Load;waitfor=game;wait=4;shot=loaded;quit"
run perf_1000 420 --play --map=1000 --novsync --perf --hour=10 --weather=clear --auto="waitfor=game;wait=6;shot=near;cam=90;wait=6;shot=far;cam=30;speed=4;wait=8;shot=ultra;quit"
if [[ -z $QUICK ]]; then
  run perf_1500 420 --play --map=1500 --novsync --perf --hour=10 --weather=clear --auto="waitfor=game;wait=6;cam=90;wait=6;shot=far;quit"
  run visuals 600 --play --map=250 --hour=6 --auto="waitfor=game;wait=4;shot=dawn;hour=12;wait=3;shot=noon;hour=19;wait=3;shot=dusk;hour=23;wait=3;shot=night;quit"
  run rain 300 --play --map=250 --hour=13 --weather=rain --auto="waitfor=game;wait=6;shot=rain;quit"
  run snow 300 --play --map=250 --hour=13 --weather=snow --auto="waitfor=game;wait=8;shot=snow;quit"
  run fog 300 --play --map=250 --hour=7 --weather=fog --auto="waitfor=game;wait=6;shot=fog;quit"
fi

echo ""
if [[ $FAILS -eq 0 ]]; then echo "ALL INTEGRATION TESTS PASSED ($REPORT)"; else echo "$FAILS INTEGRATION FAILURES ($REPORT)"; fi
exit $FAILS
