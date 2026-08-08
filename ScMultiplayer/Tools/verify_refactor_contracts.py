from pathlib import Path
import re
import zipfile
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[3]
MOD = ROOT / "Mod" / "ScMultiplayer"
PACKAGE = ROOT / "publish" / "Windows" / "Mods" / "[SuAPI]ScMultiplayer-2.0.8.scmod"


def read(path):
    return path.read_text(encoding="utf-8")


def require(text, pattern, label):
    if not re.search(pattern, text, re.MULTILINE | re.DOTALL):
        raise AssertionError(f"missing contract: {label}")


def main():
    transfer = read(MOD / "Core" / "WorldTransferRegistry.cs")
    catch_up = read(MOD / "Core" / "JoinCatchUpRegistry.cs")
    assets = read(MOD / "Core" / "SessionAssetRegistry.cs")
    copier = read(MOD / "Core" / "WorldSnapshotFileCopier.cs")
    sanitizer = read(MOD / "Modules" / "Join" / "WorldArchiveSanitizer.cs")
    player_keys = read(MOD / "Modules" / "Player" / "PlayerRecordKeyResolver.cs")
    value_codec = read(MOD / "Modules" / "Player" / "PlayerProfileValueCodec.cs")
    skin_hash = read(MOD / "Modules" / "Player" / "SkinHashCodec.cs")
    bounded_reader = read(MOD / "Modules" / "Player" / "BoundedStreamReader.cs")
    skin_image = read(MOD / "Modules" / "Player" / "SkinImageValidator.cs")
    read_only_snapshot = read(MOD / "Core" / "PlayerReadOnlyStateSnapshot.cs")
    read_only_capture = read(MOD / "Modules" / "Player" / "PlayerReadOnlyStateCapture.cs")
    authoritative_snapshot = read(MOD / "Core" / "AuthoritativePlayerStateSnapshot.cs")
    input_policy = read(MOD / "Modules" / "Player" / "PlayerInputStatePolicy.cs")
    join_budget = read(MOD / "Transport" / "JoinTransferBudgetPolicy.cs")
    join_ready = read(MOD / "Modules" / "Join" / "JoinReadyPolicy.cs")
    action_validation = read(MOD / "Modules" / "Session" / "ActionRequestValidationPolicy.cs")
    action_sequences = read(MOD / "Core" / "PlayerActionSequencePolicy.cs")
    status_formatter = read(MOD / "Modules" / "Runtime" / "NetworkStatusFormatter.cs")
    transfer_paths = read(MOD / "Modules" / "Join" / "WorldTransferPathPolicy.cs")
    record_values = read(MOD / "Modules" / "Player" / "PlayerRecordValuePolicy.cs")
    ingress_command = read(MOD / "Networking" / "NetworkIngressCommand.cs")
    ingress_diagnostics = read(
        MOD / "Diagnostics" / "NetworkIngressDiagnosticsCollector.cs")
    runtime_verifier = read(MOD / "Tools" / "analyze_runtime_verification.py")
    runtime = read(MOD / "Core" / "ScMultiplayerRuntimeState.cs")
    sender = read(MOD / "Networking" / "NetworkMessageSender.cs")
    router = read(MOD / "Control" / "NetworkMessageRouter.cs")

    require(transfer, r"class WorldTransferRegistry", "world transfer registry")
    require(transfer, r"OutgoingTransfers", "outgoing transfer ownership")
    require(transfer, r"IncomingTransfers", "incoming transfer ownership")
    require(transfer, r"PendingWorldReadyTransferId", "world ready barrier")
    require(transfer, r"PendingCircuitReadyTransferId", "circuit ready barrier")
    require(transfer, r"ClientJoinReadyStageValue", "join ready stage")
    require(transfer, r"ResetClientTerrainBaselines", "terrain baseline reset")
    require(transfer, r"RemoveClient", "world transfer client removal")
    require(catch_up, r"class JoinCatchUpRegistry", "catch-up registry")
    require(catch_up, r"TransfersAwaitingReady", "awaiting ready ownership")
    require(catch_up, r"CompletedReadyTransfers", "completed ready ownership")
    require(catch_up, r"RemoveClient", "catch-up client removal")
    require(assets, r"class SessionAssetRegistry", "session asset registry")
    require(assets, r"IncomingSkinAssetTransfers", "skin transfer ownership")
    require(assets, r"DetachWorldSessionAssets", "session asset detachment")
    require(copier, r"class WorldSnapshotFileCopier", "snapshot file copier")
    require(copier, r"CopyDirectory", "snapshot directory copy")
    require(sanitizer, r"class WorldArchiveSanitizer", "world archive sanitizer")
    require(sanitizer, r"RemoveNetworkPlayers", "network player archive filtering")
    require(sanitizer, r"Project\.xml", "project xml archive filtering")
    require(player_keys, r"class PlayerRecordKeyResolver", "player record key resolver")
    require(player_keys, r"GetPlayerRecordKey", "player record key formatting")
    require(player_keys, r"GetNetworkRecordKey", "network record key formatting")
    require(value_codec, r"class PlayerProfileValueCodec", "player profile value codec")
    require(value_codec, r"FormatFloat", "profile float formatting")
    require(value_codec, r"ParseIntArray", "profile array parsing")
    require(skin_hash, r"class SkinHashCodec", "skin hash codec")
    require(skin_hash, r"IsValid", "skin hash validation")
    require(skin_hash, r"Parse", "skin hash parsing")
    require(bounded_reader, r"class BoundedStreamReader", "bounded stream reader")
    require(bounded_reader, r"maximumBytes", "bounded payload limit")
    require(skin_image, r"class SkinImageValidator", "skin image validator")
    require(skin_image, r"IsPowerOf2", "skin image dimension validation")
    require(read_only_snapshot, r"struct PlayerReadOnlyStateSnapshot", "player read-only snapshot")
    require(read_only_snapshot, r"ApplyTo\(NetworkPlayerState state\)", "player snapshot application boundary")
    require(read_only_snapshot, r"IsFinite", "player snapshot finite validation")
    require(read_only_capture, r"class PlayerReadOnlyStateCapture", "player snapshot capture")
    require(read_only_capture, r"TryCapture", "player snapshot capture boundary")
    require(authoritative_snapshot, r"struct AuthoritativePlayerStateSnapshot", "authoritative player snapshot")
    require(authoritative_snapshot, r"HasMeaningfulChangeFrom", "authoritative snapshot comparison")
    require(input_policy, r"class PlayerInputStatePolicy", "player input policy")
    require(input_policy, r"Sanitize", "network input sanitization")
    require(input_policy, r"CreateHeld", "held input policy")
    require(join_budget, r"class JoinTransferBudgetPolicy", "join transfer budget policy")
    require(join_budget, r"CalculateAvailableBytesPerSecond", "join budget headroom")
    require(join_budget, r"RefillTokens", "join budget token refill")
    require(join_budget, r"EstimatePacketBytes", "join packet estimate")
    require(join_ready, r"class JoinReadyPolicy", "join ready policy")
    require(join_ready, r"HasTimedOut", "join timeout policy")
    require(action_validation, r"class ActionRequestValidationPolicy", "action validation policy")
    require(action_validation, r"IsSupportedHostRequest", "host request envelope policy")
    require(action_sequences, r"class PlayerActionSequencePolicy", "player action sequence policy")
    require(action_sequences, r"ShouldTrimCache", "sequence cache policy")
    require(status_formatter, r"class NetworkStatusFormatter", "network status formatter")
    require(status_formatter, r"FormatBytesPerSecond", "network byte formatter")
    require(transfer_paths, r"class WorldTransferPathPolicy", "world transfer path policy")
    require(transfer_paths, r"TryNormalizeZipPath", "world zip path policy")
    require(record_values, r"class PlayerRecordValuePolicy", "player record value policy")
    require(record_values, r"DefaultTemperature", "player record defaults")
    require(ingress_command, r"struct NetworkIngressCommand", "network ingress command")
    require(ingress_command, r"NetworkIngressCommandKind", "ingress command kind")
    require(ingress_command, r"WithQueue", "ingress queue correlation")
    require(ingress_diagnostics, r"class NetworkIngressDiagnosticsCollector",
            "network ingress diagnostics")
    require(ingress_diagnostics, r"RecordReceive", "ingress receive checkpoint")
    require(ingress_diagnostics, r"RecordEnqueue", "ingress enqueue checkpoint")
    require(ingress_diagnostics, r"RecordApply", "ingress apply checkpoint")
    require(ingress_diagnostics, r"RecordResult", "ingress result checkpoint")
    require(ingress_diagnostics, r"SampleWindowMilliseconds = 5000",
            "bounded ingress summary window")
    require(runtime_verifier, r"vr03JoinEvidence", "VR-03 join evidence output")
    require(runtime_verifier, r"fullVr03.*NOT_RUN", "full VR gate remains explicit")
    require(runtime_verifier, r"--adb-log", "Android runtime log input")

    world_handlers = read(MOD / "Modules" / "Join" / "ScMultiplayerWorldTransferHandlers.cs")
    profile_handlers = read(MOD / "Modules" / "Player" / "ScMultiplayerProfileHandlers.cs")
    legacy_state_aliases = (
        "m_outgoingWorldTransfers",
        "m_incomingWorldTransfers",
        "m_worldTransfersAwaitingReady",
        "m_hostProjectReadyTransfers",
        "m_completedWorldReadyTransfers",
        "m_joinCatchUpJournals",
        "m_pendingJoinCatchUps",
        "m_clientTerrainChunkBaselineRevision",
        "m_clientTerrainJoinBaselineRevision",
        "m_pendingWorldReadyTransferId",
        "m_pendingCircuitReadyTransferId",
        "m_clientJoinReadyStage",
        "m_sessionSkinAssets",
        "m_playerSkinHashes",
        "m_requestedSkinAssetKeys",
        "m_incomingSkinAssetTransfers",
        "m_sentLocalSkinAssetHashes",
        "m_networkWorldSessionAssets",
    )
    for path in (MOD / "Modules").rglob("*.cs"):
        text = read(path)
        for alias in legacy_state_aliases:
            if alias in text:
                raise AssertionError(f"legacy Phase 3 state alias remains: {alias}: {path}")
    require(world_handlers, r"m_worldTransferRegistry\.IncomingTransfers", "direct world transfer ownership")
    require(world_handlers, r"m_joinCatchUpRegistry\.Journals", "direct catch-up ownership")
    require(profile_handlers, r"m_sessionAssetRegistry\.SkinAssets", "direct session asset ownership")

    client_events = read(MOD / "Modules" / "Session" / "ScMultiplayerClientEvents.cs")
    require(client_events, r"m_worldTransferRegistry\.Reset\(\)", "global world transfer reset")
    require(client_events, r"m_joinCatchUpRegistry\.Reset\(\)", "global catch-up reset")
    require(world_handlers, r"DetachWorldSessionAssets\(\)", "session asset release ordering")
    world_objects = read(MOD / "Modules" / "World" / "ScMultiplayerWorldObjectHandlers.cs")
    require(world_objects, r"m_worldTransferRegistry\.RemoveClient\(clientId\)", "per-client transfer removal")
    require(world_objects, r"m_joinCatchUpRegistry\.RemoveClient\(clientId\)", "per-client catch-up removal")

    require(runtime, r"WorldTransferRegistry", "world transfer registry ownership")
    require(runtime, r"JoinCatchUpRegistry", "catch-up registry ownership")
    require(runtime, r"SessionAssetRegistry", "session asset registry ownership")
    require(sender, r"SendRawPayload", "raw transport sender boundary")
    require(sender, r"SendRawMessage", "message transport sender boundary")
    require(router, r"NetworkIngressCommand\.Create", "router ingress command creation")
    require(router, r"RecordReceive\(in command\)", "router receive checkpoint")
    require(router, r"QueueEndOfFrameAction\(command", "structured normal ingress queue")
    require(router, r"QueuePriorityInputAction\(command",
            "structured priority ingress queue")
    require(router, r"QueueWorldTransferAction\(command",
            "structured join ingress queue")

    for path in (MOD / "Modules").rglob("*.cs"):
        text = read(path)
        if "client.SendDirectInput" in text or "server.SendDirectInput" in text:
            raise AssertionError(f"module bypasses NetworkMessageSender: {path}")

    for path in (
        MOD / "Core" / "MultiplayerContext.cs",
        MOD / "Core" / "MultiplayerSessionState.cs",
        MOD / "Core" / "WorldTransferRegistry.cs",
        MOD / "Core" / "JoinCatchUpRegistry.cs",
        MOD / "Core" / "SessionAssetRegistry.cs",
        MOD / "Core" / "WorldSnapshotFileCopier.cs",
        MOD / "Core" / "AuthoritativePlayerStateSnapshot.cs",
        MOD / "Transport" / "JoinTransferBudgetPolicy.cs",
        MOD / "Core" / "PlayerActionSequencePolicy.cs",
    ):
        text = read(path)
        for forbidden in ("using Comms", "using Engine", "ModManager", "Widget"):
            if forbidden in text:
                raise AssertionError(f"forbidden Core dependency {forbidden}: {path}")

    if not PACKAGE.exists():
        raise AssertionError(f"missing package: {PACKAGE}")
    with zipfile.ZipFile(PACKAGE) as archive:
        expected = ["ModInfo.xml", "Lib/ScMultiplayer.dll", "Lib/Comms.dll"]
        if archive.namelist() != expected:
            raise AssertionError(f"unexpected package entries: {archive.namelist()}")
        if archive.testzip() is not None:
            raise AssertionError("package CRC check failed")
        mod_info = ET.fromstring(archive.read("ModInfo.xml"))
        if mod_info.findtext("./ModInfo/IsMergeLib") != "true":
            raise AssertionError("ScMultiplayer package is not merge-lib mode")

    print("refactor contracts: OK")


if __name__ == "__main__":
    main()
