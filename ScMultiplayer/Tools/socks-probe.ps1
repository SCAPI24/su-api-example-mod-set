# Probe which ports a local SOCKS5 proxy actually relays (not just "accepts").
#
# Why: Clash/mihomo answer SOCKS5 CONNECT with "succeeded" without checking whether the
# upstream node can reach the target. So "connect succeeded" proves nothing. This script
# distinguishes the three real outcomes:
#   refused   - SOCKS reply code != 0 (proxy rejected)
#   reached   - connection opened and the peer closed it (for the game port that is the host's
#               TCP transmitter dropping a connection that never completed the handshake)
#   silent    - connection opened, nothing came back within the timeout: the proxy is not
#               relaying this destination (this is what "accepted but silent" means)
#
# Usage:
#   pwsh -File Tools\socks-probe.ps1 -Targets <server-ip>:51459,suceru.site:51459,gitee.com:22
param(
    [string]$Proxy = '127.0.0.1:7890',
    [string]$Targets = '<server-ip>:51459,suceru.site:51459',
    [int]$TimeoutMs = 8000
)

$ErrorActionPreference = 'Stop'
$proxyParts = $Proxy.Split(':')
$proxyHost = $proxyParts[0]
$proxyPort = [int]$proxyParts[1]

function Read-Exactly {
    param($Stream, [int]$Count, [int]$Timeout)
    $buffer = New-Object byte[] $Count
    $read = 0
    $Stream.ReadTimeout = $Timeout
    while ($read -lt $Count) {
        $got = $Stream.Read($buffer, $read, $Count - $read)
        if ($got -le 0) { return $null }
        $read += $got
    }
    return $buffer
}

foreach ($target in $Targets.Split(',')) {
    if ([string]::IsNullOrWhiteSpace($target)) { continue }
    $parts = $target.Split(':')
    $hostName = $parts[0]
    $port = [int]$parts[1]
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect($proxyHost, $proxyPort)
        $stream = $client.GetStream()
        $stream.Write([byte[]]@(5, 1, 0), 0, 3)
        $stream.Flush()
        $greeting = Read-Exactly -Stream $stream -Count 2 -Timeout $TimeoutMs
        if ($null -eq $greeting -or $greeting[0] -ne 5 -or $greeting[1] -ne 0) {
            Write-Output ("{0}:{1}  proxy handshake failed" -f $hostName, $port)
            continue
        }
        $nameBytes = [System.Text.Encoding]::ASCII.GetBytes($hostName)
        $request = New-Object System.Collections.Generic.List[byte]
        $request.AddRange([byte[]]@(5, 1, 0, 3))
        $request.Add([byte]$nameBytes.Length)
        $request.AddRange($nameBytes)
        $request.Add([byte]($port -shr 8))
        $request.Add([byte]($port -band 0xff))
        $stream.Write($request.ToArray(), 0, $request.Count)
        $stream.Flush()
        $head = Read-Exactly -Stream $stream -Count 4 -Timeout $TimeoutMs
        if ($null -eq $head) { Write-Output ("{0}:{1}  silent (no SOCKS reply)" -f $hostName, $port); continue }
        if ($head[1] -ne 0) { Write-Output ("{0}:{1}  refused (SOCKS reply {2})" -f $hostName, $port, $head[1]); continue }
        # Drain the rest of the reply (BND.ADDR + BND.PORT); 6 bytes for IPv4, 18 for IPv6.
        $drain = if ($head[3] -eq 1) { 6 } elseif ($head[3] -eq 4) { 18 } else {
            $len = Read-Exactly -Stream $stream -Count 1 -Timeout $TimeoutMs
            $len[0] + 2
        }
        [void](Read-Exactly -Stream $stream -Count $drain -Timeout $TimeoutMs)
        # Now wait for the peer: data, EOF or silence.
        $stream.ReadTimeout = $TimeoutMs
        try {
            $probe = New-Object byte[] 64
            $n = $stream.Read($probe, 0, $probe.Length)
            if ($n -gt 0) { Write-Output ("{0}:{1}  reached (peer sent {2} bytes)" -f $hostName, $port, $n) }
            else { Write-Output ("{0}:{1}  reached (peer closed the connection)" -f $hostName, $port) }
        } catch {
            Write-Output ("{0}:{1}  SILENT - proxy accepted but relays nothing (node cannot carry this port)" -f $hostName, $port)
        }
    } catch {
        Write-Output ("{0}:{1}  error: {2}" -f $hostName, $port, $_.Exception.Message)
    } finally {
        try { $client.Close() } catch { }
    }
}
