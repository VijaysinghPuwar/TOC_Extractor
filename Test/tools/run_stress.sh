#!/bin/bash
# Stress the C# CLI against a local copy of a book, and record how this machine copes.
#
#   Test/tools/run_stress.sh [MACHINE_NAME]
#
# Nothing leaves the machine: the book is served from 127.0.0.1 and every
# page comes from corpus.jsonl.gz. Results land in Test/<machine>/<date>/;
# the downloaded chapter text goes to a temporary folder and is deleted.
#
# Phases, each pulling the whole book with every delay at zero:
#   single  one process, 3 -> 8 -> 16 -> 32 tabs
#   swarm   N processes at once, 4 tabs each, N rising until a run fails
#
# Everything runs in a systemd scope capped at MEM_CAP of RAM with no swap,
# so running out of memory kills the test, never the desktop.
#
# Tunables (environment): CHAPTERS=2334 TABS="3 8 16 32" SWARM="20 30 40" (empty skips a phase)
#                         SWARM_TABS=4 MEM_CAP=75% PORT=8765
set -uo pipefail

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
TOOLS=$ROOT/Test/tools
MACHINE=${1:-$(hostname -s)}
CHAPTERS=${CHAPTERS:-2334}
TABS=${TABS-3 8 16 32}
SWARM=${SWARM-20 30 40}
SWARM_TABS=${SWARM_TABS:-4}
MEM_CAP=${MEM_CAP:-75%}
PORT=${PORT:-8765}

OUT=$ROOT/Test/$MACHINE/$(date +%Y-%m-%d)
CLI=$ROOT/dotnet/src/TocExtractor.Cli/bin/Release/net10.0/toc-extractor

# Re-enter inside the capped scope unless already there.
if [ -z "${TOC_STRESS_SCOPED:-}" ] && command -v systemd-run >/dev/null; then
    exec systemd-run --user --scope --quiet -p MemoryMax="$MEM_CAP" -p MemorySwapMax=0 \
        env TOC_STRESS_SCOPED=1 "$0" "$@"
fi

WORK=$(mktemp -d)
SERVER=
trap '[ -n "$SERVER" ] && kill $SERVER 2>/dev/null; rm -rf "$WORK"' EXIT
# Ctrl-C or `systemctl stop` would otherwise skip the cleanup above.
trap 'exit 143' INT TERM

mkdir -p "$OUT/runs"
"$TOOLS/collect_specs.sh" > "$OUT/specs.json"
# No build servers: they outlive the build and keep the capped scope alive.
dotnet build "$ROOT/dotnet/src/TocExtractor.Cli" -c Release --nologo -v q --disable-build-servers \
    > "$OUT/build.log" 2>&1 || {
    echo "build failed, see $OUT/build.log"; exit 1; }
git -C "$ROOT" rev-parse HEAD > "$OUT/commit.txt"

python3 -I "$TOOLS/make_site.py" "$WORK/site" "$CHAPTERS"
python3 -I -m http.server "$PORT" --bind 127.0.0.1 -d "$WORK/site" > /dev/null 2>&1 &
SERVER=$!
sleep 1

# Average current clock across cores, and package temperature. Logged
# because a laptop that throttles mid-run otherwise looks like a slowdown in
# the software: on the first machine every core sat at 400 MHz after 3 hours.
clock() { cat /sys/devices/system/cpu/cpu*/cpufreq/scaling_cur_freq 2>/dev/null | awk '{s+=$1; n++} END {if (n) printf "%d", s/n/1000}'; }
pkg_temp() {
    local z
    for z in /sys/class/thermal/thermal_zone*; do
        [ "$(cat "$z/type" 2>/dev/null)" = x86_pkg_temp ] && { echo $(( $(cat "$z/temp") / 1000 )); return; }
    done
}

# sample LOG ROOT_PIDS... : one CSV row every 3s while any root pid lives.
sample() {
    local log=$1 dir=$2; shift 2
    local t0; t0=$(date +%s%N)
    echo "t_s,tree_pss_mb,procs,cpu_pct,mem_avail_mb,load1,files_written,app_pss_mb,driver_pss_mb,browser_pss_mb,cpu_mhz_avg,pkg_temp_c" > "$log"
    while :; do
        local live=0 p
        for p in "$@"; do kill -0 "$p" 2>/dev/null && live=1; done
        [ $live = 1 ] || break
        local all pss=0 app=0 driver=0 browser=0 v comm
        all=$(for p in "$@"; do pstree -lpT "$p" 2>/dev/null; done | grep -oE '\([0-9]+\)' | tr -d '()')
        # Split by component: the CLI itself, Playwright's node driver, and
        # everything else (Chromium). Shell builtins only, so a loaded machine
        # starves the sampler less.
        for p in $all; do
            v=0
            while read -r k n _; do [ "$k" = Pss: ] && v=$n && break; done 2>/dev/null < "/proc/$p/smaps_rollup"
            read -r comm 2>/dev/null < "/proc/$p/comm" || comm=
            case $comm in
                toc-extractor|dotnet) app=$((app + v)) ;;
                node|MainThread) driver=$((driver + v)) ;;
                bash|sh) ;;
                *) browser=$((browser + v)) ;;
            esac
            pss=$((pss + v))
        done
        echo "$(( ($(date +%s%N) - t0) / 1000000000 )),$((pss / 1024)),$(echo $all | wc -w),$(
            ps -o %cpu= -p "$(echo $all | tr ' ' ,)" 2>/dev/null | awk '{s+=$1} END {printf "%.0f", s}'),$(
            awk '/MemAvailable/{print int($2/1024)}' /proc/meminfo),$(cut -d' ' -f1 /proc/loadavg),$(
            find "$dir" -name '*.txt' 2>/dev/null | wc -l),$((app / 1024)),$((driver / 1024)),$((browser / 1024)),$(clock),$(pkg_temp)" >> "$log"
        sleep 3
    done
}

# launch OUTDIR TABS : start one CLI, print its pid.
# The exit code goes to OUTDIR/rc from inside the subshell. Collecting it with
# `wait` hours later proved unreliable: three clean runs once reported 1.
launch() {
    ( "$CLI" --toc "http://127.0.0.1:$PORT/" --link 'ol.toc a' --title 'h1.title' \
        --content 'article.reader' --max 100000 --format text --format jsonl \
        --out "$1/out" --allow-private-hosts --min-delay 0 --max-delay 0 \
        --wait-after-load 0 --concurrency "$2" > "$1/run.log" 2>&1
      echo $? > "$1/rc" ) > /dev/null 2>&1 &
    echo $!
}

# finish RUN_DIR LOG_DIR PIDS... : wait, record exits and elapsed, keep manifests.
finish() {
    local run=$1 dir=$2 t0=$3 failed=0 p rc; shift 3
    wait "$@" 2>/dev/null
    for p in "$run"/rc "$run"/*/rc; do
        [ -f "$p" ] || continue
        rc=$(cat "$p")
        [ "$rc" = 0 ] || failed=$((failed + 1))
        echo "$(basename "$(dirname "$p")") exit=$rc" >> "$dir/exits.log"
    done
    echo "elapsed_ms=$(( ($(date +%s%N) - t0) / 1000000 ))" >> "$dir/exits.log"
    for f in "$run"/*/out/manifest.jsonl "$run"/out/manifest.jsonl; do
        [ -f "$f" ] || continue
        local name; name=$(basename "$(dirname "$(dirname "$f")")")
        tail -1 "$f" > "$dir/summary-${name}.json"
    done
    for f in "$run"/run.log "$run"/*/run.log; do
        [ -f "$f" ] && sed "s#$WORK#<work>#g" "$f" > "$dir/$(basename "$(dirname "$f")").log"
    done
    return $failed
}

for c in $TABS; do
    run=$WORK/single-$c; dir=$OUT/runs/single-c$c; mkdir -p "$run" "$dir"
    t0=$(date +%s%N); pid=$(launch "$run" "$c")
    sample "$dir/samples.csv" "$run" "$pid"
    finish "$run" "$dir" "$t0" "$pid" || echo "single c=$c failed"
    rm -rf "$run"
done

for n in $SWARM; do
    run=$WORK/swarm-$n; dir=$OUT/runs/swarm-n$n; mkdir -p "$run" "$dir"
    t0=$(date +%s%N); pids=()
    for i in $(seq 1 "$n"); do mkdir -p "$run/p$i"; pids+=("$(launch "$run/p$i" "$SWARM_TABS")"); done
    sample "$dir/samples.csv" "$run" "${pids[@]}"
    finish "$run" "$dir" "$t0" "${pids[@]}"; failed=$?
    rm -rf "$run"
    if [ "$failed" -gt 0 ]; then
        echo "swarm n=$n: $failed of $n processes failed; this is the limit"
        break
    fi
done

python3 -I "$TOOLS/summarize.py" "$OUT"
echo "results in $OUT"
