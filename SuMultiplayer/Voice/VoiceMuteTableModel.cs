using System;
using System.Collections.Generic;
using System.Text;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 静音表格的一行（纯数据）：<c>名称</c> + <c>[MUTE]/[UNMUTE]</c>。
    ///
    /// 产品口径（2026-10-21）：
    ///   · 表格里**只列远端**：过滤自己；主机还要过滤 client_id = 0（主机自己）；
    ///   · 已静音 = 红色底 + 名字后缀 <c> *</c>，按钮文字 <c>UNMUTE</c>；
    ///     未静音 = 正常色，按钮文字 <c>MUTE</c>；
    ///   · 顺序沿用 <see cref="VoicePeerRoster.Snapshot"/>（按 client_id 升序）—— 行不跳动。
    ///
    /// 纯逻辑，不依赖 Engine / Game（自检直接编译同一份源码）。
    /// </summary>
    public struct VoiceMuteRow
    {
        public int ClientId;
        public string Name;
        public bool Muted;

        public VoiceMuteRow(int clientId, string name, bool muted)
        {
            ClientId = clientId;
            Name = name;
            Muted = muted;
        }
    }

    /// <summary>
    /// 静音表格的**唯一**构造 / 文案 / 颜色规则。
    ///
    /// Source: Survivalcraft/Game/BevelledButtonWidget.cs（表格按钮沿用 HUD 同款按钮）；
    /// Source: Survivalcraft/Game/ScrollPanelWidget.cs（行多时滚动）；
    /// Source: Modules/VoiceChatModule.cs:ToggleMuteAll / IsMutedAll / TogglePeerMute /
    ///         IsPeerMuted（动作与状态都走模块公开方法，表格不复制静音状态）。
    /// </summary>
    public static class VoiceMuteTableRules
    {
        public const string DialogTitle = "VOICE";
        public const string MuteText = "MUTE";
        public const string UnmuteText = "UNMUTE";
        public const string MuteAllText = "MUTE ALL";
        public const string UnmuteAllText = "UNMUTE ALL";
        public const string CloseText = "CLOSE";

        /// <summary>已静音行的名字后缀（产品要求：红色底 + 名字后缀 " *"）。</summary>
        public const string MutedSuffix = " *";

        // 颜色口径（0xRRGGBB；UI 层再转 Engine.Color，自检不用引擎类型）：
        //   未静音 = 与 HUD 的 TALK 同一档冷色；已静音 = 红底（与旧 HUD 的 MUTE 红一致）。
        public const uint RowCenterRgb = 0x3C5A6E;
        public const uint RowMutedCenterRgb = 0x823C3C;
        public const uint MuteAllCenterRgb = 0x3C5A6E;
        public const uint MuteAllMutedCenterRgb = 0x823C3C;

        public static string ActionText(bool muted) => muted ? UnmuteText : MuteText;

        public static string MuteAllActionText(bool mutedAll) =>
            mutedAll ? UnmuteAllText : MuteAllText;

        public static uint RowCenter(bool muted) => muted ? RowMutedCenterRgb : RowCenterRgb;

        public static uint MuteAllCenter(bool mutedAll) =>
            mutedAll ? MuteAllMutedCenterRgb : MuteAllCenterRgb;

        /// <summary>行名：远端名（空白 → Player &lt;id&gt;）+ 已静音后缀。</summary>
        public static string RowLabel(string name, int clientId, bool muted)
        {
            string display = string.IsNullOrWhiteSpace(name) ? "Player " + clientId : name.Trim();
            return muted ? display + MutedSuffix : display;
        }

        /// <summary>
        /// 构造表格行：过滤自己；主机再过滤 client_id = 0；其余按名册顺序保留。
        /// <paramref name="isMuted"/> 由调用方给出模块的实时状态（不缓存）。
        /// </summary>
        public static VoiceMuteRow[] BuildRows(IReadOnlyList<VoicePeerState> peers,
            int localClientId, bool hosting, Func<int, bool> isMuted)
        {
            if (isMuted == null) throw new ArgumentNullException(nameof(isMuted));
            if (peers == null) return Array.Empty<VoiceMuteRow>();
            var rows = new List<VoiceMuteRow>(peers.Count);
            for (int i = 0; i < peers.Count; i++)
            {
                VoicePeerState peer = peers[i];
                if (peer.ClientId == localClientId) continue;
                if (hosting && peer.ClientId == 0) continue;
                bool muted = isMuted(peer.ClientId);
                rows.Add(new VoiceMuteRow(peer.ClientId, RowLabel(peer.Name, peer.ClientId, muted),
                    muted));
            }
            return rows.ToArray();
        }

        /// <summary>
        /// 行集合签名：peers / 名字 / 静音状态任一变化都要重画表格。
        /// 对话框打开期间每帧比较它，变化才重建（不每帧重建控件）。
        /// </summary>
        public static string Signature(IReadOnlyList<VoiceMuteRow> rows)
        {
            if (rows == null || rows.Count == 0) return string.Empty;
            var builder = new StringBuilder(rows.Count * 24);
            for (int i = 0; i < rows.Count; i++)
            {
                builder.Append(rows[i].ClientId).Append(':')
                    .Append(rows[i].Name).Append(':')
                    .Append(rows[i].Muted ? '1' : '0').Append(';');
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// <c>IsClicked</c> 的**一次按下只动作一次**闸门（纯逻辑）。
    ///
    /// 为什么需要：<c>IsClicked</c> 是 <c>ClickableWidget.Update()</c> 每帧重算的单帧边沿，
    /// 但控件树一旦停止 Update（切屏 / 对话框淡出）就可能冻结在 true；
    /// 驱动方若在 <c>Frame.Update</c> 里读它，就会每帧重复动作。
    /// Source: UI/RoomUiAttacher.cs:TryConsumeClick（同一纪律的 HUD 版本）。
    /// 复位条件 = 该控件真的回到 <c>IsClicked == false</c>。
    /// </summary>
    public sealed class VoiceClickEdgeGate
    {
        private readonly HashSet<object> m_consumed = new HashSet<object>();

        public int ConsumedCount => m_consumed.Count;

        public bool TryConsume(object key, bool isClicked)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (!isClicked)
            {
                m_consumed.Remove(key);
                return false;
            }
            if (m_consumed.Contains(key)) return false;
            m_consumed.Add(key);
            return true;
        }

        /// <summary>
        /// 丢掉"肯定不会再被误读"的记录：控件已不在活动列表、且 IsClicked 已回到 false。
        /// 仍冻结在 true 的记录必须保留（否则重新出现时会把同一次旧点击再触发一遍）。
        /// Source: UI/RoomUiAttacher.cs:TickCore 的 m_consumedClicks.RemoveWhere。
        /// </summary>
        public void Prune(Func<object, bool> keep)
        {
            if (keep == null) throw new ArgumentNullException(nameof(keep));
            var drop = new List<object>();
            foreach (object key in m_consumed)
            {
                if (!keep(key)) drop.Add(key);
            }
            for (int i = 0; i < drop.Count; i++) m_consumed.Remove(drop[i]);
        }

        public void Clear() => m_consumed.Clear();
    }

    /// <summary>
    /// VO 表格 / VO 按钮的**显式计数**（自检可断言、状态行 / 限流日志里逐项可读）。
    /// 名字与产品口径一致：voButtonCreateFailures / voTableFailures 等。
    /// </summary>
    public sealed class VoiceMuteTableCounters
    {
        // VO 按钮（MoreContents）生命周期。
        public long ButtonCreates;
        public long ButtonCreateFailures;
        public long ButtonRemovals;
        public long MoreContentsMisses;
        public long TickFailures;

        // 表格对话框生命周期与交互。
        public long TableOpens;
        public long TableFailures;
        public long AlreadyOpen;
        public long DialogBuildFailures;
        public long RowsRebuilt;
        public long RowClicks;
        public long MuteAllClicks;
        public long CloseClicks;
        public long UpdateFailures;

        public string Describe() =>
            "voButtons=" + ButtonCreates +
            " voButtonCreateFailures=" + ButtonCreateFailures +
            " voButtonRemovals=" + ButtonRemovals +
            " voMoreContentsMissing=" + MoreContentsMisses +
            " voTickFailures=" + TickFailures +
            " voTableOpens=" + TableOpens +
            " voTableFailures=" + TableFailures +
            " voTableAlreadyOpen=" + AlreadyOpen +
            " voDialogBuildFailures=" + DialogBuildFailures +
            " voRowsRebuilt=" + RowsRebuilt +
            " voRowClicks=" + RowClicks +
            " voMuteAllClicks=" + MuteAllClicks +
            " voCloseClicks=" + CloseClicks +
            " voTableUpdateFailures=" + UpdateFailures;
    }
}
