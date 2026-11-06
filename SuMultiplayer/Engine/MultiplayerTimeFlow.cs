using System;
using Game;
using GameEntitySystem;
using SuAPI;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 联机时**禁止世界时钟被本地界面冻结**的唯一实现（<c>doc/reconnect-and-kick-plan.md</c> G8）。
    ///
    /// 为什么需要（实机问题，2026-11-06）：
    ///   引擎在 <c>SubsystemTime.NextFrame</c> 里按「**所有**玩家是否都开着 <c>GameMenuDialog</c>」
    ///   决定世界时钟：
    ///   <code>
    ///   bool flag = true;
    ///   foreach (ComponentPlayer p : m_subsystemPlayers.ComponentPlayers)
    ///       if (!p.ComponentGui.IsGameMenuDialogVisible()) { flag = false; break; }
    ///   if (flag) GameTimeFactor = 0f;                          // ← 整个世界停住
    ///   else if (GameTimeFactor == 0f) GameTimeFactor = 1f;
    ///   </code>
    ///   单机下这个行为是对的；**联机房间里有别的设备上的玩家**，任何一端打开 ESC / 游戏统计 /
    ///   编辑玩家 / 设置 / 帮助 / 生物图鉴 / 合成百科都不该让别人的世界停住。
    ///
    /// 为什么必须**每帧**拉回：
    ///   <c>NextFrame()</c> 由 <c>GameScreen.Update → GameManager.UpdateProject →
    ///   SubsystemUpdate.Update</c> 驱动，**每帧都会重算并覆写**这个字段。设一次没用。
    ///
    /// 为什么用 <see cref="IModParentField.BindFieldRef{TTarget,TValue}"/> 而不是
    /// <see cref="IModParentField.ModifyParentField"/>：
    ///   这是每帧热路径。SuAPI 对该 API 的定位写得很明确 ——
    ///   <c>BindFieldRef</c> 是 **preferred API for repeated per-frame access**，
    ///   返回 <c>ref TValue</c>，**no lookup or boxing**；而 <c>ModifyParentField</c>
    ///   是 low-frequency access（每次要走字典查找 + 装箱）。
    ///   另外 <c>SubsystemTime.GameTimeFactor</c> 的 setter 是 private，且**属性没有存储位置**，
    ///   拿不到 <c>ref</c> —— 所以这里只能用私有字段 <c>m_gameTimeFactor</c>
    ///   （旧实现 <c>Mod/ScMultiplayer</c> 的 MaintainMultiplayerTimeFlow 改的也是同一个字段）。
    ///
    /// 为什么只写 <c>&lt;= 0f</c>：
    ///   睡眠加速走的是另一条路径（<c>FixedTimeStep = 0.05f</c> + <c>UpdatesPerFrame = 20</c>），
    ///   <c>NextFrame</c> 会把时钟因子设成 <c>frameDuration / fixedStep</c>（&gt; 1），不是 0。
    ///   条件写 <c>&lt;= 0f</c> 既不会碰睡眠加速，也不会覆盖其它 mod 设置的 &gt; 1 的值。
    ///
    /// 线程：只在游戏主线程调用（模块 tick / Frame.Update）。无静态可变状态。
    /// </summary>
    public sealed class MultiplayerTimeFlow
    {
        /// <summary>引擎字段名（<c>SubsystemTime.m_gameTimeFactor</c>；见文件头注释说明为什么用字段不用属性）。</summary>
        public const string GameTimeFactorField = "m_gameTimeFactor";

        private readonly IModParentField m_parentField;

        /// <summary>一次绑定、每帧零反射开销的 ref 入口。<c>null</c> = 未绑定（模块没拿到 SuAPI）。</summary>
        private readonly ModFieldRef<SubsystemTime, float> m_gameTimeFactorRef;

        private string m_bindFailureReason;
        private long m_pullBackCount;
        private long m_unexpectedValueCount;
        private long m_lastProblemLogMs;
        private long m_missingSubsystemCount;

        public MultiplayerTimeFlow(IModParentField parentField)
        {
            m_parentField = parentField;
            if (parentField == null)
            {
                m_bindFailureReason = "没有 IModParentField（组合根未注入 SuAPI）";
                return;
            }
            try
            {
                m_gameTimeFactorRef =
                    parentField.BindFieldRef<SubsystemTime, float>(GameTimeFactorField);
                if (m_gameTimeFactorRef == null)
                    m_bindFailureReason = "BindFieldRef 返回 null（字段 " +
                        GameTimeFactorField + " 不存在？）";
            }
            catch (Exception e)
            {
                m_bindFailureReason = e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>绑定是否成功（失败时 <see cref="Maintain"/> 是空操作，但原因会写日志）。</summary>
        public bool IsBound => m_gameTimeFactorRef != null;

        /// <summary>绑定失败原因（空 = 成功）。</summary>
        public string BindFailureReason => m_bindFailureReason ?? string.Empty;

        /// <summary>累计把时钟从 ≤0 拉回 1 的次数（诊断：联机时进菜单的次数大致对应）。</summary>
        public long PullBackCount => m_pullBackCount;

        /// <summary>累计项目 / 子系统不可用、无法维护的次数（诊断用，不刷屏）。</summary>
        public long MissingSubsystemCount => m_missingSubsystemCount;

        /// <summary>
        /// **每帧**调用一次（在引擎 <c>NextFrame()</c> 之后，即 mod 的 Frame.Update 里）。
        ///
        /// <paramref name="multiplayerSessionActive"/> = 本端此刻参与联机会话
        /// （主机已建房 **或** 客户端已连上）。**false（单机）时本方法完全不干预** ——
        /// 单机的 ESC 暂停是引擎正确行为，不能动。
        /// </summary>
        public void Maintain(bool multiplayerSessionActive, long nowMs)
        {
            if (!multiplayerSessionActive) return;
            if (m_gameTimeFactorRef == null) return;
            Project project = GameManager.Project;
            if (project == null) return;                     // 主菜单 / 世界已卸载：没有时钟可维护
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            if (time == null)
            {
                m_missingSubsystemCount++;
                return;
            }
            ref float factor = ref m_gameTimeFactorRef(time);
            if (factor > 0f) return;                         // 正常在跑：不碰（也不碰睡眠加速的 >1）
            // 先记下原值再改写（改完就是 1f 了，回头再看就没法判断它原本是不是负值）。
            float previous = factor;
            // 引擎刚把它压成 0（本帧所有人都开着 GameMenuDialog）→ 联机时把它拉回来。
            factor = 1f;
            m_pullBackCount++;
            if (previous < 0f)
            {
                // 不可能由引擎产生（GameTimeFactor 的 setter 有 Clamp 0..1024）：
                // 真出现了说明字段被别的代码直接写坏，明确记一行。
                m_unexpectedValueCount++;
                LogProblem(nowMs, "世界时钟因子出现负值（" + previous +
                    "）→ 已拉回 1；请检查是否有其它代码直接写 SubsystemTime.m_gameTimeFactor");
            }
        }

        /// <summary>Composition-root 启动期一次性自检：绑定失败要明确报出来（不静默）。</summary>
        public string Describe()
        {
            return "multiplayerTimeFlow bound=" + (IsBound ? "yes" : "no") +
                (IsBound ? string.Empty : "（" + BindFailureReason + "）") +
                " pullBacks=" + m_pullBackCount +
                " missingSubsystem=" + m_missingSubsystemCount;
        }

        private void LogProblem(long nowMs, string message)
        {
            if (m_lastProblemLogMs != 0 && nowMs - m_lastProblemLogMs < 10000) return;
            m_lastProblemLogMs = nowMs;
            ModLog.Warn("[SuMP][time-flow] " + message);
        }
    }
}
