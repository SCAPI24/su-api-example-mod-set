#!/usr/bin/env python3
"""Check the control status and recent log of a remote headless server."""

from __future__ import annotations

import argparse
import getpass
from pathlib import PurePosixPath

import paramiko


def main() -> int:
    # Source: publish/check_remote_runtime.py:main
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--ssh-port", type=int, default=22)
    parser.add_argument("--user", required=True)
    parser.add_argument("--password")
    parser.add_argument("--root", default="C:/SurvivalcraftServer")
    parser.add_argument("--tail-bytes", type=int, default=262144)
    args = parser.parse_args()

    password = args.password or getpass.getpass("SSH password: ")
    root = args.root.rstrip("/")
    remote_python = root + "/tools/python/python.exe"
    serverctl = root + "/tools/serverctl.py"
    log_path = PurePosixPath(root) / "Logs" / "Game.log"

    client = paramiko.SSHClient()
    client.load_system_host_keys()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(
        args.host,
        port=args.ssh_port,
        username=args.user,
        password=password,
        timeout=15,
    )
    try:
        command = f'"{remote_python}" "{serverctl}" --root "{root}" direct status'
        _, stdout, stderr = client.exec_command(command, timeout=20)
        status = stdout.read().decode("utf-8", errors="replace")
        error = stderr.read().decode("gbk", errors="replace")
        exit_code = stdout.channel.recv_exit_status()
        if exit_code != 0:
            raise RuntimeError(error.strip() or status.strip() or command)
        print("REMOTE_STATUS " + status.strip())

        sftp = client.open_sftp()
        try:
            with sftp.open(str(log_path), "rb") as stream:
                size = stream.stat().st_size
                stream.seek(max(0, size - max(1, args.tail_bytes)))
                log = stream.read().decode("utf-8-sig", errors="replace")
        finally:
            sftp.close()

        for line in log.splitlines()[-400:]:
            if (
                "[HeadlessRenderingMod]" in line
                or "ERROR" in line
                or "Exception" in line
            ):
                print(line)
    finally:
        client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
