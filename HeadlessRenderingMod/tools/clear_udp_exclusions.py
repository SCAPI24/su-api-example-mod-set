#!/usr/bin/env python3
"""Remove Windows UDP port exclusions that block the local Survivalcraft server."""

from __future__ import annotations

import ctypes
import argparse
from pathlib import Path
import subprocess
import sys


EXCLUDED_RANGES = (
    (50000, 60),
    (51386, 100),
    (51486, 100),
    (51586, 100),
    (51686, 100),
    (51786, 100),
    (51886, 100),
    (51986, 100),
    (52086, 100),
    (52186, 100),
)
VIRTUAL_NETWORK_SERVICES = ("vmcompute", "hns", "winnat")
REPORT_PATH = Path(__file__).with_name("clear_udp_exclusions.log")


def run_netsh(*arguments: str) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        [r"C:\Windows\System32\netsh.exe", *arguments],
        capture_output=True,
        check=False,
    )


def decode(data: bytes) -> str:
    return data.decode("mbcs", errors="replace").strip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--stop-virtual-network",
        action="store_true",
        help="stop VMCompute, HNS and WinNAT before deleting their reservations",
    )
    args = parser.parse_args()
    if not ctypes.windll.shell32.IsUserAnAdmin():
        print("This tool must be run as Administrator.", file=sys.stderr)
        return 2

    report: list[str] = []
    failures = 0
    if args.stop_virtual_network:
        for service in VIRTUAL_NETWORK_SERVICES:
            completed = subprocess.run(
                [r"C:\Windows\System32\sc.exe", "stop", service],
                capture_output=True,
                check=False,
            )
            message = decode(completed.stdout) or decode(completed.stderr)
            line = f"stop {service}: {completed.returncode} {message}"
            report.append(line)
            print(line)
    for family in ("ipv4", "ipv6"):
        for start_port, count in EXCLUDED_RANGES:
            completed = run_netsh(
                "interface",
                family,
                "delete",
                "excludedportrange",
                "protocol=udp",
                f"startport={start_port}",
                f"numberofports={count}",
                "store=active",
            )
            end_port = start_port + count - 1
            message = decode(completed.stdout) or decode(completed.stderr)
            line = f"{family} {start_port}-{end_port}: {completed.returncode} {message}"
            report.append(line)
            print(line)
            if completed.returncode != 0:
                failures += 1

    REPORT_PATH.write_text("\n".join(report) + "\n", encoding="utf-8")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
