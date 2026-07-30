#!/usr/bin/env python3
"""Deploy one .scmod package to a remote headless server."""

from __future__ import annotations

import argparse
import getpass
import hashlib
import json
from pathlib import Path, PurePosixPath

import paramiko


def main() -> int:
    # Source: publish/deploy_remote_mod.py:main
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--ssh-port", type=int, default=22)
    parser.add_argument("--user", required=True)
    parser.add_argument("--password")
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--root", default="C:/SurvivalcraftServer")
    args = parser.parse_args()

    source = args.source.resolve()
    if not source.is_file() or source.suffix.casefold() != ".scmod":
        raise RuntimeError(f"source is not a .scmod file: {source}")
    password = args.password or getpass.getpass("SSH password: ")
    expected_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    root = args.root.rstrip("/")
    remote_staging = PurePosixPath(root) / "tools" / "incoming"
    remote_source = remote_staging / source.name
    remote_destination = PurePosixPath(root) / "Mods" / source.name
    erroneous_destination = (
        PurePosixPath(root) / "Mods" / ("." + source.stem + ".incoming.scmod")
    )
    remote_python = root + "/tools/python/python.exe"
    operations = root + "/tools/remote_server_ops.py"
    serverctl = root + "/tools/serverctl.py"

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

    def execute(command: str, timeout: float = 240.0) -> str:
        _, stdout, stderr = client.exec_command(command, timeout=timeout)
        output = stdout.read().decode("utf-8", errors="replace")
        error = stderr.read().decode("gbk", errors="replace")
        status = stdout.channel.recv_exit_status()
        if status != 0:
            raise RuntimeError(error.strip() or output.strip() or command)
        return output

    sftp = client.open_sftp()
    try:
        try:
            sftp.mkdir(str(remote_staging))
        except OSError:
            pass
        try:
            sftp.remove(str(erroneous_destination))
            print(f"REMOVED_ERRONEOUS {erroneous_destination}")
        except OSError:
            pass
        sftp.put(str(source), str(remote_source))
    finally:
        sftp.close()

    try:
        command = (
            f'"{remote_python}" "{operations}" --root "{root}" --timeout 90 '
            f'deploy-mod "{remote_source}"'
        )
        print(execute(command))

        digest = hashlib.sha256()
        sftp = client.open_sftp()
        try:
            with sftp.open(str(remote_destination), "rb") as stream:
                while block := stream.read(1024 * 1024):
                    digest.update(block)
        finally:
            sftp.close()
        if digest.hexdigest() != expected_hash:
            raise RuntimeError("remote deployed mod hash does not match source")
        print(f"REMOTE_VERIFIED {remote_destination} {expected_hash}")

        status_command = (
            f'"{remote_python}" "{serverctl}" --root "{root}" direct status'
        )
        status = json.loads(execute(status_command))
        print("REMOTE_STATUS " + json.dumps(status, ensure_ascii=False))
    finally:
        sftp = client.open_sftp()
        try:
            try:
                sftp.remove(str(remote_source))
            except OSError:
                pass
        finally:
            sftp.close()
        client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
