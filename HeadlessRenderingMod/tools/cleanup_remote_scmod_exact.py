#!/usr/bin/env python3
"""Remove exact stale ScMultiplayer deployment files without shell globbing."""

from pathlib import Path
import sys


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: cleanup_remote_scmod_exact.py <server-root>")
    root = Path(sys.argv[1]).resolve()
    targets = (
        root / "Mods" / "[SuAPI]ScMultiplayer-1.8.2.scmod",
        root / "tools" / "incoming" / "[SuAPI]ScMultiplayer-1.8.3.scmod",
    )
    for target in targets:
        if target.is_file():
            target.unlink()
            print(f"Removed {target}")
    packages = sorted(
        (path.name, path.stat().st_size)
        for path in (root / "Mods").glob("*ScMultiplayer*.scmod")
    )
    print(f"ScMultiplayer packages: {packages}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
