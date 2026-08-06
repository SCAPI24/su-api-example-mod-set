#!/usr/bin/env python3
"""Request UAC elevation for clear_udp_exclusions.py."""

from __future__ import annotations

import ctypes
from pathlib import Path
import sys


def main() -> int:
    target = Path(__file__).with_name("clear_udp_exclusions.py").resolve()
    result = ctypes.windll.shell32.ShellExecuteW(
        None,
        "runas",
        sys.executable,
        f'"{target}" --stop-virtual-network',
        str(target.parent),
        1,
    )
    if result <= 32:
        print(f"UAC launch failed with code {result}", file=sys.stderr)
        return 1
    print("UAC cleanup process started.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
