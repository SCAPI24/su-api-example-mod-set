"""Read-only source contract checks; not compilation or gameplay verification."""
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
errors = []


def read(path):
    try:
        return path.read_text(encoding="utf-8-sig")
    except (OSError, UnicodeError) as exc:
        errors.append(f"Cannot read {path}: {exc}")
        return ""


def require(source, label, fragments):
    for fragment in fragments:
        if fragment not in source:
            errors.append(f"{label}: missing source contract {fragment!r}")


entry = read(ROOT / "Plug" / "MemoryBankDrawMod.cs")
presenter = read(ROOT / "Func" / "MemoryBankDialogPresenter.cs")
dialog = read(ROOT / "Func" / "SuEditMemoryBankDialog.cs")
require(entry, "Entry", [
    'SubscribeEvent("Frame.Update"',
    "MemoryBankDialogPresenter.ValidateContract()",
    "MemoryBankDialogPresenter.EnhanceOpenDialogs()",
    "UnsubscribeEvent(m_frameToken)",
    "MemoryBankDialogPresenter.CloseOwnedDialogs()",
    "ExceptionManager.ReportExceptionToUser",
    "Dependencies => Array.Empty<string>()", "IsMergeLib => true",
])
for forbidden in ("GameDatabase.GameDatabase", "HandleGameDatabase",
                  "SuSubsystemMemoryBankBlockBehavior", "MemoryBankDialogCompatibility"):
    if forbidden in entry:
        errors.append(f"Entry still uses obsolete replacement path: {forbidden}")
for old_name in ("MemoryBankDialogCompatibility.cs", "SuSubsystemMemoryBankBlockBehavior.cs"):
    if (ROOT / "Func" / old_name).exists():
        errors.append(f"Obsolete implementation remains: {old_name}")
require(presenter, "Presenter", [
    "DialogsManager.Dialogs", "GetType() != typeof(EditMemoryBankDialog)",
    '"m_memoryBankData"', '"m_handler"',
    "s_memoryBankDataField.FieldType != typeof(MemoryBankData)",
    "s_handlerField.FieldType != typeof(Action)",
    "SuEditMemoryBankDialog.ValidateTextBoxContract()",
    "(MemoryBankData)s_memoryBankDataField.GetValue(nativeDialog)",
    "(Action)s_handlerField.GetValue(nativeDialog)",
    "new SuEditMemoryBankDialog(memoryBankData, handler)",
    "DialogsManager.HideDialog(nativeDialog)",
    "DialogsManager.ShowDialog(parentWidget, drawDialog)",
    "s_ownedDialogs.Add(drawDialog)", "public static void CloseOwnedDialogs()",
    "DialogsManager.HideDialog(dialog)", "s_ownedDialogs.Clear()",
])
require(dialog, "Draw dialog", [
    "m_handler = handler;", "m_memoryBankData = memoryBankData;",
    "m_tmpMemoryBankData = (MemoryBankData)memoryBankData.Copy()",
    "m_memoryBankData.Data = m_tmpMemoryBankData.Data;",
    "Dismiss(result: true)", "Dismiss(result: false)",
    "if (m_handler != null && result)", "m_handler();",
    "s_textProp.PropertyType != typeof(string)",
    "!s_textProp.CanRead", "!s_textProp.CanWrite",
])
# Metadata is parsed structurally; the checks above deliberately inspect source only.
try:
    manifest = ET.fromstring(read(ROOT / "ModInfo.xml"))
    project = ET.fromstring(read(ROOT / "MemoryBankDrawMod.csproj"))
    if manifest.findtext("ModInfo/APIVersion") != "0.2.0.0":
        errors.append("ModInfo APIVersion must match NuGet API 0.2.0.0")
    if manifest.findtext("ModInfo/IsMergeLib") != "true":
        errors.append("ModInfo must use IsMergeLib=true")
    dependencies = manifest.find("Dependencies")
    if dependencies is not None and (len(dependencies) or (dependencies.text or "").strip()):
        errors.append("ModInfo declares a dependency; this Mod must remain standalone")
    if project.findtext("PropertyGroup/TargetFramework") != "net10.0":
        errors.append("Project must target net10.0 for the NuGet API")
    if project.findall("ItemGroup/ProjectReference") or project.findall("ItemGroup/Reference"):
        errors.append("Core dependencies must use NuGet, without source or local DLL references")
    packages = project.findall("ItemGroup/PackageReference")
    expected = {"Engine", "EntitySystem", "Survivalcraft"}
    core_packages = [ref for ref in packages if ref.attrib.get("Include") in expected]
    if len(core_packages) != 3 or {ref.attrib.get("Include") for ref in core_packages} != expected:
        errors.append("Project must reference all three core NuGet packages exactly once")
    if any(ref.attrib.get("Version") != "[0.2.0.0]" for ref in core_packages):
        errors.append("Core NuGet package versions must be pinned to [0.2.0.0]")
    if project.findtext("PropertyGroup/CopyLocalLockFileAssemblies") != "true":
        errors.append("NuGet dependency DLLs must be copied for Obfuscar resolution")
    obfuscar = ET.fromstring(read(ROOT / "Obfuscar.xml"))
    modules = obfuscar.findall("Module")
    if len(modules) != 1 or modules[0].attrib.get("file") != "$(InPath)\\MemoryBankDrawMod.dll":
        errors.append("Obfuscar must only transform MemoryBankDrawMod.dll")
    elif not any(t.attrib.get("name") == "MemoryBankDrawMod.MemoryBankDrawMod"
                 for t in modules[0].findall("SkipType")):
        errors.append("Obfuscar must preserve the IMod entry type")
    if not any(p.attrib.get("path") == "$(InPath)" for p in obfuscar.findall("AssemblySearchPath")):
        errors.append("Obfuscar must resolve core DLLs through AssemblySearchPath")
except ET.ParseError as exc:
    errors.append(f"Invalid project/manifest XML: {exc}")

if errors:
    for error in errors:
        print(f"FAIL: {error}")
    sys.exit(1)
print("PASS: MemoryBankDrawMod NuGet 0.2.0.0 source contracts and metadata.")
print("SCOPE: Read-only static checks only; no build, runtime, UI, save or network validation.")
