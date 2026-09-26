<#
  sync-player-ai-instance.ps1

  Sync the PlayerAi *instance* content between this repo seed and a game instance root.

  Why this exists
  ---------------
  The instance content (behavior trees .scbtpak / action packages .scatpak / question banks .qbank /
  action scripts .aeact) lives under paths that are NOT tracked by any repo:
      PC      : <repo>/publish/[SuAPI]Survivalcraft/PlayerAi/...
      Android : /sdcard/Download/Survivalcraft/PlayerAi/...
  so it is lost when the publish tree is deleted. The seed IS tracked:
      <repo>/PlayerAiMod/Instance/{BehaviorTrees,Questions,Scripts}
  The factory templates in code (PackageTemplates / QuestionBankTemplates / ActionScriptTemplates)
  are a *different* source: they only fill in missing files and they are the versions baked into
  the mod at build time -- not necessarily what the instance currently has.

  Usage
  -----
    # seed -> instance  (default: only create files that are missing; -Force overwrites)
    powershell -File PlayerAiMod\Tools\sync-player-ai-instance.ps1 -Action deploy `
        -Root "P:\Ugit\Survivalcraft\publish\[SuAPI]Survivalcraft"
    powershell -File PlayerAiMod\Tools\sync-player-ai-instance.ps1 -Action deploy -Force -Root "<instance root>"

    # instance -> seed  (capture what the instance currently has back into the repo)
    powershell -File PlayerAiMod\Tools\sync-player-ai-instance.ps1 -Action capture -Root "<instance root>"

  -Root is the *instance root*: the folder that contains Survivalcraft.exe and PlayerAi\.
  Optional -Seed points at a different seed folder (defaults to ..\Instance next to this script).

  THIS SCRIPT IS INTENTIONALLY ASCII-ONLY. Windows PowerShell 5.1 reads a .ps1 without a BOM as ANSI,
  so non-ASCII text inside a script gets mangled (this project has been hit by exactly that).
  Keep any Chinese explanation in Instance\README.md, not in here.
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('deploy', 'capture')][string]$Action,
    [Parameter(Mandatory = $true)][string]$Root,
    [switch]$Force,
    [string]$Seed
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Seed)) { $Seed = Join-Path $PSScriptRoot '..\Instance' }
$subDirs = @('BehaviorTrees', 'Questions', 'Scripts')

if (-not (Test-Path -LiteralPath $Root)) { throw ("Root does not exist: " + $Root + " (pass the instance root, the folder that has Survivalcraft.exe)") }
$instancePlayerAi = Join-Path $Root 'PlayerAi'

if ($Action -eq 'deploy') { $sourceRoot = $Seed;  $targetRoot = $instancePlayerAi }
else                      { $sourceRoot = $instancePlayerAi; $targetRoot = $Seed }

if (-not (Test-Path -LiteralPath $sourceRoot)) { throw ("source folder does not exist: " + $sourceRoot) }

Write-Host ("seed        : " + $Seed)
Write-Host ("instance    : " + $instancePlayerAi)
Write-Host ("direction   : " + $Action + $(if ($Force) { " (force)" } else { " (fill in missing only)" }))
Write-Host ""

$copied = 0; $skipped = 0
foreach ($sub in $subDirs) {
    $from = Join-Path $sourceRoot $sub
    if (-not (Test-Path -LiteralPath $from)) { Write-Host ("  [" + $sub + "] no such folder in source - skipped"); continue }
    $to = Join-Path $targetRoot $sub
    if (-not (Test-Path -LiteralPath $to)) { New-Item -ItemType Directory -Path $to -Force | Out-Null }

    $files = @(Get-ChildItem -LiteralPath $from -File | Sort-Object Name)
    Write-Host ("  [" + $sub + "] " + $files.Count + " file(s)")
    foreach ($f in $files) {
        $dest = Join-Path $to $f.Name
        if ((Test-Path -LiteralPath $dest) -and (-not $Force)) {
            $skipped++
            Write-Host ("    skip   " + $f.Name)
            continue
        }
        Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        $copied++
        Write-Host ("    copy   " + $f.Name)
    }
}

Write-Host ""
Write-Host ("done: copied=" + $copied + " skipped=" + $skipped)
if ($Action -eq 'capture') {
    Write-Host "Review the seed with: git status PlayerAiMod/Instance"
}
