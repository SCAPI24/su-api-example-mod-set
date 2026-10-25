using System;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 世界控制的四个动作（位标志；语义 = 引擎创意按钮，Source:
    /// Survivalcraft/Game/ComponentGui.cs:679-737）。客户端按按钮**不在本地执行**：
    /// 意图经 <c>WORLD_CONTROL_REQUEST</c>（id 112）发往主机，主机在权威世界上
    /// 落地后由既有 WorldInfo 快照（~2 Hz）广播给所有端；结果回执
    /// <c>WORLD_CONTROL_RESULT</c>（id 113）让请求者显示与引擎一致的提示。
    /// 参照实现：Mod/ScMultiplayer/Func/Component/SuComponentInput.cs（旧联机 mod
    /// 在替换的 ComponentInput 里捕获同一组按钮并发 WorldControlRequest）。
    /// </summary>
    [Flags]
    public enum WorldControlActions
    {
        None = 0,
        /// <summary>切换降水（引擎 ManualPrecipitationStart / End）。</summary>
        Precipitation = 1 << 0,
        /// <summary>切换雾（引擎 ManualFogStart / End）。</summary>
        Fog = 1 << 1,
        /// <summary>在请求者眼睛位置沿视线劈一次闪电（引擎 ManualLightingStrike）。</summary>
        Lightning = 1 << 2,
        /// <summary>时间跳到最近的 晨/午/暮/夜（引擎区间判定 + TimeOfDayOffset）。</summary>
        TimeOfDay = 1 << 3,
    }

    /// <summary>时间动作的结果标签（数值与协议 WorldControlResult.time_result 一致，不要改）。</summary>
    public enum WorldControlTimeResult
    {
        None = 0,
        Dawn = 1,
        Noon = 2,
        Dusk = 3,
        Midnight = 4,
    }

    /// <summary>主机执行结果（模块只搬运，不理解引擎语义）。</summary>
    public struct WorldControlOutcome
    {
        public bool Accepted;
        /// <summary>拒绝原因（可定位、给人看；accepted=false 时必填）。</summary>
        public string RejectReason;
        public bool PrecipitationStarted;
        public bool FogStarted;
        public WorldControlTimeResult TimeResult;
        /// <summary>实际应用的动作位（与请求位语义一致）。</summary>
        public WorldControlActions Applied;
    }

    /// <summary>
    /// 客户端提交入口（实现 = Modules/WorldControlModule.cs；引擎侧输入外壳调用）。
    /// 返回 true = 请求已发出（或被明确限流计数），调用方**必须**消费本地点击，
    /// 防止 ComponentGui 再按原生路径本地执行；返回 false = 未发送（无会话 / 通道
    /// 不可用），调用方放行原生行为。
    /// </summary>
    public interface IWorldControlRequestSink
    {
        /// <summary>提交一次世界控制请求。闪电动作必须有全部有限的瞄准分量。</summary>
        bool TrySubmit(WorldControlActions actions,
            bool hasAim,
            float aimX, float aimY, float aimZ,
            float dirX, float dirY, float dirZ);
    }

    /// <summary>
    /// 引擎侧端口（实现 = Engine/WorldControlEngine.cs；组合根注册后模块可见）。
    /// 模块层通过它触达引擎：每帧挂接输入外壳、主机在权威世界执行、客户端显示结果。
    /// </summary>
    public interface IWorldControlPort
    {
        /// <summary>每帧一次（模块 Tick 调用）：客户端角色时挂接输入外壳，否则摘除。</summary>
        void Pump();

        /// <summary>模块在 Attach 时写入自己，Detach 时写 null（客户端输入 → 请求的唯一入口）。</summary>
        void SetRequestSink(IWorldControlRequestSink sink);

        /// <summary>
        /// 主机：在权威世界上执行动作（引擎同款调用：ManualPrecipitationStart/End、
        /// ManualFogStart/End、ManualLightingStrike、TimeOfDayOffset 区间判定）。
        /// requestingClientId 只用于日志归属。
        /// </summary>
        WorldControlOutcome ExecuteHost(WorldControlActions actions, bool hasAim,
            float aimX, float aimY, float aimZ,
            float dirX, float dirY, float dirZ, int requestingClientId);

        /// <summary>
        /// 客户端：显示与引擎一致的结果提示（"Precipitation On"/"Dawn"…；
        /// 拒绝时显示原因）。找不到本机 ComponentGui 时计一次明确失败，不静默。
        /// </summary>
        void ShowClientResult(bool accepted, string rejectReason, WorldControlActions applied,
            bool precipitationStarted, bool fogStarted, WorldControlTimeResult timeResult);
    }
}
