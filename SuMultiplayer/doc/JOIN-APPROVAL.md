# 加入审批（Join Approval）

> 用户口径（2026-10-21）：参考旧联机 mod —— **玩家端加入必须主机同意**；不想每次点可以打开"自动同意加入"。 
> 审批发生在**任何角色记录 / 角色实体 / 世界传输之前**：被拒绝的连接不会在主机上留下角色记录或联机化身。

## 配置（世界作用域，跟地图走）

文件：`<世界目录>/SuMultiplayerWorld.json`（不存在 = 默认，不自动创建）

```json
{
  "autoapprovejoins": false
}
```

| 键 | 值 | 说明 |
|---|---|---|
| `autoapprovejoins` | `false`（默认）/ `true` | `false` = 每个**新加入**都要主机在弹窗里点 `Allow`；`true` = 直接放行。无头主机没有界面可点 Allow，必须显式设为 `true`（否则 120 秒后按超时明确拒绝）。 |

- 配置在**换世界时重读**（每个世界可以不同）。
- 同一次房间内的断线重连（Hello 声明的下载世界会话 == 当前主机会话）**不重复弹审批**，
  与旧联机 mod 的 rejoin grace 口径一致；重新"加入"（会话不一致 / 首次加入）才需要同意。
- 读失败 = 保持默认（手动审批），日志写清原因；绝不因为文件坏了就自动放行。

## 流程

```
客户端 Hello → 主机检查是否自动同意 / 是否需要审批
  ├─ 自动同意（autoapprovejoins=true）→ 继续资料闸门 → HelloAck → 世界传输
  ├─ 需要审批 → 主机弹窗 [Allow] [Reject]（120 秒超时）
  │    ├─ Allow → 继续完整握手
  │    └─ Reject / 超时 → HelloAck.rejected + reject_reason，移除连接状态
  └─ 拒绝的连接：不建角色记录、不建联机化身、不发世界
客户端收到 rejected → 关闭通道、回 Play 屏、弹窗显示拒绝原因（不再自动重连）
```

- 协议：`HelloAck.rejected = 17`、`HelloAck.reject_reason = 18`（append-only，旧端不认 = 不会触发）。
- 审批界面的名字/身份：`<PlayerData.Name> (身份前 8 位)`；主机日志里有完整一行
  `[SuMP][join-approval] 等待主机决定 / 主机同意 / 主机拒绝` 与 Terrain 层 `加入审批：…` 轨迹。
