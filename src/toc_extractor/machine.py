"""What this computer can afford, asked of the computer itself.

A browser page costs memory - measured on real reading sites, 150 to 400 MB
once images and ad frames have loaded. The concurrency flag used to accept any
number on any machine, so `--concurrency 12` meant something quite different
on a 64 GB desktop and on an 8 GB laptop, and on the laptop it meant swapping
until the run crawled.

Measured on Windows with a load test: forty processes at the default
concurrency of three drove a 16 GB machine to zero available memory and
15 GB of resident set. Nothing crashed, but every worker slowed to the disk.

So the ceiling comes from the machine. This module is the part that reads it.
The policy that uses it lives in the CLI, and the same numbers as the C#
MachineBudget, so both implementations behave alike.

Everything here answers None rather than raising when the system will not say.
A tool that cannot read free memory should still run.
"""

from __future__ import annotations

import ctypes
import os
import sys
from dataclasses import dataclass

# Memory left for the system and whatever else the person has open.
RESERVED_GIGABYTES = 4.0

# Pages allowed per gigabyte above the reserve. A working page is 150-400 MB,
# so two per gigabyte leaves room for the ones at the top of that range.
PAGES_PER_GIGABYTE = 2.0

# Past this, pages only queue for the processor.
PAGES_PER_CORE = 4

# The floor, so a small machine still fetches more than one chapter at a time.
FEWEST_PAGES = 1

# The ceiling, however large the machine. Beyond this the site is the limit,
# not us, and being polite matters more than being fast.
MOST_PAGES_ANYWHERE = 16

# Below this share of memory free, the ceiling is halved: whatever the machine
# could hold when idle, it cannot hold now. Same threshold as the C#
# MachineBudget.TightBelowPercent.
TIGHT_BELOW_PERCENT = 20


class _MemoryStatusEx(ctypes.Structure):
    """Windows MEMORYSTATUSEX. Field order is the API's, do not reorder."""

    _fields_ = (
        ("dwLength", ctypes.c_ulong),
        ("dwMemoryLoad", ctypes.c_ulong),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    )


def _windows_memory() -> tuple[int, int] | None:
    """(total, available) bytes from GlobalMemoryStatusEx, or None."""
    try:
        status = _MemoryStatusEx()
        status.dwLength = ctypes.sizeof(_MemoryStatusEx)
        # windll exists only on Windows, where a type checker also sees it.
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):  # type: ignore[attr-defined, unused-ignore]
            return None
    except (AttributeError, OSError):
        return None
    if status.ullTotalPhys == 0:
        return None
    # AvailPhys, not TotalPhys minus the working sets: it already counts the
    # standby cache Windows would hand back the moment it is asked.
    return int(status.ullTotalPhys), int(status.ullAvailPhys)


def _linux_memory() -> tuple[int, int] | None:
    try:
        with open("/proc/meminfo", encoding="ascii") as handle:
            fields = {}
            for line in handle:
                name, _, rest = line.partition(":")
                fields[name] = int(rest.strip().split()[0]) * 1024
    except (OSError, ValueError, IndexError):
        return None
    total = fields.get("MemTotal", 0)
    # MemAvailable is the kernel's own estimate and accounts for reclaimable
    # cache; MemFree alone understates what a new process can actually have.
    available = fields.get("MemAvailable", fields.get("MemFree", 0))
    return (total, available) if total else None


def _macos_memory() -> tuple[int, int] | None:
    try:
        import subprocess

        total = int(
            subprocess.run(
                ["sysctl", "-n", "hw.memsize"], capture_output=True, text=True, check=True
            ).stdout.strip()
        )
    except (OSError, ValueError, subprocess.SubprocessError):
        return None
    # No cheap equivalent of MemAvailable without vm_stat parsing; the ceiling
    # only needs the total, and free memory reads as unknown.
    return (total, 0) if total else None


def read_memory() -> tuple[int, int] | None:
    """(total bytes, available bytes). Available may be 0 where unknown."""
    # Through a str, not sys.platform directly: to a type checker
    # sys.platform is a literal for the platform it is running on, so the
    # other two branches read as dead code and strict mode rejects them.
    platform: str = sys.platform
    if platform == "win32":
        return _windows_memory()
    if platform == "darwin":
        return _macos_memory()
    return _linux_memory()


def total_memory_bytes() -> int:
    memory = read_memory()
    return memory[0] if memory else 0


def free_memory_percent() -> int | None:
    """Share of memory free right now, 0-100, or None if the system will not say."""
    memory = read_memory()
    if memory is None:
        return None
    total, available = memory
    if total <= 0 or available <= 0:
        return None
    return round(available * 100 / total)


def most_pages_for(memory_bytes: int, cores: int) -> int:
    """The concurrency ceiling for a machine with this much memory and this many cores."""
    by_cores = max(1, cores) * PAGES_PER_CORE
    if memory_bytes <= 0:
        return max(FEWEST_PAGES, min(by_cores, MOST_PAGES_ANYWHERE))
    gigabytes = memory_bytes / 1024**3
    by_memory = int((gigabytes - RESERVED_GIGABYTES) * PAGES_PER_GIGABYTE)
    return max(FEWEST_PAGES, min(by_memory, by_cores, MOST_PAGES_ANYWHERE))


@dataclass(frozen=True, slots=True)
class Budget:
    """What this machine can afford, and whether the request fits."""

    requested: int
    allowed: int
    total_memory_bytes: int
    cores: int
    free_percent: int | None

    @property
    def capped(self) -> bool:
        return self.allowed < self.requested

    def describe(self) -> str:
        gigabytes = self.total_memory_bytes / 1024**3
        memory = f"{gigabytes:.1f} GB" if self.total_memory_bytes else "unknown memory"
        free = f", {self.free_percent}% free now" if self.free_percent is not None else ""
        return f"{memory}, {self.cores} core(s){free}"


def budget_for(
    requested: int,
    *,
    cores: int | None = None,
    memory_bytes: int | None = None,
    free_percent: int | None = None,
) -> Budget:
    """Work out what concurrency this machine should actually run.

    Never raises the request: someone who asks for one page gets one. It only
    lowers a request the machine cannot hold.

    Two questions, not one. How much memory the machine has is fixed and sets
    the ceiling. How much is free right now is not, and matters more: the
    ceiling assumes the reserve is actually available, and on a laptop with a
    browser and a chat app already open it is not. So when memory is already
    scarce the ceiling is halved again, which is the difference between a slow
    run and one that swaps.
    """
    total = memory_bytes if memory_bytes is not None else total_memory_bytes()
    processors = cores if cores is not None else (os.cpu_count() or 1)
    free = free_percent if free_percent is not None else free_memory_percent()

    ceiling = most_pages_for(total, processors)
    if free is not None and free < TIGHT_BELOW_PERCENT:
        ceiling = max(FEWEST_PAGES, ceiling // 2)

    return Budget(
        requested=requested,
        allowed=max(FEWEST_PAGES, min(requested, ceiling)),
        total_memory_bytes=total,
        cores=processors,
        free_percent=free,
    )
