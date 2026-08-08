from __future__ import annotations

import argparse
import json
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
DEFAULT_WINDOWS_LOG = ROOT / "publish" / "Windows" / "Logs" / "Game.log"
WIRE_PATTERN = re.compile(
    r"Wire protocol mod (?P<version>[^,]+), protocol (?P<protocol>\S+), "
    r"build (?P<build>[0-9a-f]+)", re.IGNORECASE)
DOWNLOAD_PATTERN = re.compile(
    r"World download complete:.*Bytes=(?P<bytes>\d+), Seconds=(?P<seconds>[0-9.]+), "
    r"RepairRounds=(?P<repairs>\d+)")
INGRESS_PATTERN = re.compile(r"event=ingress\.summary\s+(?P<fields>.+)$")

CRITICAL_PATTERNS = (
    "Join barrier timed out",
    "Join world transfer stopped making progress",
    "World transfer checksum failed",
    "retry=30",
    "did not respond for",
    "End-of-frame network action failed",
    "Priority input action failed",
    "World-transfer action failed",
    "Terrain chunk sync action failed",
    "Unknown message type",
    "Unhandled exception",
)

EXTERNAL_ERROR_PATTERNS = (
    "ERROR: Connection failure",
    "ERROR: Failed downloading MOTD",
)


@dataclass(frozen=True)
class LogSource:
    label: str
    text: str


def parse_assignment(value: str) -> tuple[str, str]:
    if "=" not in value:
        raise argparse.ArgumentTypeError("expected LABEL=VALUE")
    label, data = value.split("=", 1)
    if not label or not data:
        raise argparse.ArgumentTypeError("expected non-empty LABEL=VALUE")
    return label, data


def read_file_source(value: str, maximum_bytes: int) -> LogSource:
    label, path_text = parse_assignment(value)
    path = Path(path_text).expanduser().resolve()
    data = path.read_bytes()
    if maximum_bytes > 0:
        data = data[-maximum_bytes:]
    return LogSource(label, data.decode("utf-8", errors="replace"))


def read_adb_source(value: str, maximum_bytes: int) -> LogSource:
    label, endpoint = parse_assignment(value)
    if "::" not in endpoint:
        raise argparse.ArgumentTypeError(
            "expected LABEL=ADB_SERIAL::REMOTE_PATH")
    serial, remote_path = endpoint.split("::", 1)
    command = ["adb", "-s", serial, "exec-out", "tail", "-c",
               str(maximum_bytes), remote_path]
    result = subprocess.run(command, capture_output=True, check=False)
    if result.returncode != 0:
        error = result.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"failed to read {label}: {error}")
    return LogSource(label, result.stdout.decode("utf-8", errors="replace"))


def latest_protocol_session(lines: list[str]) -> list[str]:
    starts = [index for index, line in enumerate(lines)
              if "[ScMP] Wire protocol mod " in line]
    return lines[starts[-1]:] if starts else lines


def parse_fields(text: str) -> dict[str, str]:
    fields = {}
    for item in text.split():
        if "=" in item:
            key, value = item.split("=", 1)
            fields[key] = value
    return fields


def analyze(source: LogSource) -> dict[str, object]:
    lines = latest_protocol_session(source.text.splitlines())
    wire = next((WIRE_PATTERN.search(line) for line in lines
                 if WIRE_PATTERN.search(line)), None)
    downloads = []
    ingress = []
    for line in lines:
        download = DOWNLOAD_PATTERN.search(line)
        if download:
            downloads.append({
                "bytes": int(download.group("bytes")),
                "seconds": float(download.group("seconds")),
                "repairs": int(download.group("repairs")),
            })
        summary = INGRESS_PATTERN.search(line)
        if summary:
            ingress.append(parse_fields(summary.group("fields")))

    critical = [line for line in lines
                if any(pattern in line for pattern in CRITICAL_PATTERNS)]
    external_errors = [line for line in lines
                       if any(pattern in line
                              for pattern in EXTERNAL_ERROR_PATTERNS)]
    other_errors = [line for line in lines if " ERROR:" in line and
                    line not in critical and line not in external_errors]
    joined = sum("[ScMP] GameJoined" in line for line in lines)
    client_ready = sum("Client catch-up complete" in line for line in lines)
    host_ready = sum("World transfer ready:" in line for line in lines)
    circuit_ready = sum("Client circuit bootstrap complete" in line for line in lines)
    terrain_repairs = sum("Terrain recovery requested" in line for line in lines)
    terrain_completes = sum("Terrain recovery complete" in line for line in lines)
    retransmits = sum("retry=" in line or "Retransmit" in line for line in lines)
    join_observed = joined > 0 and client_ready > 0 or host_ready > 0

    return {
        "label": source.label,
        "version": wire.group("version") if wire else "unknown",
        "protocol": wire.group("protocol") if wire else "unknown",
        "build": wire.group("build") if wire else "unknown",
        "lines": len(lines),
        "joined": joined,
        "clientReady": client_ready,
        "hostReady": host_ready,
        "circuitReady": circuit_ready,
        "downloads": downloads,
        "downloadRepairs": sum(item["repairs"] for item in downloads),
        "terrainRecoveryRequested": terrain_repairs,
        "terrainRecoveryCompleted": terrain_completes,
        "retransmitLines": retransmits,
        "ingressSummaries": ingress,
        "critical": critical,
        "externalErrors": external_errors,
        "otherErrors": other_errors,
        "smoke": "PASS" if wire and not critical and not other_errors else "FAIL",
        "vr03JoinEvidence": "OBSERVED" if join_observed else "NOT_OBSERVED",
        "fullVr03": "NOT_RUN",
        "fullVr04": "NOT_RUN",
        "fullVr05": "NOT_RUN",
    }


def print_summary(result: dict[str, object]) -> None:
    print(f"[{result['label']}] mod={result['version']} "
          f"protocol={result['protocol']} build={result['build']}")
    print(f"  smoke={result['smoke']} joinEvidence={result['vr03JoinEvidence']} "
          f"joined={result['joined']} clientReady={result['clientReady']} "
          f"hostReady={result['hostReady']} circuitReady={result['circuitReady']}")
    print(f"  downloads={len(result['downloads'])} "
          f"downloadRepairs={result['downloadRepairs']} "
          f"terrainRecovery={result['terrainRecoveryRequested']}/"
          f"{result['terrainRecoveryCompleted']} "
          f"retransmitLines={result['retransmitLines']} "
          f"ingressSummaries={len(result['ingressSummaries'])}")
    print(f"  critical={len(result['critical'])} "
          f"otherErrors={len(result['otherErrors'])} "
          f"externalErrors={len(result['externalErrors'])}")
    if result["ingressSummaries"]:
        latest = result["ingressSummaries"][-1]
        print("  latestIngress=" + " ".join(
            f"{key}={latest[key]}" for key in sorted(latest)))
    for line in (result["critical"] + result["otherErrors"])[-5:]:
        print("  ! " + line)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Summarize ScMultiplayer runtime verification evidence.")
    parser.add_argument("--log", action="append", default=[],
                        help="LABEL=PATH; may be repeated")
    parser.add_argument("--adb-log", action="append", default=[],
                        help="LABEL=ADB_SERIAL::REMOTE_PATH; may be repeated")
    parser.add_argument("--max-bytes", type=int, default=4 * 1024 * 1024)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    file_logs = args.log or [f"Windows={DEFAULT_WINDOWS_LOG}"]
    sources = [read_file_source(value, args.max_bytes) for value in file_logs]
    sources.extend(read_adb_source(value, args.max_bytes)
                   for value in args.adb_log)
    results = [analyze(source) for source in sources]
    if args.json:
        print(json.dumps(results, ensure_ascii=False, indent=2))
    else:
        for result in results:
            print_summary(result)
    return 1 if any(result["smoke"] == "FAIL" for result in results) else 0


if __name__ == "__main__":
    raise SystemExit(main())
