using System;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 第三分屏相机的种类（纯值，不依赖 Game：自检直接编译本文件）。
    /// </summary>
    public enum ViewCameraKind
    {
        /// <summary>
        /// 不是本类认识的镜像相机（null / LoadingCamera / IntroCamera，或引擎版本变化后新增的类型）。
        /// </summary>
        Unknown = 0,
        /// <summary>引擎 <c>FppCamera</c>：读 Target 的 EyePosition / EyeRotation。</summary>
        FirstPerson = 1,
        /// <summary>引擎 <c>TppCamera</c>：按 Target 的 EyeRotation 环绕 + 地形遮挡收缩。</summary>
        ThirdPerson = 2,
        /// <summary>引擎 <c>DeathCamera</c>：客户端角色死亡时的优先表现。</summary>
        Death = 3,
        /// <summary>引擎 <c>OrbitCamera</c>：环绕 Target 的自由轨道。</summary>
        Orbit = 4,
        /// <summary>引擎 <c>FixedCamera</c>：冻结在切换那一刻的视点。</summary>
        Fixed = 5
    }

    /// <summary>
    /// 第三分屏"客户端视角模式 → 相机"的**纯逻辑**（无静态状态、无引擎类型）。
    ///
    /// 产品口径（2026-10-21）：客户端按 V / 视角按钮切任意一个视图时，主机第三屏必须显示
    /// **同一个模式**；死亡时 <c>DeathCamera</c> 优先；复活后回到客户端最后上报的模式。
    ///
    /// 引擎事实：
    ///   · 客户端 V 键 / 视角按钮在 <c>Fpp → Tpp → Orbit → Fixed → Fpp</c> 之间循环
    ///     （Source: Survivalcraft/Game/ComponentGui.cs:645-670）——本协议按 ActiveCamera 的
    ///     **实际类型**上报 1..4（Fpp / Tpp / Orbit / Fixed），其余相机（Death / Intro / Loading）
    ///     属于非玩家选择，上报 0（不改变），主机保留最后已知模式；
    ///   · <c>TppCamera.Update</c> 用 <c>Target.ComponentCreatureModel.EyeRotation</c> 算环绕位置
    ///     并做地形遮挡收缩（Source: Survivalcraft/Game/TppCamera.cs:27-89），主机第三屏的
    ///     Target 就是客户端 <c>ComponentPlayer</c>，所以环绕方向 / 遮挡自动跟随，不需要同步角度；
    ///   · <c>FppCamera.Update</c> 读同一个 Target 的 EyePosition / EyeRotation
    ///     （Source: Survivalcraft/Game/FppCamera.cs:21-44）；
    ///   · <c>OrbitCamera.Update</c> 以 <c>GameWidget.Target.ComponentBody</c> 为环绕中心，角度 / 距离
    ///     由**本视图自己的** <c>GameWidget.PlayerData.ComponentPlayer.ComponentInput</c> 驱动
    ///     （Source: Survivalcraft/Game/OrbitCamera.cs:26-47），所以第三屏能显示"客户端角色的轨道
    ///     视角"，但环绕角是主机本地输入（与客户端自己的角度无关 —— 那是客户端的输入状态，
    ///     不是引擎按 Target 派生的状态）；
    ///   · <c>FixedCamera.Update</c> 是空实现，<c>Activate</c> 只把视点冻结在上一台相机的位置 /
    ///     朝向（Source: Survivalcraft/Game/FixedCamera.cs:16-22），所以第三屏切到它时冻结的是
    ///     **主机这一视图**切换那一刻的视点；客户端自己的固定视点位置不在本协议里（见
    ///     <c>doc/MIRROR-CHARACTER.md</c> 的视角模式表）。
    ///
    /// 调用方（<c>Engine/MirrorCharacterEngine.cs</c>）负责把引擎相机对象映射成
    /// <see cref="ViewCameraKind"/>、按本类的目标写 <c>GameWidget.ActiveCamera</c>，并计数。
    /// </summary>
    public static class ViewCameraPolicy
    {
        /// <summary>客户端没有上报 / 上报了本协议不表达的模式：不改变主机相机。</summary>
        public const int ModeUnknown = 0;

        /// <summary>第一人称（客户端 ActiveCamera = FppCamera）。</summary>
        public const int ModeFpp = 1;

        /// <summary>第三人称（客户端 ActiveCamera = TppCamera）。</summary>
        public const int ModeTpp = 2;

        /// <summary>环绕轨道（客户端 ActiveCamera = OrbitCamera）。</summary>
        public const int ModeOrbit = 3;

        /// <summary>固定视点（客户端 ActiveCamera = FixedCamera）。</summary>
        public const int ModeFixed = 4;

        /// <summary>没有拒绝过任何模式时 <c>viewCameraModeRejectedBy</c> 的取值。</summary>
        public const string RejectedByNone = "none";

        /// <summary>wire 上表达玩家选择的模式范围：1（Fpp）.. 4（Fixed）；0 另算"不改变"。</summary>
        public static bool IsKnownMode(int mode) => mode >= ModeFpp && mode <= ModeFixed;

        /// <summary>把 wire 模式映射成相机种类；0 / 非法值 → <see cref="ViewCameraKind.Unknown"/>。</summary>
        public static ViewCameraKind KindOf(int mode)
        {
            switch (mode)
            {
                case ModeFpp: return ViewCameraKind.FirstPerson;
                case ModeTpp: return ViewCameraKind.ThirdPerson;
                case ModeOrbit: return ViewCameraKind.Orbit;
                case ModeFixed: return ViewCameraKind.Fixed;
                default: return ViewCameraKind.Unknown;
            }
        }

        /// <summary>把相机种类映射回 wire 模式；Death / Unknown → 0（不改变）。</summary>
        public static int ModeOf(ViewCameraKind kind)
        {
            switch (kind)
            {
                case ViewCameraKind.FirstPerson: return ModeFpp;
                case ViewCameraKind.ThirdPerson: return ModeTpp;
                case ViewCameraKind.Orbit: return ModeOrbit;
                case ViewCameraKind.Fixed: return ModeFixed;
                default: return ModeUnknown;
            }
        }

        /// <summary>
        /// 客户端采集：按本机 <c>GameWidget.ActiveCamera</c> 的实际类型判定上报值。
        /// 四个 <c>is</c> 由调用方对引擎相机对象求值；一个都不成立 → 0（不改变：死亡相机、
        /// Intro / Loading，以及未来新增的相机类型）。
        /// </summary>
        public static int ModeOfCameraTypes(bool isFpp, bool isTpp, bool isOrbit, bool isFixed)
        {
            if (isFpp) return ModeFpp;
            if (isTpp) return ModeTpp;
            if (isOrbit) return ModeOrbit;
            if (isFixed) return ModeFixed;
            return ModeUnknown;
        }

        /// <summary>
        /// 第三分屏的**合法相机集合**：Fpp / Tpp / Orbit / Fixed / Death。Tpp·Orbit·Fixed 都算
        /// "客户端自己选的视图"（旧逻辑只认 Fpp / Death，会把刚切好的第三人称强制切回第一人称）；
        /// Unknown（Loading / Intro / null）才需要按客户端最后模式校正。
        /// </summary>
        public static bool IsMirrorCamera(ViewCameraKind kind) =>
            kind == ViewCameraKind.FirstPerson || kind == ViewCameraKind.ThirdPerson ||
            kind == ViewCameraKind.Orbit || kind == ViewCameraKind.Fixed ||
            kind == ViewCameraKind.Death;

        /// <summary>
        /// 活着时**可以保留 / 恢复**的相机（不是死亡相机、也不是"被引擎改成 Loading"）：
        /// Fpp / Tpp / Orbit / Fixed。
        /// </summary>
        public static bool IsPlayableCamera(ViewCameraKind kind) =>
            kind == ViewCameraKind.FirstPerson || kind == ViewCameraKind.ThirdPerson ||
            kind == ViewCameraKind.Orbit || kind == ViewCameraKind.Fixed;

        /// <summary>
        /// 选目标相机：
        ///   ① 客户端角色死亡 → <see cref="ViewCameraKind.Death"/>（优先于任何模式）；
        ///   ② 活着且客户端最后上报了 1..4 → 对应模式（复活后同样走这一条）；
        ///   ③ 模式未知（从没上报）→ 当前已是 Fpp / Tpp / Orbit / Fixed 就保持它（0 = 不改变），
        ///      否则第一人称（绑定默认值，与旧行为一致）。
        /// 返回值与 <paramref name="current"/> 相同 = 不需要写相机。
        /// </summary>
        public static ViewCameraKind Select(ViewCameraKind current, int lastKnownMode,
            bool sourceDead)
        {
            if (sourceDead) return ViewCameraKind.Death;
            ViewCameraKind wanted = KindOf(lastKnownMode);
            if (wanted != ViewCameraKind.Unknown) return wanted;
            return IsPlayableCamera(current) ? current : ViewCameraKind.FirstPerson;
        }

        /// <summary>诊断词：fpp / tpp / death / orbit / fixed / unknown。</summary>
        public static string Name(ViewCameraKind kind)
        {
            switch (kind)
            {
                case ViewCameraKind.FirstPerson: return "fpp";
                case ViewCameraKind.ThirdPerson: return "tpp";
                case ViewCameraKind.Death: return "death";
                case ViewCameraKind.Orbit: return "orbit";
                case ViewCameraKind.Fixed: return "fixed";
                default: return "unknown";
            }
        }

        /// <summary>诊断词：fpp / tpp / orbit / fixed / unknown（0 = 客户端没有表达模式）。</summary>
        public static string Name(int mode) => Name(KindOf(mode));

        /// <summary>
        /// 状态行里的相机计数一行（进 <c>mirror-character</c>；键固定，未启用时是一串 0）。
        ///   · viewCameraMode          客户端最后上报并生效的模式（0 未知 / 1 Fpp / 2 Tpp /
        ///                             3 Orbit / 4 Fixed）；
        ///   · viewCameraModeChanges   模式变化次数（含 0 → 1..4 的首次上报）；
        ///   · viewCameraApply         按模式成功写 <c>ActiveCamera</c> 的次数（不含死亡相机）；
        ///   · viewCameraApplyFailures 写相机失败次数（缺相机 / setter 抛异常 / 引擎拒绝）；
        ///   · viewCameraModeRejectedBy 最近一次拒绝原因（未拒绝 = none）；
        ///   · viewCameraModeRejects   模式被拒绝的次数（非法 clientId / 非法值 / 绑在别的客户端）。
        /// 当前实际相机在 <c>viewCameraActual</c>（fpp / tpp / orbit / fixed / death / none）。
        /// </summary>
        public static string DescribeCounters(int mode, long changes, long apply, long failures,
            string rejectedBy, long rejects)
        {
            int shownMode = IsKnownMode(mode) ? mode : ModeUnknown;
            return "viewCameraMode=" + shownMode +
                " viewCameraModeChanges=" + changes +
                " viewCameraApply=" + apply +
                " viewCameraApplyFailures=" + failures +
                " viewCameraModeRejectedBy=" +
                (string.IsNullOrEmpty(rejectedBy) ? RejectedByNone : rejectedBy) +
                " viewCameraModeRejects=" + rejects;
        }
    }
}
