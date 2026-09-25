using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P5：**主机侧执法**（第一批：挖 + 放）。设计稿 §5「主机侧执法点（统一入口）」。
    ///
    /// 锁定口径：
    ///   · 只有**拥有者**能修改领地内的方块（主机/GM 也不例外 —— 要改就先"赋予领地"给自己）；
    ///   · **重叠格归编号最小者**（复用 <see cref="OwnerClaimAt"/>）；
    ///   · 否决时**不落地**，走既有"挖掘结果/放置结果被拒"的回滚路径（客户端自己把预测改回去）；
    ///   · 同时**通知发起人**（一行提示，按客户端节流）并写主机审计。
    ///
    /// 本阶段覆盖：客户端发起的挖（`TerrainDigRequestMessage`）与放（`InteractRequest` +
    /// `HasTerrainPrediction`）。主机**本机**玩家的挖/放、活塞/发射器/流体、拾取/点燃/爆炸见 P6/P7。
    /// </summary>
    public partial class ScMultiplayer
    {
        private const double RegionDenyNoticeInterval = 1.0;

        private readonly Dictionary<long, double> m_regionDenyNoticeTimes =
            new Dictionary<long, double>();

        // 节流键 = **clientId + 动作**，不能只用 clientId：否则同一秒内不同动作会互相挤掉。
        // 2026-09-25 实测：P7b 的拾取守卫（主机本机、每秒一条 `（pickup）`）会把同一秒的
        // `（dig）` 通知整个盖掉，日志里只能看到 pickup，像是 dig 守卫没工作。
        private static long RegionDenyNoticeKey(int clientId, string action)
        {
            unchecked
            {
                int hash = 17;
                if (!string.IsNullOrEmpty(action))
                {
                    for (int i = 0; i < action.Length; i++)
                        hash = hash * 31 + action[i];
                }
                return ((long)clientId << 32) ^ (uint)hash;
            }
        }

        /// <summary>
        /// 该客户端能否修改该格：无领地 → 可以；有领地 → 必须是拥有者（身份按 userId 判定）。
        /// </summary>
        internal bool CanRegionModifyCell(int clientId, Point3 cell, out RegionClaim claim,
            out string reason)
        {
            claim = OwnerClaimAt(cell);
            reason = null;
            if (claim == null)
                return true;
            if (claim.Owners.Count == 0)
            {
                // 没有拥有者的领地（理论上只在建立者没有身份时出现）：只放行主机自己，避免把管理端锁死。
                if (IsHost && clientId == 0)
                    return true;
                reason = "领地 #" + claim.Id + " 还没有指定拥有者，只有主机可以修改";
                return false;
            }
            if (!TryResolveRegionClaimOwnerIdentity(clientId, out string userId, out string _))
            {
                reason = "拿不到你的身份，无法确认领地 #" + claim.Id + " 的拥有关系";
                return false;
            }
            for (int i = 0; i < claim.Owners.Count; i++)
            {
                if (string.Equals(claim.Owners[i].UserId, userId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            reason = "这块区域属于 " + claim.DescribeOwners() + "（领地 #" + claim.Id + "），修改已否决";
            return false;
        }

        /// <summary>否决后通知发起人（一行提示，按客户端 1 秒节流）并写主机审计。</summary>
        internal void NotifyRegionModificationDenied(int clientId, Point3 cell, RegionClaim claim,
            string action, string reason)
        {
            if (claim == null)
                return;
            PublishServerAudit("region.deny." + action, clientId,
                "id=" + claim.Id + " cell=" + cell.X.ToString(CultureInfo.InvariantCulture) + "," +
                cell.Y.ToString(CultureInfo.InvariantCulture) + "," +
                cell.Z.ToString(CultureInfo.InvariantCulture));
            double now = Time.RealTime;
            long noticeKey = RegionDenyNoticeKey(clientId, action);
            if (m_regionDenyNoticeTimes.TryGetValue(noticeKey, out double last) &&
                now - last < RegionDenyNoticeInterval)
                return;
            m_regionDenyNoticeTimes[noticeKey] = now;
            string text = string.IsNullOrEmpty(reason)
                ? "这里属于 " + claim.DescribeOwners() + "（领地 #" + claim.Id + "），修改已否决"
                : reason;
            // 末尾附动作标签，便于客户端日志/复盘区分是"挖 / 放 / 拾取"中的哪一种被否决
            text = text + "（" + action + "）";
            if (clientId == 0)
            {
                // 主机本机玩家：直接进本地聊天/提示
                Log.Information("[ScMP] " + text);
                return;
            }
            NetworkMessageSender.SendScheduledMessage(clientId,
                new ChatMessage("ScMP", "region", text));
        }
    }
}
