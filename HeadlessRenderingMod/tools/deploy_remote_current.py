#!/usr/bin/env python3
"""Deploy a complete Windows publish directory to a remote headless server."""

from __future__ import annotations

import argparse
import getpass
import hashlib
import json
from pathlib import Path, PurePosixPath
import time

import paramiko


def main() -> int:
    # Source: publish/deploy_remote_current.py:main
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--ssh-port", type=int, default=22)
    parser.add_argument("--user", required=True)
    parser.add_argument("--password")
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--root", default="C:/SurvivalcraftServer")
    parser.add_argument("--world")
    parser.add_argument("--resume-only", action="store_true")
    args = parser.parse_args()

    source = args.source.resolve()
    if not source.is_dir():
        raise RuntimeError(f"publish source does not exist: {source}")
    password = args.password or getpass.getpass("SSH password: ")
    root = args.root.rstrip("/")
    remote_python = root + "/tools/python/python.exe"
    serverctl = root + "/tools/serverctl.py"
    operations = root + "/tools/remote_server_ops.py"

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

    def execute(command: str, timeout: float = 180.0) -> str:
        _, stdout, stderr = client.exec_command(command, timeout=timeout)
        output = stdout.read().decode("utf-8", errors="replace")
        error = stderr.read().decode("gbk", errors="replace")
        status = stdout.channel.recv_exit_status()
        if status != 0:
            raise RuntimeError(error.strip() or output.strip() or command)
        return output

    try:
        status_command = (
            f'"{remote_python}" "{serverctl}" --root "{root}" direct status'
        )
        initial = json.loads(execute(status_command))
        world = args.world or str((initial.get("result") or {}).get("worldName") or "")

        if not args.resume_only:
            execute(f'"{remote_python}" "{operations}" --root "{root}" --timeout 60 stop')
            sftp = client.open_sftp()
            try:
                for local in sorted(source.rglob("*")):
                    if not local.is_file():
                        continue
                    relative = local.relative_to(source)
                    remote = PurePosixPath(root).joinpath(*relative.parts)
                    current = PurePosixPath(root)
                    for part in relative.parts[:-1]:
                        current /= part
                        try:
                            sftp.mkdir(str(current))
                        except OSError:
                            pass
                    sftp.put(str(local), str(remote))
            finally:
                sftp.close()

            execute(f'"{remote_python}" "{operations}" --root "{root}" --timeout 60 start')

        if world:
            deadline = time.monotonic() + 180.0
            join_command = (
                f'"{remote_python}" "{serverctl}" --root "{root}" direct world.join '
                + '"world=' + world.replace('"', "") + '"'
            )
            while time.monotonic() < deadline:
                try:
                    execute(join_command)
                    break
                except RuntimeError as error:
                    if "game_not_ready" not in str(error):
                        raise
                    time.sleep(0.5)
            else:
                raise RuntimeError("remote world command did not become ready")

            while time.monotonic() < deadline:
                status = json.loads(execute(status_command))
                result = status.get("result") or {}
                if result.get("worldLoaded") and result.get("currentScreen") == "Game":
                    break
                time.sleep(0.5)
            else:
                raise RuntimeError("remote world did not return to Game")

        # Source: publish/deploy_remote_current.py:local_checks
        local_checks = [
            candidate
            for candidate in (source / "Survivalcraft.dll", source / "SuAPICore.dll")
            if candidate.is_file()
        ]
        mods = source / "Mods"
        if mods.is_dir():
            local_checks.extend(sorted(mods.glob("*.scmod")))
        if not local_checks:
            raise RuntimeError("publish source has no core DLL or .scmod files to verify")

        sftp = client.open_sftp()
        try:
            for local in local_checks:
                remote = PurePosixPath(root).joinpath(*local.relative_to(source).parts)
                digest = hashlib.sha256()
                with sftp.open(str(remote), "rb") as stream:
                    while block := stream.read(1024 * 1024):
                        digest.update(block)
                expected = hashlib.sha256(local.read_bytes()).hexdigest()
                if digest.hexdigest() != expected:
                    raise RuntimeError(f"remote hash mismatch: {remote}")
                print(f"verified {remote} {expected}")
        finally:
            sftp.close()

        print(execute(status_command))
    finally:
        client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
