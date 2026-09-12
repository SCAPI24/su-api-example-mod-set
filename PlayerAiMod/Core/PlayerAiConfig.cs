using System;

namespace PlayerAiMod
{
    /// <summary>
    /// PlayerAiMod 运行参数。
    ///
    /// 目前是代码内默认值（改完重新编译即可）；等行为状态定型、需要热调时再按
    /// CmdBridgeMod 的 CmdBridgeConfig 模式改成 JSON 文件配置。
    /// </summary>
    public static class PlayerAiConfig
    {
        /// <summary>与 ModInfo.xml 保持一致。</summary>
        public const string ModVersion = "0.2.0";

        /// <summary>总开关：关闭时不创建角色、不注入任何输入。</summary>
        public static bool Enabled = true;

        // ---------------------------------------------------------------- 状态机

        /// <summary>单帧最多允许的转移次数，防止守卫互相打脸时一帧内来回横跳。</summary>
        public static int MaxTransitionsPerTick = 8;

        /// <summary>状态最短驻留（秒）：小于它的转移候选会被跳过，用于抑制抖动。0 = 不限制。</summary>
        public static float DefaultMinDwellSeconds = 0.15f;

        /// <summary>最近转移记录条数（Snapshot 与调试用）。</summary>
        public static int TransitionLogCapacity = 32;

        /// <summary>详细日志：状态进入/退出、转移裁决过程。默认关闭（热路径不刷日志）。</summary>
        public static bool VerboseLogging = false;

        /// <summary>安全状态 ID：状态 Tick 抛异常或触发不可恢复错误时回退到这里。</summary>
        public static string SafeStateId = AiStateIds.Idle;

        // ---------------------------------------------------------------- 行为树包

        /// <summary>
        /// 世界就绪后是否自动装载并运行一棵行为树（默认**开**：装上 Mod 就能看到 AI 按包里的树行动）。
        /// 装载失败只记日志、保持待机，不会反复重试。
        /// </summary>
        public static bool AutoLoadTreeOnStart = true;

        /// <summary>启动时装载哪个包（包名或相对路径，见 `PlayerAi/BehaviorTrees/`）。</summary>
        public static string StartupTreePackage = PackageTemplates.DemoFile;

        // ---------------------------------------------------------------- 动作包回放（P1）

        /// <summary>回放漂移容忍：与关键帧的位置差超过它就判失败（米）。</summary>
        public static double ReplayDriftToleranceMeters = 3.0;

        /// <summary>回放漂移容忍：朝向差超过它就判失败（度）。</summary>
        public static double ReplayDriftToleranceDegrees = 45.0;

        /// <summary>关键帧采样间隔（秒）—— 录制时每隔这么久记一次位置/朝向。</summary>
        public static double KeyframeIntervalSeconds = 0.5;

        // ---------------------------------------------------------------- 观测日志（P0-10）

        /// <summary>是否把重载/切换/改写/录制这些事件写进 `<实例根>/PlayerAi/Logs/PlayerAi.log`。</summary>
        public static bool LogToFile = true;

        /// <summary>单个日志文件的上限（字节），超过就滚动。</summary>
        public static int LogMaxBytes = 256 * 1024;

        /// <summary>保留几份滚动日志（含当前文件）。</summary>
        public static int LogMaxFiles = 3;

        /// <summary>内存里保留多少条最近记录（`ai.logs` 与 `ai.status` 用，不落盘也能看）。</summary>
        public static int LogRecentCapacity = 64;

        /// <summary>
        /// 可选低频安全扫描间隔（秒）。**默认 0 = 关闭**：热重载主路径是编辑器推送通知
        /// （`ai.tree.notify`），不做反复扫描；只有"有人直接手改文件、编辑器又没通知"时才需要打开它（5~10 秒足够）。
        /// </summary>
        public static double PackageWatchSeconds = 0.0;

        // ---------------------------------------------------------------- 接管范围

        /// <summary>
        /// 只接管"本端玩家"（拥有 GameWidget 视图的那个玩家）。
        /// 远端角色不能在主机端直接操作：远端操作要远端执行、本端只做复现，否则两边会不同步。
        /// </summary>
        public static bool OnlyLocalPlayer = true;

        /// <summary>世界加载后是否自动接管本端玩家（关闭则只挂组件、不产生动作）。</summary>
        public static bool AutoEnableLocalPlayer = true;

        // ---------------------------------------------------------------- 说明：示例行为不在代码里
        //
        // P0-8 起，"看 basil → 走近 3 m → 待机"这类**示例行为全部在行为树包里**
        // （出厂示例 `demo.greet.scbtpak`：服务 UpdateNearestPlayer 的 nameFilter 就是目标名，
        //   Task.MoveTo 的 acceptableRadius/timeout 就是距离与超时）。
        // 改行为改包即可，不必改代码 —— 所以这里不再有任何"示例决策参数"。

        public static void Validate()
        {
            if (MaxTransitionsPerTick < 1)
                MaxTransitionsPerTick = 1;
            if (DefaultMinDwellSeconds < 0f)
                DefaultMinDwellSeconds = 0f;
            if (TransitionLogCapacity < 4)
                TransitionLogCapacity = 4;
            if (string.IsNullOrEmpty(SafeStateId))
                SafeStateId = AiStateIds.Idle;
        }
    }
}
