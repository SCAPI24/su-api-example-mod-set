#!/usr/bin/env python3
"""Manage HeadlessRenderingMod instances without batch files."""

from __future__ import annotations

import argparse
import ctypes
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import sys
import time
from typing import Any


DEFAULT_PORT = 26741
PID_FILE = "headless.pid"
CONFIG_FILE = "server.json"
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_TERMINATE = 0x0001
STILL_ACTIVE = 259


def load_config(instance: Path) -> dict[str, Any]:
    path = instance / CONFIG_FILE
    if not path.is_file():
        raise RuntimeError(f"missing configuration: {path}")
    with path.open("r", encoding="utf-8") as stream:
        config = json.load(stream)
    for key in ("bindAddress", "port", "token"):
        if key not in config:
            raise RuntimeError(f"server.json is missing '{key}'")
    return config


def save_config(path: Path, config: dict[str, Any]) -> None:
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(config, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def command(instance: Path, name: str, arguments: dict[str, Any] | None = None) -> dict[str, Any]:
    config = load_config(instance)
    request: dict[str, Any] = {
        "id": secrets.token_hex(8),
        "token": config["token"],
        "command": name,
    }
    if arguments:
        request["args"] = arguments

    timeout = float(config.get("requestTimeoutSeconds", 10)) + 2.0
    payload = (json.dumps(request, separators=(",", ":")) + "\n").encode("utf-8")
    with socket.create_connection(
        (str(config["bindAddress"]), int(config["port"])), timeout=timeout
    ) as connection:
        connection.settimeout(timeout)
        connection.sendall(payload)
        response = read_line(connection, 1024 * 1024)
    return json.loads(response.decode("utf-8"))


def read_line(connection: socket.socket, maximum_bytes: int) -> bytes:
    data = bytearray()
    while True:
        block = connection.recv(4096)
        if not block:
            raise RuntimeError("control connection closed without a response")
        newline = block.find(b"\n")
        if newline >= 0:
            data.extend(block[:newline])
            return bytes(data)
        data.extend(block)
        if len(data) > maximum_bytes:
            raise RuntimeError("control response is too large")


def instance_path(root: Path, name: str) -> Path:
    if not name or name in {".", ".."} or any(ch in name for ch in "\\/:"):
        raise RuntimeError("instance name must be one directory name")
    result = (root / "instances" / name).resolve()
    instances_root = (root / "instances").resolve()
    if not result.is_relative_to(instances_root):
        raise RuntimeError("instance path escapes the instances directory")
    return result


def choose_port(root: Path) -> int:
    used: set[int] = set()
    instances = root / "instances"
    if instances.is_dir():
        for config_path in instances.glob(f"*/{CONFIG_FILE}"):
            try:
                with config_path.open("r", encoding="utf-8") as stream:
                    used.add(int(json.load(stream)["port"]))
            except (OSError, ValueError, KeyError, json.JSONDecodeError):
                continue

    for port in range(DEFAULT_PORT, DEFAULT_PORT + 1000):
        if port in used:
            continue
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            try:
                probe.bind(("127.0.0.1", port))
            except OSError:
                continue
        return port
    raise RuntimeError("no free control port was found")


def copy_template(template: Path, destination: Path, copy_files: bool) -> None:
    mutable_names = {CONFIG_FILE, "Settings.xml", PID_FILE}
    mutable_directories = {"Worlds", "Logs", "Scworld"}
    destination.mkdir(parents=True)
    for source in template.rglob("*"):
        relative = source.relative_to(template)
        if any(part in mutable_directories for part in relative.parts):
            continue
        if source.name in mutable_names:
            continue
        target = destination / relative
        if source.is_dir():
            target.mkdir(parents=True, exist_ok=True)
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        if copy_files:
            shutil.copy2(source, target)
        else:
            try:
                os.link(source, target)
            except OSError:
                shutil.copy2(source, target)


def create_instance(root: Path, name: str, port: int | None, copy_files: bool) -> None:
    template = (root / "template").resolve()
    if not template.is_dir():
        raise RuntimeError(f"template directory does not exist: {template}")
    destination = instance_path(root, name)
    if destination.exists():
        raise RuntimeError(f"instance already exists: {destination}")

    copy_template(template, destination, copy_files)
    for directory in ("Worlds", "Logs", "Scworld"):
        (destination / directory).mkdir(exist_ok=True)
    config = {
        "enabled": True,
        "instanceId": name,
        "bindAddress": "127.0.0.1",
        "port": port if port is not None else choose_port(root),
        "token": secrets.token_hex(32).upper(),
        "targetFrameRate": 20,
        "hideWindow": True,
        "disableDrawing": True,
        "enableConsole": True,
        "disableAudio": True,
        "maxQueuedCommands": 256,
        "maxCommandsPerFrame": 64,
        "requestTimeoutSeconds": 10,
        "maxRequestBytes": 65536,
    }
    save_config(destination / CONFIG_FILE, config)
    print(destination)


def find_executable(instance: Path) -> Path:
    preferred = instance / "Survivalcraft.exe"
    if preferred.is_file():
        return preferred
    candidates = sorted(instance.glob("*.exe"))
    if len(candidates) != 1:
        raise RuntimeError(f"cannot uniquely locate Survivalcraft.exe in {instance}")
    return candidates[0]


def read_pid(instance: Path) -> int | None:
    path = instance / PID_FILE
    try:
        return int(path.read_text(encoding="ascii").strip())
    except (OSError, ValueError):
        return None


def process_alive(pid: int | None) -> bool:
    if pid is None:
        return False
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.GetExitCodeProcess.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_ulong),
    ]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return False
    try:
        exit_code = ctypes.c_ulong()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
            return False
        return exit_code.value == STILL_ACTIVE
    finally:
        kernel32.CloseHandle(handle)


def terminate_process(pid: int) -> None:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel32.OpenProcess(PROCESS_TERMINATE, False, pid)
    if not handle:
        raise OSError(ctypes.get_last_error(), f"cannot open process {pid}")
    try:
        if not kernel32.TerminateProcess(handle, 1):
            raise OSError(ctypes.get_last_error(), f"cannot terminate process {pid}")
    finally:
        kernel32.CloseHandle(handle)


def start_instance(instance: Path) -> None:
    pid = read_pid(instance)
    if process_alive(pid):
        raise RuntimeError(f"instance is already running with PID {pid}")

    executable = find_executable(instance)
    logs = instance / "Logs"
    logs.mkdir(exist_ok=True)
    process_log = (logs / "process.log").open("ab")
    creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    try:
        process = subprocess.Popen(
            [str(executable)],
            cwd=instance,
            stdin=subprocess.DEVNULL,
            stdout=process_log,
            stderr=subprocess.STDOUT,
            creationflags=creation_flags,
        )
    finally:
        process_log.close()
    (instance / PID_FILE).write_text(str(process.pid), encoding="ascii")

    deadline = time.monotonic() + 30.0
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"Survivalcraft exited with code {process.returncode}")
        try:
            response = command(instance, "ping")
            if response.get("ok"):
                print(json.dumps(response, ensure_ascii=False))
                return
        except (OSError, RuntimeError, json.JSONDecodeError):
            pass
        time.sleep(0.25)
    raise RuntimeError("control server did not become ready within 30 seconds")


def stop_instance(instance: Path, timeout: float) -> None:
    try:
        response = command(instance, "shutdown")
        print(json.dumps(response, ensure_ascii=False))
    except (OSError, RuntimeError, json.JSONDecodeError) as error:
        print(f"graceful shutdown failed: {error}", file=sys.stderr)

    pid = read_pid(instance)
    deadline = time.monotonic() + timeout
    while process_alive(pid) and time.monotonic() < deadline:
        time.sleep(0.25)
    if process_alive(pid):
        terminate_process(pid)
    try:
        (instance / PID_FILE).unlink()
    except FileNotFoundError:
        pass


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


def print_response(response: dict[str, Any]) -> None:
    print(json.dumps(response, ensure_ascii=False, indent=2))
    if not response.get("ok"):
        error = response.get("error")
        if isinstance(error, dict):
            code = error.get("code", "command_failed")
            message = error.get("message", "unknown control error")
            raise RuntimeError(f"{code}: {message}")
        raise RuntimeError("control command failed")


def prompt_choice(label: str, choices: list[str], default: str) -> str:
    print(f"\n{label}:")
    for index, choice in enumerate(choices, 1):
        suffix = " (default)" if choice == default else ""
        print(f"  {index}. {choice}{suffix}")
    while True:
        raw = input(f"Select [default {default}]: ").strip()
        if not raw:
            return default
        if raw.isdigit() and 1 <= int(raw) <= len(choices):
            return choices[int(raw) - 1]
        for choice in choices:
            if raw.casefold() == choice.casefold():
                return choice
        print("Invalid selection.")


def prompt_boolean(label: str, default: bool) -> bool:
    suffix = "Y/n" if default else "y/N"
    while True:
        raw = input(f"{label} [{suffix}]: ").strip().casefold()
        if not raw:
            return default
        if raw in {"y", "yes", "true", "1"}:
            return True
        if raw in {"n", "no", "false", "0"}:
            return False
        print("Enter yes or no.")


def valid_world_name(name: str) -> bool:
    if not 1 <= len(name) <= 14:
        return False
    if not name[0].isalnum() or not name[-1].isalnum():
        return False
    return all(ord(ch) <= 127 and (ch.isalnum() or ch == " ") for ch in name)


def prompt_create_world() -> dict[str, Any]:
    print("\nCreateWorld - new Survivalcraft world")
    print("World names use 1-14 ASCII letters, digits or spaces.")
    while True:
        name = input("World name [ServerWorld]: ").strip() or "ServerWorld"
        if valid_world_name(name):
            break
        print("Invalid world name.")

    seed = input("Seed [random]: ").strip()
    game_mode = prompt_choice(
        "Game mode",
        ["Creative", "Harmless", "Survival", "Challenging", "Cruel"],
        "Survival",
    )
    starting_position = prompt_choice(
        "Starting position",
        ["Easy", "Medium", "Hard"],
        "Easy",
    )
    terrain_choices = ["Continent", "Island"]
    if game_mode == "Creative":
        terrain_choices.extend(["FlatContinent", "FlatIsland"])
    terrain_generation = prompt_choice(
        "Terrain generation",
        terrain_choices,
        "Continent",
    )

    values: dict[str, Any] = {
        "name": name,
        "seed": seed,
        "gameMode": game_mode,
        "startingPosition": starting_position,
        "terrainGeneration": terrain_generation,
        "supernaturalCreatures": prompt_boolean("Enable supernatural creatures", True),
        "friendlyFire": prompt_boolean("Allow friendly fire", True),
        "seasonsChanging": prompt_boolean("Enable changing seasons", True),
    }
    if game_mode == "Creative":
        values["environmentBehavior"] = prompt_choice(
            "Environment behavior",
            ["Living", "Static"],
            "Living",
        )
        values["timeOfDay"] = prompt_choice(
            "Time of day",
            ["Changing", "Day", "Night", "Sunrise", "Sunset"],
            "Changing",
        )
        values["weatherEffects"] = prompt_boolean("Enable weather effects", True)

    print("\nSelected options:")
    print(json.dumps(values, ensure_ascii=False, indent=2))
    if not prompt_boolean("Create this world and enter it", True):
        raise RuntimeError("CreateWorld canceled")
    return values


def wait_for_world_commands(instance: Path, timeout: float = 60.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        response = command(instance, "screen.list")
        if response.get("ok"):
            screens = response.get("result") or []
            if "GameLoading" in screens and "Game" in screens:
                return
        time.sleep(0.25)
    raise RuntimeError("game loading did not finish initializing world commands")


def wait_for_world_loaded(instance: Path, timeout: float = 180.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        response = command(instance, "status")
        if not response.get("ok"):
            print_response(response)
        result = response.get("result") or {}
        if result.get("worldLoaded") and result.get("currentScreen") != "GameLoading":
            print("\nWorld loaded successfully:")
            print(json.dumps(result, ensure_ascii=False, indent=2))
            return
        if result.get("serverError") or result.get("frameError"):
            raise RuntimeError(
                str(result.get("serverError") or result.get("frameError"))
            )
        time.sleep(0.5)
    raise RuntimeError("world was created but did not finish loading within 180 seconds")


def execute_create_world(instance: Path, values: list[str]) -> None:
    arguments = parse_values(values) if values else prompt_create_world()
    wait_for_world_commands(instance)
    response = command(instance, "CreateWorld", arguments)
    print_response(response)
    wait_for_world_loaded(instance)


def execute_sequence(
    instance: Path,
    sequence_file: Path,
    wait: bool,
    timeout: float,
    poll_interval: float,
) -> None:
    with sequence_file.open("r", encoding="utf-8") as stream:
        definition = json.load(stream)
    if isinstance(definition, list):
        arguments: dict[str, Any] = {"steps": definition}
    elif isinstance(definition, dict) and isinstance(definition.get("steps"), list):
        arguments = definition
    else:
        raise RuntimeError("sequence JSON must be a steps array or an object containing steps")

    response = command(instance, "sequence.start", arguments)
    print_response(response)
    if not wait:
        return

    result = response.get("result") or {}
    sequence_id = result.get("sequenceId")
    if not sequence_id:
        raise RuntimeError("sequence.start did not return sequenceId")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status = command(instance, "sequence.status", {"sequenceId": sequence_id})
        if not status.get("ok"):
            print_response(status)
        status_result = status.get("result") or {}
        state = status_result.get("state")
        if state in {"completed", "failed", "canceled"}:
            print_response(status)
            if state != "completed":
                raise RuntimeError(f"sequence ended with state {state}")
            return
        time.sleep(poll_interval)
    raise RuntimeError(f"sequence {sequence_id} did not finish within {timeout:g} seconds")


def list_instances(root: Path) -> None:
    instances = root / "instances"
    if not instances.is_dir():
        return
    for item in sorted(instances.iterdir()):
        if item.is_dir() and (item / CONFIG_FILE).is_file():
            pid = read_pid(item)
            state = "running" if process_alive(pid) else "stopped"
            print(f"{item.name}\t{state}\t{pid or '-'}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    script_directory = Path(__file__).resolve().parent
    default_root = (
        script_directory.parent if script_directory.name == "tools" else script_directory
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=default_root,
        help="server root containing template/ and instances/",
    )
    subparsers = parser.add_subparsers(dest="action", required=True)

    create = subparsers.add_parser("create")
    create.add_argument("instance")
    create.add_argument("--port", type=int)
    create.add_argument(
        "--copy-files",
        action="store_true",
        help="copy immutable files instead of using hard links",
    )

    for action in ("start", "status", "stop"):
        command_parser = subparsers.add_parser(action)
        command_parser.add_argument("instance")
        if action == "stop":
            command_parser.add_argument("--timeout", type=float, default=10.0)

    send = subparsers.add_parser("command")
    send.add_argument("instance")
    send.add_argument("command")
    send.add_argument("values", nargs="*")
    direct = subparsers.add_parser(
        "direct",
        help="control the instance in --root without template/instances",
    )
    direct.add_argument("command")
    direct.add_argument("values", nargs="*")
    create_world = subparsers.add_parser(
        "CreateWorld",
        aliases=["createworld"],
        help="configure a world in the already running direct instance",
    )
    create_world.add_argument("values", nargs="*")
    sequence = subparsers.add_parser(
        "sequence",
        help="submit an asynchronous game-thread command sequence from JSON",
    )
    sequence.add_argument("file", type=Path)
    sequence.add_argument(
        "--instance",
        help="control instances/<name>; omit to control the direct --root instance",
    )
    sequence.add_argument("--wait", action="store_true")
    sequence.add_argument("--timeout", type=float, default=600.0)
    sequence.add_argument("--poll-interval", type=float, default=0.5)
    subparsers.add_parser("list")
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        if args.action == "create":
            create_instance(root, args.instance, args.port, args.copy_files)
        elif args.action == "list":
            list_instances(root)
        elif args.action.casefold() == "createworld":
            execute_create_world(root, args.values)
        elif args.action == "sequence":
            target = instance_path(root, args.instance) if args.instance else root
            execute_sequence(
                target,
                args.file.resolve(),
                args.wait,
                args.timeout,
                args.poll_interval,
            )
        elif args.action == "direct":
            if args.command.casefold() in {"createworld", "world.create"}:
                execute_create_world(root, args.values)
            else:
                values = parse_values(args.values)
                response = command(root, args.command, values)
                print_response(response)
        else:
            instance = instance_path(root, args.instance)
            if not instance.is_dir():
                raise RuntimeError(f"instance does not exist: {instance}")
            if args.action == "start":
                start_instance(instance)
            elif args.action == "stop":
                stop_instance(instance, args.timeout)
            elif args.action == "status":
                print_response(command(instance, "status"))
            elif args.action == "command":
                if args.command.casefold() in {"createworld", "world.create"}:
                    execute_create_world(instance, args.values)
                else:
                    values = parse_values(args.values)
                    response = command(instance, args.command, values)
                    print_response(response)
        return 0
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
