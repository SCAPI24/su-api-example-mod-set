from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
MOD_ROOT = ROOT.parent

entry = (ROOT / "Plug" / "MemoryBankDrawMod.cs").read_text(encoding="utf-8-sig")
bridge_path = ROOT / "Func" / "MemoryBankDialogCompatibility.cs"
standalone = (ROOT / "Func" / "SuSubsystemMemoryBankBlockBehavior.cs").read_text(
    encoding="utf-8-sig"
)
multiplayer = (
    MOD_ROOT
    / "ScMultiplayer"
    / "Func"
    / "Subsystem"
    / "SuSubsystemMemoryBankBlockBehavior.cs"
).read_text(encoding="utf-8-sig")

errors = []
if 'SubscribeEvent("Frame.Update"' not in entry:
    errors.append("MemoryBankDrawMod does not subscribe to Frame.Update")
if "MemoryBankDialogCompatibility.ReplaceNativeDialogs" not in entry:
    errors.append("Frame.Update does not invoke the compatibility bridge")
if not bridge_path.exists():
    errors.append("MemoryBankDialogCompatibility.cs is missing")
else:
    bridge = bridge_path.read_text(encoding="utf-8-sig")
    for required in [
        "DialogsManager.Dialogs",
        "typeof(EditMemoryBankDialog)",
        "GetField(",
        '"m_memoryBankData"',
        '"m_handler"',
        "new SuEditMemoryBankDialog(memoryBankData, handler)",
        "DialogsManager.HideDialog(nativeDialog)",
        "DialogsManager.ShowDialog(parentWidget, drawDialog)",
    ]:
        if required not in bridge:
            errors.append(f"Compatibility bridge is missing: {required}")
if "new SuEditMemoryBankDialog" not in standalone:
    errors.append("Standalone subsystem no longer opens the draw dialog")
if "new EditMemoryBankDialog" not in multiplayer:
    errors.append("ScMultiplayer no longer exposes a native dialog for replacement")

if errors:
    for error in errors:
        print(f"FAIL: {error}")
    sys.exit(1)

print("MemoryBankDrawMod compatibility contracts passed.")
