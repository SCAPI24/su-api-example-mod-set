using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎侧 reach 读取（**唯一来源**）：与 <c>ComponentMiner.Raycast</c> 完全同源，
    /// 不再让任何交互路径自己写 5 / 7.5 / 8 这类常量。
    ///
    /// Source: Survivalcraft/Game/ComponentMiner.cs:378
    ///   <c>reach = (WorldSettings.GameMode == GameMode.Creative)
    ///             ? SettingsManager.CreativeReach : 5f</c>
    /// Source: Survivalcraft/Game/SettingsManager.cs:166/235
    ///   <c>CreativeReach</c> 默认 7.5，客户端本地可调。
    /// </summary>
    internal static class EngineReach
    {
        /// <summary>主机 / 本机世界当前是不是创造模式（没有 Project / GameInfo = 不是）。</summary>
        public static bool IsCreative(Project project)
        {
            SubsystemGameInfo info = project?.FindSubsystem<SubsystemGameInfo>(false);
            return info != null && info.WorldSettings.GameMode == GameMode.Creative;
        }

        /// <summary>本端此刻的有效 reach 声明（引擎精确值，已夹到可发送范围）。</summary>
        public static InteractionReachClaim LocalClaim()
        {
            Project project = GameManager.Project;
            bool creative = IsCreative(project);
            return InteractionReachGate.LocalClaim(creative, SettingsManager.CreativeReach);
        }

        /// <summary>本端此刻的有效 reach（米）。</summary>
        public static float LocalReach() => LocalClaim().Meters;
    }
}
