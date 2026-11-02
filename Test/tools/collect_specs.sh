#!/bin/bash
# Print this machine's specs as JSON, for comparing stress runs across machines.
#
# Deliberately left out: serial numbers, UUIDs, MAC and IP addresses, the
# username, home paths and anything else that identifies the owner or the
# unit. Model names stay because they are what explains a difference.
#
#   Test/tools/collect_specs.sh > specs.json
set -euo pipefail

val() { cat "$1" 2>/dev/null | tr -d '\n' || true; }
lscpu_field() { lscpu | awk -F: -v k="$1" '$1==k {sub(/^[ \t]+/, "", $2); print $2; exit}'; }
khz_to_mhz() { [ -n "$1" ] && echo $(( $1 / 1000 )) || echo null; }

cpu_dir=/sys/devices/system/cpu
max_khz=$(cat $cpu_dir/cpu*/cpufreq/cpuinfo_max_freq 2>/dev/null | sort -n | tail -1 || true)
min_khz=$(cat $cpu_dir/cpu*/cpufreq/cpuinfo_max_freq 2>/dev/null | sort -n | head -1 || true)
no_turbo=$(val $cpu_dir/intel_pstate/no_turbo)
boost=$(val $cpu_dir/cpufreq/boost)
if [ "$no_turbo" = "1" ] || [ "$boost" = "0" ]; then turbo=false
elif [ -n "$no_turbo$boost" ]; then turbo=true
else turbo=null; fi

disk=$(lsblk -dn -P -o NAME,MODEL,SIZE,ROTA,TRAN 2>/dev/null | grep -vE 'NAME="(zram|loop|sr)' | grep -v 'TRAN="usb"' | head -1 \
  | sed -E 's/NAME="[^"]*" //; s/MODEL="([^"]*)" SIZE="([^"]*)" ROTA="([^"]*)" TRAN="([^"]*)"/\1, \2, \4, rotational=\3/')
on_ac=$(cat /sys/class/power_supply/A*/online 2>/dev/null | head -1 || true)
rapl=$(val /sys/class/powercap/intel-rapl:0/constraint_0_power_limit_uw)
. /etc/os-release 2>/dev/null || true

python3 -I - <<EOF
import json
def n(x):
    try: return int(x)
    except (TypeError, ValueError): return None
print(json.dumps({
    "vendor": "$(val /sys/class/dmi/id/sys_vendor)",
    "model": "$(val /sys/class/dmi/id/product_family) ($(val /sys/class/dmi/id/product_name))",
    "cpu": {
        "model": "$(lscpu_field 'Model name')",
        "logical_cpus": n("$(nproc)"),
        "cores_per_socket": n("$(lscpu_field 'Core(s) per socket')"),
        "threads_per_core": n("$(lscpu_field 'Thread(s) per core')"),
        "l3": "$(lscpu_field 'L3 cache')",
        "max_mhz_fastest_core_now": n("$(khz_to_mhz "$max_khz")"),
        "max_mhz_slowest_core_now": n("$(khz_to_mhz "$min_khz")"),
        "turbo_enabled": $( [ "$turbo" = null ] && echo None || { [ "$turbo" = true ] && echo True || echo False; } ),
        "governor": "$(val $cpu_dir/cpu0/cpufreq/scaling_governor)",
        "energy_preference": "$(val $cpu_dir/cpu0/cpufreq/energy_performance_preference)",
        "package_power_limit_w": n("${rapl:-}") // 1000000 if n("${rapl:-}") else None,
    },
    "memory_mb": n("$(awk '/MemTotal/{print int($2/1024)}' /proc/meminfo)"),
    "swap_mb": n("$(awk '/SwapTotal/{print int($2/1024)}' /proc/meminfo)"),
    "disk": "${disk}",
    "root_fs": "$(findmnt -no FSTYPE / 2>/dev/null)",
    "gpu": "$(lspci 2>/dev/null | grep -iE 'vga|3d' | head -1 | cut -d: -f3- | sed 's/^ //')",
    "os": "${PRETTY_NAME:-unknown}",
    "kernel": "$(uname -r)",
    "dotnet_sdk": "$(dotnet --version 2>/dev/null)",
    "python": "$(python3 -c 'import platform;print(platform.python_version())')",
    "on_ac_power": {"1": True, "0": False}.get("${on_ac}"),
    "platform_profile": "$(val /sys/firmware/acpi/platform_profile)",
}, indent=2))
EOF
