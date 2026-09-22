# Sync the ScMultiplayer package to BOTH ends in one step.
#
# Why this exists: Message.IsProtocolCompatible requires the host and the client to have an
# identical BuildFingerprint. Rebuilding only one end makes every join fail silently at the
# protocol gate (the caller still logs "JoinGame sent"):
#   WARNING: [ScMP] Refused incompatible room before join: host=build <A>, local=build <B>
# So: never rebuild one end alone - always run this script.
#
# Usage:
#   $env:SCMP_SSH_PASSWORD = '<password>'   # or configure ssh keys and leave it empty
#   $env:SCMP_ASKPASS      = 'C:\path\to\askpass.exe'   # optional, needed when the password env var is used
#   pwsh -File tools\sync-both-ends.ps1 [-SkipBuild] [-SkipClient] [-SkipHost]
param(
    [switch]$SkipBuild,
    [switch]$SkipClient,
    [switch]$SkipHost,
    [string]$RemoteHost = '<server-ip>',
    [string]$RemoteUser = 'Administrator',
    [string]$RemoteRoot = 'C:\SurvivalcraftServer',
    [string]$AskPass = $env:SCMP_ASKPASS
)

$ErrorActionPreference = 'Stop'

$scmpDir = Split-Path -Parent $PSScriptRoot
$modRepo = Split-Path -Parent $scmpDir
$repoRoot = Split-Path -Parent $modRepo
$project = Join-Path $scmpDir 'ScMultiplayer.csproj'
$modInfoText = Get-Content -LiteralPath (Join-Path $scmpDir 'ModInfo.xml') -Raw
$version = $null
try { $version = ([xml]$modInfoText).Mod.ModInfo.Version } catch { }
if ([string]::IsNullOrWhiteSpace($version)) {
    $match = [regex]::Match($modInfoText, '<Version>\s*([^<]+?)\s*</Version>')
    if ($match.Success) { $version = $match.Groups[1].Value }
}
if ([string]::IsNullOrWhiteSpace($version)) { throw 'cannot read <Version> from ModInfo.xml' }
$packageName = "[SuAPI]ScMultiplayer-$version.scmod"
$package = Join-Path $scmpDir $packageName
$clientMods = Join-Path $repoRoot 'publish\[SuAPI]Survivalcraft\Mods'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Scmod {
    param([string]$Out, [hashtable[]]$Entries)
    if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($Out, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($e in $Entries) {
            $entry = $zip.CreateEntry($e.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            $stream = $entry.Open()
            $bytes = [System.IO.File]::ReadAllBytes($e.Path)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Dispose()
        }
    } finally { $zip.Dispose() }
}

if (-not $SkipBuild) {
    Write-Host '== building ScMultiplayer (Release, runs Obfuscar)'
    $output = & dotnet build $project -c Release -v:m 2>&1
    if ($LASTEXITCODE -ne 0) { $output | Select-String -Pattern 'error' | ForEach-Object { Write-Host $_ }; throw 'build failed' }
}

$obfuscated = Join-Path $scmpDir 'bin\Release\net8.0\Obfuscated\ScMultiplayer.dll'
$comms = Join-Path $scmpDir 'bin\Release\net8.0\Comms.dll'
if (-not (Test-Path -LiteralPath $obfuscated)) { throw "missing $obfuscated - build first" }
if (-not (Test-Path -LiteralPath $comms)) { throw "missing $comms - build first" }

Write-Host '== packaging'
New-Scmod -Out $package -Entries @(
    @{ Name = 'ModInfo.xml'; Path = (Join-Path $scmpDir 'ModInfo.xml') },
    @{ Name = 'Lib/ScMultiplayer.dll'; Path = $obfuscated },
    @{ Name = 'Lib/Comms.dll'; Path = $comms }
)
$sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLower()
Write-Host ("   {0}  {1} bytes  SHA256={2}" -f $packageName, (Get-Item -LiteralPath $package).Length, $sha)

if (-not $SkipClient) {
    Write-Host '== client end'
    if (-not (Test-Path -LiteralPath $clientMods)) { throw "client Mods dir not found: $clientMods" }
    Copy-Item -LiteralPath $package -Destination (Join-Path $clientMods $packageName) -Force
    Write-Host ("   copied to {0} (restart the game to load it)" -f $clientMods)
}

if (-not $SkipHost) {
    Write-Host '== host end'
    if ([string]::IsNullOrWhiteSpace($AskPass) -and $env:SCMP_SSH_PASSWORD) {
        throw 'SCMP_SSH_PASSWORD is set but SCMP_ASKPASS points nowhere; set SCMP_ASKPASS to the askpass helper'
    }
    if ($AskPass) {
        $env:SSH_ASKPASS = $AskPass
        $env:SSH_ASKPASS_REQUIRE = 'force'
        $env:SSH_ASKPASS_PW = $env:SCMP_SSH_PASSWORD
    }
    $sshOptions = @(
        '-o', 'StrictHostKeyChecking=no',
        '-o', 'PreferredAuthentications=password',
        '-o', 'PubkeyAuthentication=no',
        '-o', 'ConnectTimeout=15',
        '-o', 'ServerAliveInterval=10'
    )
    $target = "$RemoteUser@$RemoteHost"
    $staging = Join-Path $env:TEMP 'scmp-sync'
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    $upload = Join-Path $staging 'scmp-upload.scmod'
    Copy-Item -LiteralPath $package -Destination $upload -Force
    & scp @sshOptions $upload "${target}:C:/Windows/Temp/scmpin/scmp-upload.scmod"
    if ($LASTEXITCODE -ne 0) { throw 'scp failed' }
    # deploy-mod uses the source file name for the destination, so rename it to the canonical
    # name on the server first (never leave a package called scmp-upload.scmod in Mods/).
    $remoteScript = @"
`$ErrorActionPreference = 'Stop'
`$stage = 'C:\Windows\Temp\scmpin'
`$src = Join-Path `$stage 'scmp-upload.scmod'
`$dst = Join-Path `$stage '$packageName'
if (Test-Path -LiteralPath `$dst) { Remove-Item -LiteralPath `$dst -Force }
Rename-Item -LiteralPath `$src -NewName (Split-Path -Leaf `$dst)
Write-Output ('staged ' + '$packageName')
"@
    $remoteScriptPath = Join-Path $staging 'rename.ps1'
    [System.IO.File]::WriteAllText($remoteScriptPath, $remoteScript, (New-Object System.Text.UTF8Encoding($false)))
    & scp @sshOptions $remoteScriptPath "${target}:C:/Windows/Temp/scmp-rename.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'scp (rename script) failed' }
    & ssh @sshOptions $target 'powershell -ExecutionPolicy Bypass -File C:\Windows\Temp\scmp-rename.ps1'
    if ($LASTEXITCODE -ne 0) { throw 'remote rename failed' }
    & ssh @sshOptions $target ("$RemoteRoot\tools\python\python.exe $RemoteRoot\tools\remote_server_ops.py --timeout 90 deploy-mod C:\Windows\Temp\scmpin\$packageName")
    if ($LASTEXITCODE -ne 0) { throw 'deploy-mod failed' }
}

Write-Host '== verify: both ends must report the same package hash'
Write-Host ("   local : {0}" -f $sha)
if (-not $SkipHost) {
    $remoteSha = (& ssh @sshOptions $target "powershell -Command (Get-FileHash -LiteralPath $RemoteRoot\Mods\$packageName -Algorithm SHA256).Hash") -join ''
    Write-Host ("   remote: {0}" -f $remoteSha.Trim().ToLower())
}
Write-Host 'Done. Restart the local client so it loads the new build.'
