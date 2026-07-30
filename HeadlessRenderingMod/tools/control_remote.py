#!/usr/bin/env python3
"""Control a HeadlessRenderingMod server over SSH."""

from __future__ import annotations

import argparse
import getpass
import json
from pathlib import PurePosixPath
import secrets
import sys
from typing import Any

import paramiko


def parse_values(values: list[str]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for value in values:
        if "=" not in value:
            raise RuntimeError(f"argument must use key=value form: {value}")
        key, raw = value.split("=", 1)
        if not key:
            raise RuntimeError("argument key cannot be empty")
        try:
            result[key] = json.loads(raw)
        except json.JSONDecodeError:
            result[key] = raw
    return result


def quote_windows_argument(value: str) -> str:
    if any(character in value for character in ('"', "\r", "\n")):
        raise RuntimeError("remote command paths and arguments cannot contain quotes or newlines")
    return f'"{value}"'


class RemoteHeadlessSession:
    def __init__(
        self,
        host: str,
        ssh_port: int,
        user: str,
        password: str,
        root: str,
    ) -> None:
        self.root = root.rstrip("/\\")
        self.client = paramiko.SSHClient()
        self.client.load_system_host_keys()
        self.client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        self.client.connect(
            host,
            port=ssh_port,
            username=user,
            password=password,
            timeout=15,
        )

    def close(self) -> None:
        self.client.close()

    # Source: HeadlessRenderingMod/Server/HeadlessControlServer.cs:HandleClientAsync
    def direct(self, command: str, arguments: dict[str, Any] | None = None) -> dict[str, Any]:
        config_path = PurePosixPath(self.root) / "server.json"
        sftp = self.client.open_sftp()
        try:
            with sftp.open(str(config_path), "r") as stream:
                config = json.loads(stream.read().decode("utf-8"))
        finally:
            sftp.close()

        request: dict[str, Any] = {
            "id": secrets.token_hex(8),
            "token": config["token"],
            "command": command,
        }
        if arguments:
            request["args"] = arguments
        payload = (json.dumps(request, separators=(",", ":")) + "\n").encode("utf-8")
        maximum = int(config.get("maxRequestBytes", 65536)) * 16
        timeout = float(config.get("requestTimeoutSeconds", 10)) + 2.0
        transport = self.client.get_transport()
        if transport is None or not transport.is_active():
            raise RuntimeError("SSH transport is not active")
        channel = transport.open_channel(
            "direct-tcpip",
            (str(config["bindAddress"]), int(config["port"])),
            ("127.0.0.1", 0),
        )
        try:
            channel.settimeout(timeout)
            channel.sendall(payload)
            data = bytearray()
            while True:
                block = channel.recv(4096)
                if not block:
                    raise RuntimeError("control connection closed without a response")
                newline = block.find(b"\n")
                if newline >= 0:
                    data.extend(block[:newline])
                    break
                data.extend(block)
                if len(data) > maximum:
                    raise RuntimeError("control response is too large")
        finally:
            channel.close()
        return json.loads(data.decode("utf-8"))

    # Source: HeadlessRenderingMod/tools/remote_server_ops.py:main
    def operation(
        self,
        action: str,
        timeout: float,
        world: str | None = None,
    ) -> str:
        remote_python = self.root + "/tools/python/python.exe"
        operations = self.root + "/tools/remote_server_ops.py"
        parts = [
            quote_windows_argument(remote_python),
            quote_windows_argument(operations),
            "--root",
            quote_windows_argument(self.root),
            "--timeout",
            str(timeout),
            action,
        ]
        if world:
            parts.extend(("--world", quote_windows_argument(world)))
        remote_command = " ".join(parts)
        _, stdout, stderr = self.client.exec_command(
            remote_command,
            timeout=max(timeout + 30.0, 60.0),
        )
        output = stdout.read().decode("utf-8", errors="replace")
        error = stderr.read().decode("gbk", errors="replace")
        exit_code = stdout.channel.recv_exit_status()
        if exit_code != 0:
            raise RuntimeError(error.strip() or output.strip() or remote_command)
        return output.strip()


def print_response(response: dict[str, Any]) -> None:
    print(json.dumps(response, ensure_ascii=False, indent=2))
    if response.get("ok"):
        return
    error = response.get("error")
    if isinstance(error, dict):
        raise RuntimeError(
            f"{error.get('code', 'command_failed')}: "
            f"{error.get('message', 'unknown control error')}"
        )
    raise RuntimeError("control command failed")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--ssh-port", type=int, default=22)
    parser.add_argument("--user", required=True)
    parser.add_argument("--password")
    parser.add_argument("--root", default="C:/SurvivalcraftServer")
    parser.add_argument("--timeout", type=float, default=60.0)
    subparsers = parser.add_subparsers(dest="action", required=True)
    subparsers.add_parser("ping")
    subparsers.add_parser("status")
    start = subparsers.add_parser("start")
    start.add_argument("--world")
    subparsers.add_parser("stop")
    restart = subparsers.add_parser("restart")
    restart.add_argument("--world")
    join = subparsers.add_parser("join")
    join.add_argument("world")
    subparsers.add_parser("close")
    subparsers.add_parser("save")
    direct = subparsers.add_parser("direct")
    direct.add_argument("command")
    direct.add_argument("values", nargs="*")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    password = args.password or getpass.getpass("SSH password: ")
    session = RemoteHeadlessSession(
        args.host,
        args.ssh_port,
        args.user,
        password,
        args.root,
    )
    try:
        if args.action in {"start", "stop", "restart"}:
            print(session.operation(
                args.action,
                args.timeout,
                getattr(args, "world", None),
            ))
        elif args.action == "status":
            print(session.operation("status", args.timeout))
        elif args.action == "ping":
            print_response(session.direct("ping"))
        elif args.action == "join":
            print_response(session.direct("world.join", {"world": args.world}))
        elif args.action == "close":
            print_response(session.direct("world.close"))
        elif args.action == "save":
            print_response(session.direct("world.save"))
        elif args.action == "direct":
            print_response(session.direct(args.command, parse_values(args.values)))
        return 0
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    finally:
        session.close()


if __name__ == "__main__":
    raise SystemExit(main())
