#!/usr/bin/env python3
"""Read headless server status and recent multiplayer join diagnostics over SSH."""

from __future__ import annotations

import argparse
import base64
import getpass
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


JOIN_LOG_KEYS = (
    "Client joining",
    "Accepted ClientID",
    "World transfer",
    "Loading Project",
    "catch-up",
    "bootstrap",
    "Aborted joining",
    "Client left",
    "Terrain recovery",
    "WorldControl",
    "Fog",
)


def quote_windows_argument(value: str) -> str:
    if any(character in value for character in ('"', "\r", "\n")):
        raise RuntimeError("remote paths cannot contain quotes or newlines")
    return f'"{value}"'


def build_remote_script(root: str, since: str, tail_lines: int) -> str:
    # Source: HeadlessRenderingMod/tools/serverctl.py:command
    return f'''\
import json
from pathlib import Path
import sys

root = Path({root.replace(chr(92), "/")!r})
sys.path.insert(0, str(root / "tools"))
import serverctl

print("STATUS")
print(json.dumps(serverctl.command(root, "status"), ensure_ascii=True, indent=2))
path = root / "Logs" / "Game.log"
print("LOG", path, path.stat().st_size if path.exists() else "MISSING")
if path.exists():
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    keys = {JOIN_LOG_KEYS!r}
    since = {since!r}
    for line in lines[-{tail_lines}:]:
        if (since and line[:len(since)] >= since) or any(
            key.casefold() in line.casefold() for key in keys
        ):
            print(line)
'''


def run(args: argparse.Namespace, password: str) -> int:
    plink = shutil.which("plink")
    if not plink:
        raise RuntimeError("PuTTY plink.exe was not found")

    script = build_remote_script(args.root, args.since, args.tail_lines)
    payload = base64.b64encode(script.encode("utf-8")).decode("ascii")
    remote_python = args.root.rstrip("/\\") + "/tools/python/python.exe"
    remote_command = (
        f"{quote_windows_argument(remote_python)} -c "
        f'"import base64;exec(base64.b64decode(\\\"{payload}\\\"))"'
    )

    # Source: HeadlessRenderingMod/tools/control_remote.py:RemoteHeadlessSession
    # Keep credentials out of command-line arguments and remove the temporary file immediately.
    with tempfile.NamedTemporaryFile(
        mode="w", encoding="utf-8", newline="", delete=False
    ) as stream:
        password_path = Path(stream.name)
        stream.write(password)
    try:
        completed = subprocess.run(
            [
                plink,
                "-batch",
                "-ssh",
                "-hostkey",
                args.hostkey,
                "-P",
                str(args.ssh_port),
                "-l",
                args.user,
                "-pwfile",
                str(password_path),
                args.host,
                remote_command,
            ],
            capture_output=True,
            timeout=args.timeout,
        )
    finally:
        password_path.unlink(missing_ok=True)

    sys.stdout.buffer.write(completed.stdout)
    sys.stderr.buffer.write(completed.stderr)
    return completed.returncode


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--ssh-port", type=int, default=22)
    parser.add_argument("--user", required=True)
    parser.add_argument("--root", required=True)
    parser.add_argument("--hostkey", required=True)
    parser.add_argument("--since", default="")
    parser.add_argument("--tail-lines", type=int, default=20000)
    parser.add_argument("--timeout", type=float, default=45.0)
    parser.add_argument("--password-stdin", action="store_true")
    args = parser.parse_args()
    if args.tail_lines <= 0:
        parser.error("--tail-lines must be positive")
    password = (
        sys.stdin.readline().rstrip("\r\n")
        if args.password_stdin
        else getpass.getpass("SSH password: ")
    )
    if not password:
        parser.error("SSH password is required")
    try:
        return run(args, password)
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
