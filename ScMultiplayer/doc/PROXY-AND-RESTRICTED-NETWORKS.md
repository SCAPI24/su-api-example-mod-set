# 代理 / 受限网络下的联机（客户端 SOCKS5）

> 客户端侧功能，**不涉及任何协议变更**：主机端不需要新版本、不需要配置。
> 实现：`Mod/Comms/Comms/Socks5Proxy.cs`、`Mod/Comms/Comms/ProxyDatagramTransmitter.cs`、
> `Mod/ScMultiplayer/Networking/ScMultiplayerProxySettings.cs`。

## 一、它解决什么

本机跑着 Clash / v2ray 一类代理时，系统级 TUN（虚拟网卡）会和别的软件抢网卡、还需要驱动权限；
而**规则型代理**（fake-ip + 指定策略）又常把游戏端口匹配到不转发 UDP 的出口，表现就是
"本地 TCP 握手成功、对端什么也收不到，加入房间中一直转圈"。

SOCKS5 方案让 Comms **自己**把两条通道送进代理：

| 通道 | SOCKS5 机制 | 说明 |
|---|---|---|
| TCP 可靠流 | `CONNECT <域名>:<端口>` | 用 `ATYP=3` 把**域名**交给代理解析，本机 fake-ip 完全绕开 |
| UDP 数据报 | `UDP ASSOCIATE` + 10 字节报文头 | 每条数据报发到中继端点，回包按头里的源地址还原 |

认证只做「无认证」（`0x00`）：本机代理不需要账号密码；需要认证的代理请在代理侧放开回环地址。

## 二、配置

文件：**游戏目录下的 `ScMultiplayer.proxy.txt`**（`data:/ScMultiplayer.proxy.txt`，不存在时自动创建）

```ini
# enabled: auto = 检测到代理就用（默认）；on = 必须走代理；off = 只走直连
enabled=auto
host=127.0.0.1
port=7890
```

- 默认 `auto` + `127.0.0.1:7890`：**不改任何东西**，开着 Clash 就自动走代理，关掉就自动直连。
- 探测是**真正的 SOCKS5 握手**（不是"端口开着就算"），每 3 秒一次；7890 上放着别的服务不会被误判。
- `on` 模式下探测失败**不偷偷直连**：TCP 拨号会明确报错，避免"以为走了代理其实没走"。

## 三、游戏途中开/关代理都能连（三种场景）

| 场景 | 行为 |
|---|---|
| 启动时代理**开着** | TCP 走 `CONNECT`，UDP 走中继；主机通过"按实际观测源地址修正 peer"把连接绑定到代理出口地址 |
| 启动时代理**关着** | 完全直连，与以前一样 |
| 途中**打开**代理 | 数据报立刻改走中继；TCP 流在下次重拨时走 `CONNECT`；主机把该 peer 的地址修正到新的观测源 |
| 途中**关掉**代理 | 中继会话与代理流断开；数据报立刻退回直连，TCP 在下次可靠发送时重新直拨；主机再次修正地址 |

关键点：**主机端不需要任何配合**。我们已有的 `DatagramToken` + `MoveConnection`
（`AllowDatagramAddressIpChange` 默认开、最小变更间隔 1s）本来就是为"NAT/代理导致源地址变化"设计的，
通道切换只是同一机制的一次应用；切换瞬间会丢几个包，由上层重传补上。

局域网/私有地址（`10/8`、`172.16/12`、`192.168/16`、`169.254/16`、`127/8`、组播/广播、IPv6 ULA）
**始终直连**：同网段走代理没有意义，也会丢掉局域网发现。

## 四、日志（排查用）

```
[ScMP] SOCKS5 proxy setting: 127.0.0.1:7890 (auto) (data:/ScMultiplayer.proxy.txt)
[ScMP] SOCKS5 proxy configured: 127.0.0.1:7890 (auto)
[ScMP] SOCKS5 proxy is available, datagram + stream go through it
[ScMP] SOCKS5 proxy is gone, falling back to direct connections
[Comms] TCP stream via SOCKS5: suceru.site via 127.0.0.1:7890
[Comms] SOCKS5 UDP relay 127.0.0.1:xxxxx via 127.0.0.1:7890
[Comms] SOCKS5 UDP relay unavailable: SOCKS5 UDP ASSOCIATE failed: command not supported
```

最后一条说明代理不支持 `UDP ASSOCIATE`（或节点关了 UDP）：此时 TCP 仍可走代理，
但加入房间需要 UDP 也通，请换一个支持 UDP 的节点，或改用系统级 TUN/VPN。

## 五、限制与注意

- **只支持无认证 SOCKS5**；用户/密码认证（RFC 1929）不在范围里。
- 代理与系统级 TUN **同时开着**时，游戏流量会被"代理里的代理"包一层。建议二选一：
  用 TUN 就把 `enabled=off`（本功能不参与），用本功能就关掉 TUN，或在代理里给
  `suceru.site` / 服务器 IP 配 DIRECT 规则。
- 代理必须**同时**能转发 TCP 和 UDP 到 `51459-51522`；HTTP(S) 代理只能 TCP，不适用。
- UDP 报文仍受 `UdpTransmitter` 的 1200 字节上限约束；走中继时按 1190 分片（多出的 10 字节头），
  依旧不超过 IPv6 最小 MTU，不会被 IP 分片。
- 域名的"解析地址 ↔ 域名"登记（`Socks5RouteTable`）来自服务器目录解析：
  用短码/服务器列表进入时会自动带上；直接手填**裸 IP** 时按 IP 路由（`ATYP=1`）。

## 六、实测：Clash / mihomo 常见坑（2026-09-22 真机复现）

**现象**：客户端能上网页、SSH 能连主机，但游戏"加入房间中"一直转圈，房间列表也空。

| 检查 | 结果 |
|---|---|
| 主机监听 | TCP `:::51459` + UDP `0.0.0.0:51459` 都在；主机防火墙三个 profile 全关 |
| 本机 → 主机 `:22` | 通（走 DIRECT） |
| 本机 → 主机 `:51459`（直连/TUN） | 客户端 TCP `Connect` **秒回成功**（TUN 假接受，`local=198.18.0.1`），但**主机侧 `Get-NetTCPConnection -State Established -LocalPort 51459` 看不到这条连接** |
| 本机 → 主机 `:51459`（SOCKS5 CONNECT，域名或 IP 都一样） | 代理回 `05 00` 成功，随后**收不到任何对端字节**（5 秒接收超时），主机侧同样什么都没有 |

**结论**：`uiclick`… 不，是**客户端的 TCP `Connect` 成功在 TUN 模式下不能作为判据**（TUN 会先本地接受）。
唯一权威判据是**主机侧**是否出现来自你公网 IP 的 ESTABLISHED。上面两种路径主机侧都为空 ⇒ 这套 Clash 配置
**没有把 51459 的流量真正送到目的地**（22 端口走 DIRECT 所以正常）。

**修法**（任选其一，改完立即生效）：

```ini
# Clash / mihomo 规则：把服务器排除出代理
DOMAIN-SUFFIX,suceru.site,DIRECT
IP-CIDR,<server-ip>/32,DIRECT
```

- 或者关掉 TUN/虚拟网卡（只保留系统代理），或换一个**明确支持 UDP** 的节点；
- ScMP 需要 **TCP 和 UDP 同时**通到 `51459-51522`：HTTP(S) 代理只能 TCP，不适用；节点必须 `udp: true`。

**验证顺序**（别用"客户端连上了"判断）：

1. `Get-NetTCPConnection -State Established -LocalPort 51459`（在主机上执行）能看到你的公网 IP；
2. 进游戏 → 房间列表出现房间；点加入 → 客户端日志 `JoinGame sent: … server=<真实 IP>:51459`；
3. 主机日志 `Client entered Loading Project` / `World transfer ready`。

## 七、主机应答发现与"真实 IP"链路（排错要点）

- 主机开房后会 `Explorer.StopDiscovery()`（`Explorer discovery paused (room is active)`），但**应答探测的是主机侧 `Server`**
  （`Comms.Drt/Func/Server/Server.cs` 处理 `ClientDiscoveryRequestMessage`），与 Explorer 无关 —— 所以"开房即无法被发现"并不成立；
  真正的前提仍然是**客户端 UDP 能到主机 51459-51522**。
- 客户端侧现在有两条"把域名换成真实 IP"的链路，都基于 DoH（端点写成 IP 字面量，走代理链路的 HTTPS，fake-ip 抢答拦不住，结果缓存 10 分钟）：
  1. Explorer 的探测目标**保持域名**：实测走 SOCKS UDP ASSOCIATE + `ATYP=3` 时房间能被发现，
     而换成本机 DoH 解析出的真实 IP 后，本机代理不放行该 IP 的直连 UDP，反而**探不到**房间（已回退这一改动）；
  2. `ScMultiplayerUpdateLoop.ResolveRealJoinAddress`：加入前若目标仍是 fake-ip，反查域名再换成真实 IP
     （日志：`Join target 198.18.x:51459 is a fake-ip; using <server-ip> resolved from suceru.site`）。

## 八、掉线重连宽限（主机端 Away）与相关设置

| 键 | 默认 | 说明 |
|---|---|---|
| `rejoinGracePeriodSeconds` | `60` | 客户端断线后主机**保留化身**的秒数，`0` = 关闭（断线即按老路径离开）。范围 0–600，可在世界设置 JSON、`multiplayer.settings`（含无头控制台）里读写 |

- 宽限期内：化身原地保留、不接收输入（客户端已断）、不做 AI 移动、不存档不广播离开；
- 重连命中同一条**记录键**（账号 userid，`m_clientRecordKeys`）→ 拆掉挂起的化身并按正常加入流程恢复（属性/位置/背包来自记录），日志 `reusing the held role`；
- 宽限期满 → 走原本的离开流程（存档 + 移除 + 释放角色序号），日志 `Rejoin grace expired`；
- 身份是 `network:<clientId>`（未登录）时不享受宽限，行为与以前一致。
## 九、铁律：任何重建都必须两端同步

`Message.IsProtocolCompatible` 要求 **主机与客户端的 `BuildFingerprint` 完全相等**（`mod 版本 + 协议版本/哈希` 相同还不够）。
只重建一端时，加入会在**任何连接动作之前**被协议闸门静默拒绝，调用方照样打印 `JoinGame sent`，看起来像"点了加入没反应"：

```
WARNING: [ScMP] Refused incompatible room before join: host=build <A>, local=build <B>
INFO: [SuAP] ... [SuPlay] JoinGame sent: <世界> -> 0, server=..., advertisedHost=...
```

- **规矩**：任何一次重建，都必须把**同一个 `.scmod`** 同时放到客户端 `Mods/` 与远端 `C:\SurvivalcraftServer\Mods\`，然后各自重启加载。
- **一条命令做完**：`pwsh -File Mod/ScMultiplayer/Tools/sync-both-ends.ps1`
  （先设 `$env:SCMP_SSH_PASSWORD` 与 `$env:SCMP_ASKPASS`，或用 ssh 密钥；脚本会构建→打包→复制到客户端→上传+`deploy-mod` 到远端→**打印两端的包哈希做核对**）。
- 排查信号：客户端日志出现 `Refused incompatible room before join` 就是这条；先跑上面的脚本再谈网络。