# 断线重连 / 踢出与旧连接接管方案

> 状态：**方案待实施**（2026-11-06 定稿）
> 相关代码：`Modules/TerrainSyncModule.cs`、`Terrain/TerrainSync.cs`、`Net/TerrainNet.cs`、
> `Modules/WorldTransferModule.cs`、`Engine/ClientSessionExit.cs`、`Terrain/RejoinFlagPolicy.cs`

---

## 1. 问题现象（实机）

### 1.1 现象 A：WiFi 瞬断 1 秒就被踢出房间

客户端（红米，局域网 WiFi）在游戏中**不操作**时，偶发 WiFi 短暂中断（手机状态栏出现感叹号，约 1 秒）。
恢复后客户端已经弹出：

> Connection to the host was lost. You have left the room.

并被送回世界列表（`GameManager.Project` 已被 `DisposeProject` 销毁）。

### 1.2 现象 B：重新加入卡在「连接中」，主机却已有该角色

紧接现象 A，玩家从世界列表重新点击加入：

- 客户端：一直停在「连接中」，收不到 `WorldBegin` / `WorldChunk`，永远进不去；
- 主机：**已经把该客户端的角色加进了世界**（能在地图上看到它）。

---

## 2. 根因

### 2.1 客户端：网络瞬断＝立刻销毁世界，不尝试重连

`Modules/TerrainSyncModule.cs`（客户端主 tick）：

```csharp
if (m_client != null && !m_client.IsAlive)
{
    ...拆连接、保存 m_lastHostSessionId / m_clientChangeSequence...
    // 2026-10-26 产品口径：主机掉线 / 重启 → 客户端退出联机房间
    if (m_clientExit != null && m_clientExit.TryExitWorldOnHostLost(deadReason))
    {
        m_wantClient = false;
        LogWarning("...→ 退出联机房间回到世界列表（不自动重连）");
        return;
    }
    ...
}
```

该策略**无法区分「主机没了」和「我这边网络抖了一下」**——两者在代码里都是「连接 `IsAlive == false`」。
于是 1 秒的 WiFi 瞬断也会走 `ClientSessionExit.DrainPendingExit()`：

```
SaveProject → DisposeProject() → SwitchScreen("Play") → 弹窗
```

`Project` 一旦销毁，`RejoinFlagPolicy` 的 `projectLoaded` 条件永久为 false，
**这次会话再也无法以「断线重连」身份回去**。

### 2.2 客户端：WiFi 断开会立刻让 socket 死掉，不是超时问题

`Net/TerrainNet.cs` 客户端读循环只认这一种失败：

```csharp
if (!await ReadExactAsync(m_stream, header, TerrainFrame.HeaderBytes).ConfigureAwait(false))
{
    SetState(ChannelState.Failed, "对端关闭"); return;
}
```

`ReadExactAsync` 内部 `NetworkStream.ReadAsync` 没有任何读超时。Android 在 WiFi 接口 down 时
会**立刻**让关联 socket 抛 `SocketException`（`ENETDOWN` / `ENETUNREACH`）或返回 0，
因此 `ReadAsync` 立即失败 —— **1 秒瞬断足以判死**，不需要等任何超时。

⚠️ 结论：**任何超时参数（TCP KeepAlive / 重传时间 / 静默超时）都救不了这个路径**，
因为 socket 是被内核销毁的，不是「没等到数据」。

### 2.3 主机：旧连接 30 秒内仍被认为在线，且不释放世界传输

`Terrain/TerrainSync.cs`：

```csharp
private const long PeerSilenceTimeoutMs = 30000;   // 主机判客户端静默的阈值
...
bool silent = state.InboundFrames >= 64 && state.LastHeardMs > 0 &&
    nowMs - state.LastHeardMs > PeerSilenceTimeoutMs;
if (!CanStillReachPeer(state) || silent)
{
    // 清欠更新标记、从 m_clients 摘掉、通知上层清联机角色
}
```

客户端 socket 已死、主机侧 Peer 读循环阻塞在 `ReadAsync`（无超时）时：

- 主机要等 **30 秒**才通过 `silent` 发现客户端不在了；
- 更关键：`silent` 分支**只清理 `TerrainSyncHost.m_clients`**，**不触发 `ClientLeft` 事件**；
  而 `WorldTransferModule` 等 20+ 个模块的状态**全部依赖 `ClientLeft` 清理**。

`Modules/WorldTransferModule.cs` 的发送泵：

```csharp
private void PumpHostSend(long nowMs)
{
    if (m_activeSendClientId > 0)
    {
        SendState active = FindSend(m_activeSendClientId);
        if (active == null || active.Aborted) { ...; m_activeSendClientId = 0; }
        else
        {
            ContinueSend(active, nowMs);
            if (m_activeSendClientId == active.ClientId) return;   // 这一条还在发
        }
    }
    TryStartNextSend(nowMs);
}
```

旧 clientId 的传输卡在 `ContinueSend`，只有 `SendStallMs` 超时才会 `FailSend`。
在它失败之前，**新 clientId 的整图传输永远排在队列后面轮不到**。

### 2.4 主机：同一玩家身份允许两条连接并存

`Terrain/TerrainSync.cs` `HandleHello` 没有任何「同 `Identity` 已有旧连接」的处理：

- 旧连接（clientId=N，已死但未察觉）留在 `m_clients`；
- 新连接（clientId=M）以「首次加入」身份正常握手；
- 联想机角色模块按 `Identity` 找记录 → 旧记录仍在 → **直接复用并把角色放进世界**（现象 B 的「主机已有角色」）；
- 但世界传输被旧传输阻塞 → 客户端拿不到整图（现象 B 的「一直连接中」）。

---

## 3. 目标口径（用户 2026-11-06 定）

| 编号 | 口径 |
|---|---|
| **G1** | 网络瞬断时**不退出世界**，先尝试重连；**重连窗口 30 秒**，超时才退出 |
| **G2** | 重连期间**冻结世界模拟**（对齐游戏内打开统计/角色/设置界面时整个世界停下来的表现） |
| **G3** | 「是否断线重连」的判定改用**断线不退出世界**这一事实（`Project` 还在 ⇒ 重连；`Project` 没了 ⇒ 完全重新加入、主机下发整图） |
| **G4** | 主机端在客户端未接收同步消息期间，**缓冲地形修改**（复用现有的改动历史 / 欠更新标记，不新建机制） |
| **G5** | 主机侧**必须踢掉旧连接**，否则新连接排不上队 |
| **G6** | 主机**主动踢出**客户端时，下发明确的「你被踢出」标志，客户端据此**直接退出世界**，不进入重连窗口 |
| **G7** | **附属两条 TCP 连接（电路 / 地形）必须一起清理** |
| **G8** | **联机状态下禁止世界冻结**：ESC/返回、游戏统计、编辑玩家、设置、帮助、生物图鉴、合成百科等界面都不得暂停世界；主机补推进 `GameManager.UpdateProject()` |
| **G9** | `Hello` 新增 `join_intent`（0=未声明 / 1=全新加入 / 2=掉线重连）；主机在 `1` 与 `2` 时都必须**先清理该身份旧连接**再继续。**UI 不做任何标注** |

---

## 4. 现有可复用机制（不新建）

| 需求 | 现有机制 | 位置 |
|---|---|---|
| 缓冲地形改动（G4） | 改动历史 `m_changeJournal`（60 000 ms / 8192 条）+ 欠更新标记 `m_pendingClients` | `Terrain/TerrainSync.cs` |
| 重连追帧 | `Hello.LastAppliedSequence` + `TryAcceptRejoinLocked` + `CatchUp` 三方握手 | `Terrain/TerrainSync.cs` |
| 重连判定 | `RejoinFlagPolicy.ShouldSendAlreadyHasWorld`（7 个条件） | `Terrain/RejoinFlagPolicy.cs` |
| 世界下载期间暂停非地形广播 | `SetClientDownloading` / `IsClientDownloading` + `WorldDownloadPauseFilter` | `Modules/TerrainSyncModule.cs` |
| 世界模拟冻结 | `SubsystemTime.GameTimeFactor = 0`（游戏菜单打开时引擎自己就这么做） | `Survivalcraft/Game/SubsystemTime.cs:113` |

**结论：G4 不需要新机制**——`m_changeJournal` 保留 60 秒 > 重连窗口 30 秒，天然覆盖。

### 4.1 世界模拟冻结的既有先例

引擎在「所有玩家都打开了游戏菜单对话框」时自己就把世界停下来
（`SubsystemTime.NextFrame`）：

```csharp
bool flag = true;
foreach (ComponentPlayer componentPlayer2 in m_subsystemPlayers.ComponentPlayers)
{
    if (!componentPlayer2.ComponentGui.IsGameMenuDialogVisible()) { flag = false; break; }
}
if (flag) GameTimeFactor = 0f;
else if (GameTimeFactor == 0f) GameTimeFactor = 1f;
```

mod 侧已有通过反射改这个字段的先例（`Engine/CircuitWorldEngine.cs:111`
`m_parentField.ModifyParentField(time, "GameTimeFactor", ...)`），
重连冻结沿用同一手法。

⚠️ 注意：`GameTimeFactor` 由 `SubsystemTime.NextFrame` 每帧根据「菜单是否可见」覆写。
所以要冻结必须**每帧重设**（与 `CircuitWorldEngine.NormalizeClientSleepTime` 同款做法），
不能只设一次。

---

### 4.2 `m_changeJournal`（地形改动缓冲）详解

**它是什么**：主机侧的**地形改动历史队列**（`Terrain/TerrainSync.cs:2066`）。
主机每广播一格权威地形改动，就记一条 `ChangeJournalEntry`（改动序号 + 格坐标 + 格值）。
序号 `m_changeSequence` 从 1 递增，全房间唯一。

**它解决什么**：客户端断线重连时，`Hello` 里带 `LastAppliedSequence`（「我上次应用到第几号」）。
主机据此判断：

```
LastAppliedSequence 落在 [oldest, head] 内 → 接受重连 → CatchUp 逐条补发缺口
超出最旧保留序号 → 拒绝重连（明确日志）→ 要求整图重下
```

**它为什么会被裁剪**（`TrimChangeJournalLocked`，`Terrain/TerrainSync.cs:4173-4196`）：

```csharp
bool tooOld  = nowMs - oldest.CreatedMs > m_options.CatchUpRetentionMs;              // 60 000 ms
bool tooMany = m_changeJournal.Count > Math.Max(1, m_options.CatchUpMaxJournalEntries); // 8192 条
if (!tooOld && !tooMany) break;
m_changeJournal.Dequeue();   // 从最旧的开始丢
```

| 参数 | 默认值 | 含义 |
|---|---|---|
| `CatchUpRetentionMs` | **60 000 ms** | 超过 60 秒的改动被丢弃（**按真实时间计，与帧率 / 卡顿无关**） |
| `CatchUpMaxJournalEntries` | **8192 条** | 超过 8192 条时丢最旧的 |

实机日志形如：

```
改动历史裁剪：本次裁掉 1 条（累计 13514 条 · 当前保留 1071 条 · 上限 8192 条 / 60000ms）
→ 早于最旧保留序号的重连会明确要求整图重下
```

「**当前保留 1071 条**」远低于 8192 ⇒ 触发的必定是 **60 秒时间裁剪**，不是条数上限。

**对重连的含义**：

- 30 秒重连窗口 < 60 秒保留期 ⇒ **窗口内重连成功，追帧一定够用**；
- **不需要调整 `CatchUpRetentionMs`**（用户 2026-11-06 确认）：调大只会让主机长期占更多内存；
- **但客户端必须在窗口耗尽时立即停手**（见 5.1.1）—— 拖到 50 秒再连，
  主机可能已经裁掉了缺口，连上也会被明确拒绝，玩家白等。

## 5. 实施方案

### 5.1 客户端：瞬断不退出世界，30 秒重连窗口（G1 + G2 + G3）

**改动点**：`Modules/TerrainSyncModule.cs` 的「客户端连接已死」分支。

```
m_client != null && !m_client.IsAlive
  │
  ├─ 保存 m_lastHostSessionId / m_clientChangeSequence（现有逻辑不变）
  ├─ 拆主连接 + 附属两条连接（见 5.5）
  │
  ├─ ★ 新增判定：是否值得进入重连窗口
  │    worthReconnect = Project 仍加载 && 当前屏是 GameScreen
  │                     && 不在死亡复活界面 && worldLoaded == true
  │    （两种边界都不重连：正在死亡复活、正在世界加载中 —— 状态不完整，
  │      重连后容易错乱；直接退出世界。用户 2026-11-06 确认）
  │
  ├─ worthReconnect == true
  │    → 不调 m_clientExit；保持 m_wantClient = true
  │    → 初始化 m_reconnectWindowMs = now + 30000
  │    → 每 5 秒重试一次 StartClientOnce（现有重连分支复用）
  │    → **不冻结时钟、不拦输入**（见 5.2）
  │    → 重连成功：清除窗口，alreadyHasWorld 由 RejoinFlagPolicy 判 true（Project 还在 + 会话一致）
  │    → 窗口耗尽：才调 TryExitWorldOnHostLost（回到现有行为）
  │
  └─ worthReconnect == false
       → 保持现有行为（立刻退出世界）
```

**新增可配置项**（`Engine/SyncConfig.cs`）：

| 键 | 默认 | 含义 |
|---|---|---|
| `reconnectwindowms` | `30000` | 断线后保持世界并重连的窗口（0 = 关闭该能力，退回立刻退出） |
| `reconnectintervalms` | `5000` | 重连尝试间隔 |

**为什么不复用 `m_nextReconnectMs` 的 5 秒常量**：加入流程（主机晚建房）与断线重连是两条语义不同的路径，
前者本来就是无限重试，后者必须有上限。共用一个变量会让「加入流程」被 30 秒窗口误砍。

#### 5.1.1 硬性要求：窗口耗尽必须**立即**停止重连

`m_changeJournal` 的保留期是 **60 秒**（`TerrainSyncOptions.CatchUpRetentionMs`，见 §4）。
重连窗口 30 秒 < 60 秒，正常情况余量充足。

**但如果客户端拖到 50 秒还在重连（超过窗口却没收手），主机侧很可能已经裁掉了它需要的改动**
（实机日志见过「当前保留 1071 条」这种远低于 8192 上限的状态，说明触发的是 60 秒时间裁剪）。
那时即使连上也会被主机明确拒绝（`rejoin rejected`），玩家白等。

所以实现上必须保证：

- 窗口到期的那一帧就 `m_wantClient = false` 并走退出世界，**不留任何「再试一次」的余地**；
- 重试逻辑不得因为「这一次刚好快连上了」而延长窗口；
- 窗口判定用**单调真实时间**，不受游戏时钟 / 帧率影响。

**不需要调整 `CatchUpRetentionMs`**（用户 2026-11-06 确认）：30 秒窗口配 60 秒保留期已经够用，
把它调大只会让主机的改动历史长期占用更多内存。

### 5.2 客户端：重连期间**不冻结世界、不拦输入**（G2 定稿）

> **2026-11-06 两次修正后的定稿结论**：
> ① 原方案打算用 `GameTimeFactor = 0` 冻结重连中的客户端 —— 与「联机时禁止世界冻结」
> （5.8 节）直接冲突，且会重现「主机进菜单 → 全员冻结」的同类缺陷。**已否决。**
> ② 修正后曾打算「只拦输入」 —— 经核实**没有必要**（见 5.2.1）。**已否决。**

#### 5.2.1 为什么不需要拦输入（代码依据）

**山形改动（挖 / 放）**：客户端引擎确实会在本地写一次，但那个写入不会生效：

`Engine/SuSubsystemTerrain.cs:789-795`（客户端分支）：

```csharp
if (bridge.ClientReplica)
{
    FilterUnsyncedFurnitureEdits(m_capture);
    bridge.CaptureClientEdits(m_capture);   // ← 捕获成 CellEditRequest，不是本地权威生效
}
else
{
    bridge.PublishHostCells(m_capture, ...);  // 主机：权威广播
}
```

捕获后经 `TerrainBridge.CaptureClientEdits` → `ClientAuthorityModule.LocalEditCaptured`
→ 打包成 `CellEditRequest` 发给主机**审批**。

断线期间的结局由 `Modules/ClientAuthorityModule.cs:371-410` 的 `PumpClientEdits` 决定：

```csharp
if (m_transport == null || !m_transport.TrySend(MessageType.CellEditRequest, ...))
{
    m_editSendFailures++;                   // 发不出去：只计数
}
...
IReadOnlyList<ClientEditRequest> expired = m_tracker.CollectExpired(nowMs);
for (int i = 0; i < expired.Count; i++)
{
    // 超时回滚：请求主机回传这一整块（本端按预测内容重算摘要 → 主机不一致就回传权威值）
    bool requested = m_reverify?.RequestReverification(request.X >> 4, request.Z >> 4);
    if (requested)
    {
        m_editRollbacks++;
        NoteProblem("编辑请求超时 ... → 已请求主机回传该块（回到主机最后的值）");
    }
}
```

**所以断线期间挖方块的完整链路是**：
本地写一次（视觉上看到消失）→ 编辑请求发不出去 → **超时后自动请求主机回传该块**
→ **回滚到主机权威值** → 重连后主机下发权威地形再覆盖一次。

**伤害 / 攻击动物**：客户端**根本不执行** —— `ClientReplicaSubsystems.cs` 已在每个
会写世界的引擎回调入口拦截（`AllowSimulation(ReplicaMutationSource)`），
所有伤害都由主机权威计算后下发。断线期间客户端连伤害包都发不出去。

**结论**：现有架构已保证「客户端改不了本地世界」，**不需要额外拦输入**。

#### 5.2.2 为什么不拦输入（体验依据）

拦输入会让玩家立刻感到「所有操作都没反应」——比「挖的方块过一会又回来」明显得多。
用户明确要求：**不要这个拦截输入，避免客户端感觉明显**（2026-11-06）。

所以重连窗口期间：玩家**正常操作**，本地表现照旧，改动会在重连后被主机权威值覆盖。
最坏体验是「刚挖的方块又回来了」，可接受；而不是「整个游戏僵住」。

#### 5.2.3 重连期间的唯一 UI 反馈（**推迟实现**）

重连窗口期间应在 HUD 上显示 `Reconnecting...`，否则玩家不知道正在重连
（画面不会冻结，但会感觉「操作好像没效果」）。

> **实施状态：推迟。** 用户 2026-11-06 决定：后续会增加一个**网络状态显示**，
> 届时在那里一并加上重连状态。**本期方案不实现此项。**

#### 5.2.4 明确不做

- 不改 `GameTimeFactor`（见 5.8：联机时禁止冻结）
- 不改 `FixedTimeStep` / `UpdatesPerFrame`（那是睡眠加速语义）
- 不拦 `ComponentInput`（见 5.2.1 / 5.2.2）

### 5.3 主机：同连接身份接管（G5）—— **复用已有实现，扩展清理范围**

#### 5.3.1 已有实现（不要重写）

`Modules/PlayerAvatarModule.cs:540-560` 已经处理了「同身份新连接出现」这件事：

```csharp
if (m_recordKeyOwner.TryGetValue(recordKey, out PlayerKey owner) && owner != key)
{
    // 2026-10-26 同身份顶替：同一个 UserId 只可能是同一个人。新连接出现就意味着
    // 旧连接已经失效（闪退 / 掉线但主机还没判死）。旧实现直接拒绝新角色，实机结果是
    // 旧角色无人摘、新角色建不出来（幽灵角色 + 进不了房）。这里先摘旧角色、释放记录键，
    // 再让新连接正常建角色（绝不静默合并两个角色）。
    Warn("同身份顶替：clientId=" + clientId + " 的身份 ... 仍归旧连接 clientId=" +
        owner.ClientId + "（旧连接未被判死 / 可能闪退）→ 立刻摘掉旧角色、释放记录键...");
    m_deferredLeaves.Remove(owner.ClientId);
    m_identityTakeovers++;
    RemoveClientAvatarsNow(owner.ClientId, "同身份顶替（新连接 clientId=" + clientId + "）", ...);
}
```

**它只清理了「角色」**（`m_recordKeyOwner` / 化身实体），
**没有清理**：世界传输（`WorldTransferModule.m_activeSendClientId` / `m_sends` / `m_sendQueue`）、
`TerrainSyncHost.m_clients` 里的旧连接、电路 / 地形两条附属 Peer。

#### 5.3.2 需要补的范围（G5 的实际工作）

**改动点**：`Terrain/TerrainSync.cs` `HandleHello`（兼容性检查之后、审批之前），
与 `PlayerAvatarModule` 的顶替逻辑**协同**而不是重复实现。

```
新连接 state.Id = M，hello.Identity = I
  → 扫描 m_clients：若存在 other.Id != M 且 other.Identity == I（不论是否仍"可达"）
      → ForceKickPeerLocked(other, reason: "同一身份在新连接上重新加入")
           · 中止它的世界传输（WorldTransferModule.CancelSendFor(other.Id)）
           · 经传输层 Fail 掉主连接 + 电路 / 地形两条附属连接（见 5.5）
           · 主动派发 ClientLeft(other.Id, reason)，让全部模块清理
             （PlayerAvatarModule 的顶替逻辑也会因此走一次，与既有行为一致）
      → 会话记录（PlayerSessionRecord）按 §5.9 的 join_intent 决定：
           · 掉线重连 → 复用记录（保留 ReportedLoaded / Verdicts / MemoryChunks，
             只把 CurrentClientId 指向新连接）
           · 全新加入 → 丢弃旧记录，按首次加入重建
```

**关键前提**：`ClientLeft` 必须真的发出去。现状的 `silent` 分支（2.3 节）绕过了它，见 5.4。
`PlayerAvatarModule` 的 `m_identityTakeovers` 计数仍然有效，可用于验证顶替确实发生。

### 5.4 主机：静默判死也必须触发 ClientLeft（G5 配套）

**改动点**：`Terrain/TerrainSync.cs` 的静默清理分支。

现状只做「清欠更新标记 + 从 `m_clients` 摘掉 + 通知上层清角色」，
**必须补上对 `ClientLeft` 的派发**，否则 `WorldTransferModule` / `PlayerAvatarModule` /
`EntityReplicationModule` / `CircuitSyncModule` 等 20+ 个模块的状态全部残留。

方案：静默分支收集到 `lost` 列表后，**统一经与传输层 `Fail` 相同的出口**派发
`ClientLeft`（避免两条清理路径行为不一致）。

### 5.5 主机 / 客户端：附属两条连接一起清理（G7）

现状：

- 客户端有 `DisposeCircuitClient(...)` / `DisposeTerrainClient(...)`，但断线分支只调了前者；
- 主机侧 `Peer` 是按连接独立建的，主连接死掉时**没有连带清理同一 clientId 的
  `ChannelKind.Circuit` / `ChannelKind.Terrain` 两条 Peer**。

**方案**：

- **客户端**：断线分支统一调用一个新方法 `DisposeAllSecondaryClients(reason)`，
  固定清理电路 + 地形两条（现在只清电路）；
- **主机**：`Fail(peer, reason)` 时，若该 peer 是主连接，
  连带 `Fail` 同一 `PrimaryClientId` 的 `IsCircuit` / `IsTerrain` peers，
  保证 `m_terrainPeers` / `m_circuitPeers` 不会留下孤儿。

### 5.6 主机踢出客户端：明确的「你被踢」标志（G6）

**协议**：`HelloAck` 新增字段（append-only，复用现有 `rejected` 语义的独立扩展）

```protobuf
// protocol.proto HelloAck
// true => 主机主动把这条连接/这个身份踢出房间（不是审批拒绝、不是重连拒绝）。
// 客户端必须**直接退出世界**，不得进入 30 秒重连窗口。
// Source: doc/reconnect-and-kick-plan.md G6
bool kicked = 27;
// 踢出原因（写日志；UI 只显示英文固定文案）。
string kick_reason = 28;
```

同时新增一条主机 → 客户端消息 `MessageType.Kicked = <下一个可用 id>`，
用于**已连接状态下**被踢（`HelloAck` 只能覆盖握手期）：

```protobuf
// proto: 新增 message Kicked
message Kicked {
  // Host-defined reason code; the client logs it and leaves the world immediately.
  string reason = 1;
  // true => 同一身份在别处重新加入（客户端直接退出，不重连）；
  // false => 主机管理操作（Ban / 手动踢出）。
  bool superseded = 2;
}
```

**客户端处理**：收到 `Kicked`（或 `HelloAck.kicked == true`）→

```
m_wantClient = false
m_reconnectWindow = 0            // 不进重连窗口
DisposeAllSecondaryClients("kicked")
m_clientExit.TryExitWorldOnHostLost(kick_reason)   // 直接退出世界
UI 文案：Connection to the host was lost. You have left the room.
```

⚠️ **协议兼容**：新增字段 / 新消息类型对旧端是「未知字段忽略 / 未知类型丢弃」，
不会让旧客户端崩，但**旧客户端不会遵守「不重连」语义**——它会进重连窗口并且永远连不上
（因为被踢）。此时退化为：30 秒后退出世界。这是可接受的降级，且日志会写明。
`MultiplayerModCompatibility` 的协议 hash 会因 proto 变更而改变，**新旧版本无法互相加入**
（现有机制，不是本次引入）。

---

### 5.8 联机时禁止世界冻结（G8，2026-11-06 新增）

#### 5.8.1 问题

联机状态下，任何一端打开下列界面都会让**整个世界停止模拟**，另一端看到电路、动物、
天气全部静止：

| 入口 | 引擎行为 | 结果 |
|---|---|---|
| 顶部返回按钮 / 按 ESC | 弹出 `GameMenuDialog` | 模拟停止 |
| 游戏统计 | `GameMenuDialog` 内的统计面板 | 模拟停止 |
| **Edit Players**（编辑玩家 / 改名 / 换皮肤） | `SwitchScreen("Players")` | 世界**完全**停止 |
| **Settings**（设置） | `SwitchScreen("Settings")` | 世界**完全**停止 |
| **Help**（帮助） | `SwitchScreen("Help")` | 世界**完全**停止 |
| 生物图鉴 / 合成百科 | `GameMenuDialog` 的 More Actions | 模拟停止 |
| Keyboard Controls / Gamepad Controls | 子对话框 | 模拟停止 |

主机进设置 → 所有客户端的世界一起停（用户体验被完全打断）。
客户端进设置 → 它自己的世界不推进（但它本来就不模拟，见 5.8.4）。

#### 5.8.2 为什么引擎会停 —— 两层完全不同的机制

**必须分清这两层，因为修法不同。**

#### 第一层：`GameMenuDialog` 让 `GameTimeFactor` 变 0

`Survivalcraft/Game/SubsystemTime.cs:101-118`（在 `NextFrame()` 内）：

```csharp
bool flag = true;
foreach (ComponentPlayer componentPlayer2 in m_subsystemPlayers.ComponentPlayers)
{
    if (!componentPlayer2.ComponentGui.IsGameMenuDialogVisible()) { flag = false; break; }
}
if (flag) GameTimeFactor = 0f;                       // ← 所有人都开着菜单 → 时钟停
else if (GameTimeFactor == 0f) GameTimeFactor = 1f;
```

`IsGameMenuDialogVisible()`（`ComponentGui.cs:198`）的判据是
「`DialogsManager.Dialogs` 里有 `GameMenuDialog` 且它是本玩家的」。

**这一层 `NextFrame()` 仍在每帧执行**（`GameScreen.Update` 还在跑），
所以它每帧重算并覆写 `GameTimeFactor`。

#### 第二层：`SwitchScreen` 让 `GameScreen.Update` 整个不再被调用

调用链（`Program.cs:168`）：

```
Program 主循环
  → ScreensManager.Update()
      → Widget.UpdateWidgetsHierarchy(RootWidget)
          → GameScreen.Update()              ← 只有"当前屏"才会被驱动
              → GameManager.UpdateProject()
                  → SubsystemUpdate.Update()
                      → SubsystemTime.NextFrame()   ← 只有这里才会设 GameTimeFactor
```

`ScreensManager.SwitchScreen()`（`ScreensManager.cs:79-83`）：

```csharp
if (CurrentScreen != null)
{
    RootWidget.IsUpdateEnabled = false;   // 旧屏的 widget 层级停止更新
    CurrentScreen.Input.Clear();
}
PreviousScreen = CurrentScreen;
CurrentScreen = screen;                   // Settings / Help / Players
```

`CurrentScreen` 变成新屏后，`GameScreen.Update()` **根本不会被调用**
→ `GameManager.UpdateProject()` 不跑 → `SubsystemUpdate.Update()` 不跑
→ **`NextFrame()` 不跑** → 世界彻底停止。

⚠️ **所以「改 `GameTimeFactor`」只能解决第一层，对第二层无效。**

#### 5.8.3 旧实现参考（`Mod/ScMultiplayer`）

旧 mod 已经处理过这两个问题，本次**照旧实现迁移**（符合"先照旧实现来"规范）：

**① 第一层：每帧把时钟拉回 1**

```csharp
// Mod/ScMultiplayer/Modules/Player/ScMultiplayerHealthWorldControlHandlers.cs:488-498
// Source: SubsystemTime.cs:SubsystemTime.NextFrame
// Split-screen keeps running while at least one player is outside GameMenuDialog. A
// network room has players on other devices, so local dialogs must never pause its clock.
private static void MaintainMultiplayerTimeFlow(Project project)
{
    if (project == null || client?.IsConnected != true) return;
    SubsystemTime subsystemTime = project.FindSubsystem<SubsystemTime>(false);
    if (subsystemTime == null) return;
    ModManager.ModParentField.ModifyParentField(
        subsystemTime, "m_gameTimeFactor", 1f, typeof(SubsystemTime));
}
```

**② 第二层：主机补推进权威世界**

```csharp
// Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerRuntimePhases.cs:117-122
MaintainMultiplayerTimeFlow(project);
// Settings and help screens can remove GameScreen from the update hierarchy. A host
// still advances the authoritative Project exactly once per frame.
if (IsHost && client?.IsConnected == true &&
    m_lastProjectSimulationFrameIndex != Time.FrameIndex)
    GameManager.UpdateProject();
```

#### 5.8.4 反射方式的选择：`BindFieldRef`（每帧零开销）

`SubsystemTime.GameTimeFactor` 的 setter 是 `private`：

```csharp
public float GameTimeFactor
{
    get { return m_gameTimeFactor; }
    private set { m_gameTimeFactor = MathUtils.Clamp(value, 0f, 1024f); }   // ← private
}
```

SuAPI 提供三个可用入口，成本差异决定性：

| API | 每帧成本 | SuAPI 文档定位 |
|---|---|---|
| `ModifyParentField(obj, "m_gameTimeFactor", 1f, typeof(SubsystemTime))` | ConcurrentDictionary 查找 + 装箱（`1f` → `object`） | 明确写 **low-frequency access** |
| `PropertyInfo.SetValue(...)`（改属性） | 同上 + private setter 反射调用 | — |
| **`BindFieldRef<SubsystemTime, float>("m_gameTimeFactor")`** | **零查找、零装箱**，返回 `ref float` | 明确写 **preferred API for repeated per-frame access** |

`IModParentField.BindFieldRef` 的 XML 注释原文：

> Binds a private instance field once and returns a ref delegate to its actual storage.
> **This is the preferred API for repeated per-frame access.**
> The returned delegate performs **no lookup or boxing**.

**决定：用私有字段 `m_gameTimeFactor` + `BindFieldRef`。** 理由：

1. `BindFieldRef` 返回 `ref TValue`，**属性没有存储位置，拿不到 `ref`** —— 这是硬约束；
2. 每帧执行（`NextFrame` 每帧覆写，必须每帧拉回），必须零开销；
3. 旧 mod 改的就是这个字段，行为已验证；
4. mod 内已有同类先例（`Engine/PickableReplicaEngine.cs:60` 的 `m_inputRef`）。

绑定形态（模块加载时一次）：

```csharp
private ModFieldRef<SubsystemTime, float> m_gameTimeFactorRef;

// Load 期一次
m_gameTimeFactorRef = m_parentField.BindFieldRef<SubsystemTime, float>("m_gameTimeFactor");

// 每帧（零反射开销）
if (m_gameTimeFactorRef != null)
{
    ref float factor = ref m_gameTimeFactorRef(subsystemTime);
    if (factor <= 0f) factor = 1f;
}
```

#### 5.8.5 时序（已用源码确认）

**`Frame.Update` 在 `ScreensManager.Update` 之后触发**（`Program.cs:166-173`）：

```csharp
PerformanceManager.Update();
MotdManager.Update();
MusicManager.Update();
ScreensManager.Update();                                  // ← 这里驱动 GameScreen.Update → NextFrame()
DialogsManager.Update();
//mod ModEventBus Frame.Update for widget scanner /*mod*/
ModManager.ModEventBus.TriggerEvent("Frame.Update", null); // ← mod 的 tick 在这里
```

**结论：mod 的 `Frame.Update` 回调执行时，本帧的 `NextFrame()` 已经跑完。**
所以在 `Frame.Update` 里把 `m_gameTimeFactor` 拉回 1 是**安全且及时**的
（引擎刚写的 0 会被同帧覆盖，本帧后续的 `ScreensManager.Draw` 与下一帧模拟都用 1）。

SuMP 的 `Frame.Update` 订阅优先级是 `EventPriority.LOWEST`（`Plug/SuMultiplayerMod.cs:1304`），
在其它 mod 的 `Frame.Update` 回调之后执行 —— 这反而是好事：即使别的 mod 也在
`Frame.Update` 里动 `GameTimeFactor`，我们最后一次写入仍然生效。

#### 5.8.6 客户端进设置会不会导致「拒绝主机修改 / 世界不同步」

**不会。** 逐项核实：

| 环节 | 客户端进 Settings 后 | 影响 |
|---|---|---|
| `GameScreen.Update` | ❌ 不跑 → `SubsystemUpdate` 不跑 | 客户端本地世界不推进 |
| **mod 的 `Frame.Update` tick** | ✅ **照常跑**（不经过 `SubsystemUpdate`） | 地形 / 实体 / 位置同步继续收发与应用 |
| 地形写入 `TerrainBridge.ApplyCells` | ✅ 照常 | 主机 `CellChange` 继续写入客户端 `Terrain.Cells` |
| 引擎本地模拟 | ❌ 不跑 | **这正是期望行为**：客户端本来就不模拟（`m_bridge.ClientReplica = true` 拦掉所有会写地形的引擎回调） |
| `TerrainUpdater`（光照/顶点重算） | ❌ 不跑 | 期间收到的格改动已写进 `Cells`，只是视觉未重算；退出设置后 `GameScreen.Update` 恢复即补算 |

**关键前提**：客户端**从来没有在跑本地地形模拟**，它的地形完全来自主机下发。
所以「进设置时本地不推进」不会产生任何分歧——而这段时间主机的下发仍在应用。

**因此：客户端进 Settings / Help / Players 不需要任何补偿，也不会不同步。**

#### 5.8.7 实施方案

| 项 | 位置 | 做法 |
|---|---|---|
| ① 每帧把时钟拉回 1 | `Modules/TerrainSyncModule.cs` 的 `Tick`（或组合根 `Frame.Update` 里模块 tick 之后） | `BindFieldRef<SubsystemTime,float>` 绑定一次；每帧 `if (factor <= 0f) factor = 1f` |
| ② 生效条件 | 同上 | **联机会话活跃**（主机建房 或 客户端已连）且 `Project != null`。单机不干预 |
| ③ 主机补推进 | 组合根 `Frame.Update`（模块 tick 之后） | `if (IsHostingRoom && Project != null && !ScreensManager.IsAnimating && !(CurrentScreen is GameScreen)) GameManager.UpdateProject();` |
| ④ 客户端补推进 | — | **不做**（见 5.8.6） |

**③ 的两个前置条件是实机踩坑换来的，缺一即出 bug（2026-11-06 实测）：**

- **`!(CurrentScreen is GameScreen)`**：`GameManager.UpdateProject()` 唯一的调用者就是
  `GameScreen.Update`（`GameScreen.cs:58`）。`CurrentScreen` 是 `GameScreen` 时引擎本帧已经推进过，
  再补一次就是**每帧推进两遍** → ①游戏速度翻倍 ②`SubsystemTerrain.Update` / `TerrainUpdater`
  跑两遍，刚挖掉的方块被旧状态回填（"挖了又还原"）。
  ⚠️ **不能用 `Time.FrameIndex` 去重**：它在 `Time.AfterFrame()` 里才自增（`Engine/Engine/Time.cs:142`），
  而 `Frame.Update` 跑在它之前 —— 同一帧内 `FrameIndex` 不变，这种守卫永远拦不住重复推进。
- **`!ScreensManager.IsAnimating`**：`SwitchScreen` 会先置 `RootWidget.IsUpdateEnabled = false`，
  再由 `UpdateAnimation` 在动画过半时插入新屏并恢复。这期间 widget 更新整段被跳过，
  `ClickableWidget.Update()` 不跑 ⇒ 按钮的 `IsClicked` 保持 true。此时若旁路调用
  `GameManager.UpdateProject()`，`ComponentGui.Update` 会被重新执行并再次读到那个陈旧的
  `IsClicked=true`，又调一次 `SwitchScreen` 把 `m_animationData.Factor` 重置为 0
  ⇒ 动画永远到不了 0.5 ⇒ **界面不显示且无法操作**（实机：点 More→Help 后卡死，
  `animating` 永久 true）。`IsAnimating`（`ScreensManager.cs:44`）就是"正在转场"的权威信号。

**为什么 ② 用「`GameTimeFactor <= 0f` 才写」而不是无条件写 1**：

睡眠加速（`FixedTimeStep = 0.05f` + `UpdatesPerFrame = 20`）走的是另一条路径，
`NextFrame` 会把 `GameTimeFactor` 设为 `frameDuration/fixedStep`（> 1），不是 0。
条件写 `<= 0f` 不会碰睡眠加速，也不会碰其它 mod 设的 > 1 的值。

⚠️ 需要更新的旧结论：第 5.2 节原先打算用 `GameTimeFactor = 0` 冻结重连中的客户端，
已改为「只拦输入」（5.2 节已重写）。

---

### 5.9 加入意图：区分「全新加入」与「掉线重连」（G9，2026-11-06 新增）

#### 5.9.1 为什么需要显式意图

现状 `Hello.already_has_world`（protocol.proto 字段 7）是**布尔值**，由
`RejoinFlagPolicy` 在客户端按 7 个条件推断。它能表达「我还在这个世界里」，
但**表达不了下面这个关键场景**：

> 客户端已经退出世界（`Project` 已销毁，`already_has_world = false`），
> 但主机侧它那条**旧连接还挂着**（30 秒静默未到，传输 / 角色 / 会话都在）。

此时主机收到新连接，只知道「他要全新加入」，**不知道「这是同一个人的重新加入、
必须先清掉他的旧连接」** —— 于是旧连接的 `m_activeSendClientId` 继续阻塞世界传输，
新连接拿不到整图（现象 B 的直接成因之一）。

#### 5.9.2 协议

`Hello` 新增字段（append-only；**不动**已有字段 7 的语义）：

```protobuf
// protocol.proto — Hello
// 客户端加入意图，让主机区分"全新加入"与"掉线重连"，避免只靠布尔 already_has_world
// 推断出错误结论（客户端已退出世界但主机侧旧连接尚未判死时，两者语义完全不同）。
//   0 = 未声明（旧客户端）→ 主机按 already_has_world 推断（向后兼容）
//   1 = 全新加入（Project 已销毁 / 首次加入）→ 主机必须**先清理该身份旧连接**再走全新加入
//   2 = 掉线重连（Project 仍在，30 秒重连窗口内）→ 主机踢掉旧连接后**继承会话记录**并追帧
// Source: doc/reconnect-and-kick-plan.md G9
uint32 join_intent = 16;
```

客户端填值规则（唯一来源，与 `RejoinFlagPolicy` 同源判断）：

| 客户端状态 | `join_intent` | 与 `already_has_world` 的关系 |
|---|---|---|
| `Project` 已销毁 / 首次加入 | `1` | 必为 false |
| `Project` 仍在 + 会话一致（重连窗口内） | `2` | 必为 true |
| `Project` 仍在但会话不一致（主机重启过） | `1` | 必为 false |

即：`join_intent == 2` ⇔ `already_has_world == true`；
`join_intent == 1` 覆盖了 `already_has_world == false` 的全部情况。
**新字段的价值在于让主机在「要清理旧连接」这件事上不再靠猜。**

#### 5.9.3 主机处理矩阵

| `join_intent` | 主机动作 |
|---|---|
| `0`（旧客户端） | 完全按现有 `already_has_world` 逻辑（行为不变） |
| `1`（全新加入） | **先**清理该身份旧连接（踢传输 + `ClientLeft` + 中止世界传输 + 释放角色）→ 再走首次加入、下发整图 |
| `2`（掉线重连） | 踢掉旧连接（同 5.3）→ **继承** `PlayerSessionRecord`（`ReportedLoaded` / `Verdicts` / `MemoryChunks` 保留，`CurrentClientId` 改指新连接）→ 按 `LastAppliedSequence` 追帧 |

**「清理旧连接」是 `join_intent ∈ {1, 2}` 时的共同前置步骤** —— 这就是用户
2026-11-06 的要求：「主机端收到客户端的加入请求，也是要清理掉客户端的连接再走全新加入的途径」。

#### 5.9.4 不做的事

**房间选择 / 加入界面不做任何标注**（用户 2026-11-06 明确）：
`join_intent` 纯粹是**协议层**的语义区分，UI 不展示「这是重连」还是「全新加入」。

---

## 6. 完整状态机

### 6.1 客户端

```
[InWorld] --连接死-->
    │
    ├─ 收到 Kicked           ──→ [退出世界]  （G6：不重连）
    ├─ worthReconnect 成立    ──→ [ReconnectWindow 30s]
    │      (Project 在 && GameScreen && 非死亡复活 && worldLoaded)
    │                          ├─ 重连成功 → [InWorld]
    │                          │     join_intent=2, already_has_world=true, 主机追帧
    │                          └─ 30s 到点 → **立即**停手 → [退出世界]   （5.1.1）
    └─ worthReconnect 不成立  ──→ [退出世界]  （现有行为）
```

**下次加入时**（无论从上面哪条路退出）：

| 客户端状态 | `join_intent` | `already_has_world` |
|---|---|---|
| `Project` 已销毁 | `1`（全新加入） | false |
| `Project` 仍在但会话不一致（主机重启） | `1`（全新加入） | false |
| `Project` 仍在 + 会话一致（窗口内重连） | `2`（掉线重连） | true |

### 6.2 主机

```
[Client N 在线]
   │
   ├─ 收到 Hello(identity=I, join_intent=J)
   │     │
   │     ├─ J=1（全新加入）且已有 other(I)
   │     │     → 清理 other：踢传输 + ClientLeft + 中止世界传输 + 释放角色
   │     │     → 丢弃旧会话记录 → 走首次加入、下发整图                    （G9/G5）
   │     │
   │     ├─ J=2（掉线重连）且已有 other(I)
   │     │     → 踢掉 other（同上）
   │     │     → **继承** PlayerSessionRecord（ReportedLoaded/Verdicts/
   │     │       MemoryChunks 保留，CurrentClientId 改指新连接）        （G9/G5）
   │     │     → 按 LastAppliedSequence 追帧
   │     │
   │     └─ J=0（旧客户端）→ 按现有 already_has_world 逻辑（行为不变）
   │
   ├─ 30s 无任何帧（静默）
   │     → 清理 + **派发 ClientLeft**（不能只清 m_clients）            （5.4）
   │
   └─ 主连接 Fail
         → 连带 Fail 同 clientId 的电路/地形 Peer                       （G7）
```

---

## 7. 改动清单

| # | 文件 | 改动 | 对应口径 |
|---|---|---|---|
| 1 | `Modules/TerrainSyncModule.cs` | 客户端断线分支加 30s 重连窗口；`worthReconnect` 判定；窗口耗尽才退出 | G1/G3 |
| 2 | `Engine/SyncConfig.cs` | 新增 `reconnectwindowms` / `reconnectintervalms` | G1 |
| 3 | — | **无改动**：重连期间不拦输入、不冻结时钟（5.2.1/5.2.2 有代码依据） | G2 |
| 4 | — | **推迟**：重连 UI 提示并入后续「网络状态显示」功能（5.2.3） | G2 |
| 5 | `Modules/TerrainSyncModule.cs` | 断线分支改为 `DisposeAllSecondaryClients` | G7 |
| 6 | `Terrain/TerrainSync.cs` `HandleHello` | 同身份旧连接强制踢出 + 会话记录交接 | G5 |
| 7 | `Terrain/TerrainSync.cs` 静默清理分支 | 补 `ClientLeft` 派发 | G5 |
| 8 | `Net/TerrainNet.cs` `Fail` 路径 | 主连接失败连带 fail 附属 Peer | G7 |
| 9 | `Protocol/protocol.proto` | `HelloAck.kicked/kick_reason` + `MessageType.Kicked` + `message Kicked` | G6 |
| 10 | `Modules/TerrainSyncModule.cs` | 处理 `Kicked` / `HelloAck.kicked` → 直接退出世界 | G6 |
| 11 | `Engine/MultiplayerTimeFlow.cs` | **新文件**：`BindFieldRef<SubsystemTime,float>("m_gameTimeFactor")` 绑一次；每帧联机时把 `<= 0f` 拉回 `1f` | G8 |
| 12 | `Plug/SuMultiplayerMod.cs` | 组合根 `Frame.Update`（模块 tick 之后）调 11；并补主机 `GameManager.UpdateProject()` | G8 |
| 13 | — | **推迟**：`Reconnecting...` HUD（并入后续网络状态显示，5.2.3） | G2 |
| 14 | `Protocol/protocol.proto` | `Hello.join_intent = 16`（append-only，不动字段 7 语义） | G9 |
| 15 | `Modules/TerrainSyncModule.cs` + `Terrain/RejoinFlagPolicy.cs` | 填 `join_intent`（与 `already_has_world` 同源判断） | G9 |
| 16 | `Terrain/TerrainSync.cs` `HandleHello` | 按 `join_intent` 走矩阵：`1` = 清理旧连接后全新加入；`2` = 踢旧连接并继承会话记录 | G9 |

**关于 G4（地形改动缓冲）：零改动。** 现有的 `TerrainSyncOptions.CatchUpRetentionMs`
（60 000 ms）与 `CatchUpMaxJournalEntries`（8192 条）构成的 `m_changeJournal` 就是那个缓冲：
主机一直在记录地形改动，客户端重连时用 `Hello.LastAppliedSequence` 与主机的
`head` / `oldest` 比对，水位在保留范围内即接受重连并由 `CatchUp` 逐条补发，
超出范围则明确要求整图重下（有日志）。

**30 秒重连窗口 < 60 秒保留期**，配置自洽：只要在窗口内重连成功，追帧一定够用。

---

## 8. 不做的事（明确排除）

- **不加读超时 / 调 TCP KeepAlive 参数来解决瞬断**：socket 被内核销毁是「立即失败」，
  与超时无关（见 2.2）。TCP KeepAlive 保留（用户要求），但它只是空闲保活，
  不参与这个路径的判定。
- **不新建地形改动缓冲**：`m_changeJournal` 已覆盖 60 秒，大于 30 秒重连窗口（G4）。
- **不改 2026-10-26 的「主机真掉线要退出世界」口径**：本次只是把「我这边瞬断」
  从「主机掉线」里分离出来。
- **不做 UDP 语音通道**：与本次无关。

---

## 9. 验收标准

| # | 场景 | 期望 |
|---|---|---|
| A1 | 客户端 WiFi 瞬断 1~2 秒后恢复 | **不退出世界**；自动重连成功；世界模拟短暂冻结后恢复；无整图重下（日志 `rejoin accepted`） |
| A2 | 客户端断网 30 秒以上 | 30 秒后退出世界 + 弹窗（现有行为） |
| A3 | 客户端断网 30 秒后重新加入（Project 已销毁） | 主机按首次加入下发整图，能正常进世界（**现象 B 不复发**） |
| A4 | 主机主动踢出 / 同身份新连接接管 | 客户端**立刻**退出世界并弹窗，**不进入** 30 秒重连窗口 |
| A5 | 主机侧旧连接清理 | 主机日志出现 `ClientLeft`；`WorldTransferModule` 不再被旧传输阻塞；旧角色从世界移除 |
| A6 | 断线期间地形改动 | 重连后追帧补齐，客户端地形与主机一致（`m_changeJournal` 覆盖） |
| A7 | 附属通道 | 主连接失效后，主机侧该 clientId 不再残留电路/地形 Peer（LiveMap 三通道行数下降） |
| A8 | 主机按 ESC / 顶部返回（GameMenuDialog） | 其它客户端的世界**继续跑**（电路步进、动物、天气都不停） |
| A9 | 主机进 Settings / Help / Edit Players | 主机自己的世界**继续跑**（补 `UpdateProject`）；其它客户端也不受影响 |
| A10 | 客户端进 Settings / Help / Edit Players | 客户端照常收发（mod tick 不经 `SubsystemUpdate`）；退出后地形视觉一次补齐；主机不判它掉线 |
| A12 | 重连中玩家继续挖方块 | 本地瞬时显示消失 → 重连后**自动回滚**到主机值；无报错、无卡死、无输入僵住 |
| A13 | 客户端已退出世界（Project 销毁）后重新加入，主机侧旧连接仍在 | 主机日志出现「清理旧连接」+ `ClientLeft`；新连接走**全新加入**并收到整图；**现象 B 不复发** |
| A14 | 客户端断线 50 秒后才重连 | 主机明确 `rejoin rejected`（超出改动历史保留期）并回落整图；客户端不卡死 |
| A11 | 单机（未联机） | 行为与原生完全一致（ESC/设置**照常**暂停世界，不做任何干预） |

---

## 10. 风险与注意事项

1. **`GameTimeFactor` 每帧覆写**：`SubsystemTime.NextFrame` 每帧都会按菜单可见性重写该字段，
   `WorldFreezeGate` 必须**每帧重设**，且要在 `NextFrame` 之后执行（模块 tick 在
   `Frame.Update` 链上，需实测确认时序）。

2. **重连成功后的世界一致性**：冻结只防「客户端本地改地形」，但主机侧在断线期间仍有
   天气/季节/实体演化。追帧（`CatchUp`）覆盖地形改动；实体由 `EntityReplication` 全量快照恢复。
   需实测确认「冻结 30 秒 + 追帧」后客户端世界与主机一致。

3. **会话记录交接的边界**：同身份踢旧连接时，`PlayerSessionRecord` 里的
   `ReportedLoaded` / `Verdicts` / `MemoryChunks` 是「这个玩家」的状态，
   应保留；但 `CurrentClientId` 必须改指向新连接，否则 `MarkChunkChanged` 的
   「同一身份只由当前句柄代表」判断会跳过它。

4. **协议 hash 变更**：新增 proto 字段会让 `MultiplayerProtocolHash` 改变，
   新旧 mod 版本无法互相加入（现有兼容性机制），发布时需同步通知。

5. **联机禁冻结必须只作用于联机会话**：单机必须保持原生暂停行为（ESC 暂停是对单机玩家
   有用的功能）。判定用「主机建房 或 客户端已连」，并且在 `Project == null`（主菜单）时直接跳过。

6. **`Frame.Update` 的时序依赖是本方案的关键前提**（已在 5.8.5 用源码确认）：
   `Program.cs` 里 `ScreensManager.Update()` 先跑、`Frame.Update` 后触发。若未来引擎换了
   主循环顺序，`m_gameTimeFactor` 的每帧回拉会晚一帧 —— 表现为进入菜单后世界停一帧再恢复，
   不致命但需重测。

7. **重连期间没有 UI 反馈是已知的小缺陷**：玩家不会看到「正在重连」的提示，
   只会感觉「操作一时没效果」。已决定并入后续的**网络状态显示**功能一起做（5.2.3），
   本期不做。若在实现网络状态显示之前这个问题造成困扰，可临时用日志 / HUD 位置最低成本的方案兜底。

8. **踢出语义的两种来源要区分**：
   - `HelloAck.rejected`（审批拒绝）——现有，握手期；
   - `HelloAck.kicked`（新增）——同身份接管 / 管理踢出，**禁止重连**；
   - 运行期 `MessageType.Kicked`——已连接状态下被踢。
   三者不能混用，否则客户端会进入错误的重连窗口。
