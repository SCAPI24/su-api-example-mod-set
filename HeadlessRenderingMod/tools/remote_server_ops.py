#!/usr/bin/env python3
"""Operate a direct headless server installation from the server itself."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
import zipfile

import serverctl


TASK_NAME = "SurvivalcraftServer-Manual"
FORBIDDEN_MOD_LIBRARIES = {
    "engine.dll",
    "entitysystem.dll",
    "gameentitysystem.dll",
    "survivalcraft.dll",
}


def tasklist_pids() -> list[int]:
    # Source: Windows tasklist.exe CSV output
    completed = subprocess.run(
        [
            str(Path("C:/Windows/System32/tasklist.exe")),
            "/FI",
            "IMAGENAME eq Survivalcraft.exe",
            "/FO",
            "CSV",
            "/NH",
        ],
        capture_output=True,
        check=False,
    )
    rows = csv.reader(completed.stdout.decode("mbcs", errors="replace").splitlines())
    result: list[int] = []
    for row in rows:
        if len(row) < 2 or row[0].casefold() != "survivalcraft.exe":
            continue
        try:
            result.append(int(row[1]))
        except ValueError:
            continue
    return result


def wait_for_process_state(running: bool, timeout: float) -> list[int]:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        pids = tasklist_pids()
        if bool(pids) == running:
            return pids
        time.sleep(0.25)
    state = "start" if running else "stop"
    raise RuntimeError(f"Survivalcraft did not {state} within {timeout:g} seconds")


def install_manual_task(root: Path) -> None:
    # Source: tools/SurvivalcraftServer-Manual.xml
    definition = Path(__file__).with_name("SurvivalcraftServer-Manual.xml")
    if not definition.is_file():
        raise RuntimeError(f"missing task definition: {definition}")
    temporary = definition.with_name("SurvivalcraftServer-Manual.utf16.xml")
    xml = definition.read_text(encoding="utf-8").replace(
        'encoding="UTF-8"', 'encoding="UTF-16"'
    )
    temporary.write_text(xml, encoding="utf-16")
    try:
        completed = subprocess.run(
            [
                str(Path("C:/Windows/System32/schtasks.exe")),
                "/Create",
                "/TN",
                TASK_NAME,
                "/XML",
                str(temporary),
                "/F",
            ],
            capture_output=True,
            check=False,
        )
    finally:
        temporary.unlink(missing_ok=True)
    if completed.returncode != 0:
        message = completed.stderr.decode("mbcs", errors="replace").strip()
        raise RuntimeError(message or "failed to install the manual launch task")
    print(f"Installed manual task '{TASK_NAME}' for {root}")


def start(root: Path, timeout: float) -> None:
    if tasklist_pids():
        raise RuntimeError("Survivalcraft is already running")
    completed = subprocess.run(
        [
            str(Path("C:/Windows/System32/schtasks.exe")),
            "/Run",
            "/TN",
            TASK_NAME,
        ],
        capture_output=True,
        check=False,
    )
    if completed.returncode != 0:
        message = completed.stderr.decode("mbcs", errors="replace").strip()
        raise RuntimeError(message or "failed to run the manual launch task")
    pids = wait_for_process_state(True, timeout)
    (root / serverctl.PID_FILE).write_text(str(pids[0]), encoding="ascii")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            response = serverctl.command(root, "status")
            if response.get("ok"):
                serverctl.print_response(response)
                return
        except (OSError, RuntimeError, json.JSONDecodeError):
            pass
        time.sleep(0.25)
    raise RuntimeError("control server did not become ready")


def loaded_world_selector(root: Path) -> str | None:
    response = serverctl.command(root, "world.list")
    if not response.get("ok"):
        return None
    for world in response.get("result") or []:
        if isinstance(world, dict) and world.get("loaded"):
            return str(world.get("directoryName") or world.get("name") or "") or None
    return None


def join_world(root: Path, selector: str, timeout: float) -> None:
    # Source: HeadlessRenderingMod/Server/GameControlCommands.cs:JoinWorld
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        response = serverctl.command(root, "status")
        result = response.get("result") or {}
        if (
            response.get("ok")
            and result.get("currentScreen") == "MainMenu"
            and not result.get("screenAnimating")
        ):
            break
        time.sleep(0.25)
    else:
        raise RuntimeError("main menu did not become ready for world.join")
    serverctl.print_response(serverctl.command(root, "world.join", {"world": selector}))
    while time.monotonic() < deadline:
        response = serverctl.command(root, "status")
        result = response.get("result") or {}
        if (
            response.get("ok")
            and result.get("worldLoaded")
            and result.get("currentScreen") == "Game"
            and not result.get("screenAnimating")
        ):
            serverctl.print_response(response)
            return
        time.sleep(0.5)
    raise RuntimeError(f"world '{selector}' did not become ready")


def stop(root: Path, timeout: float) -> None:
    pids = tasklist_pids()
    if not pids:
        (root / serverctl.PID_FILE).unlink(missing_ok=True)
        return
    try:
        serverctl.print_response(serverctl.command(root, "shutdown"))
    except (OSError, RuntimeError, json.JSONDecodeError) as error:
        print(f"graceful shutdown failed: {error}", file=sys.stderr)
    try:
        wait_for_process_state(False, timeout)
    except RuntimeError:
        for pid in pids:
            serverctl.terminate_process(pid)
        wait_for_process_state(False, 5.0)
    (root / serverctl.PID_FILE).unlink(missing_ok=True)


def validate_mod(path: Path) -> None:
    if path.suffix.casefold() != ".scmod" or not path.is_file():
        raise RuntimeError(f"not an scmod file: {path}")
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
    if "ModInfo.xml" not in names or any("\\" in name for name in names):
        raise RuntimeError("invalid scmod root or entry path separators")
    forbidden = [
        name
        for name in names
        if name.rsplit("/", 1)[-1].casefold() in FORBIDDEN_MOD_LIBRARIES
    ]
    if forbidden:
        raise RuntimeError(f"forbidden game libraries in scmod: {forbidden}")


def deploy_mod(root: Path, source: Path, timeout: float) -> None:
    # Source: AGENTS.md .scmod packaging and deployment rules
    source = source.resolve()
    validate_mod(source)
    mods = root / "Mods"
    mods.mkdir(exist_ok=True)
    destination = mods / source.name
    staging = mods / f".{source.name}.deploying"
    backup = mods / f".{source.name}.previous"
    shutil.copy2(source, staging)
    expected_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    world = loaded_world_selector(root)
    stop(root, timeout)
    backup.unlink(missing_ok=True)
    if destination.exists():
        destination.replace(backup)
    staging.replace(destination)
    try:
        start(root, timeout)
        if world:
            join_world(root, world, timeout)
    except Exception:
        destination.unlink(missing_ok=True)
        if backup.exists():
            backup.replace(destination)
            start(root, timeout)
            if world:
                join_world(root, world, timeout)
        raise
    backup.unlink(missing_ok=True)
    actual_hash = hashlib.sha256(destination.read_bytes()).hexdigest()
    if actual_hash != expected_hash:
        raise RuntimeError("deployed mod hash does not match source")
    print(f"Deployed {destination.name} SHA256={actual_hash}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parent.parent,
    )
    parser.add_argument("--timeout", type=float, default=30.0)
    subparsers = parser.add_subparsers(dest="action", required=True)
    subparsers.add_parser("install-task")
    subparsers.add_parser("status")
    subparsers.add_parser("start")
    subparsers.add_parser("stop")
    subparsers.add_parser("restart")
    deploy = subparsers.add_parser("deploy-mod")
    deploy.add_argument("path", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        if args.action == "install-task":
            install_manual_task(root)
        elif args.action == "status":
            serverctl.print_response(serverctl.command(root, "status"))
        elif args.action == "start":
            start(root, args.timeout)
        elif args.action == "stop":
            stop(root, args.timeout)
        elif args.action == "restart":
            world = loaded_world_selector(root)
            stop(root, args.timeout)
            start(root, args.timeout)
            if world:
                join_world(root, world, args.timeout)
        elif args.action == "deploy-mod":
            deploy_mod(root, args.path, args.timeout)
        return 0
    except (OSError, RuntimeError, ValueError, zipfile.BadZipFile) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
