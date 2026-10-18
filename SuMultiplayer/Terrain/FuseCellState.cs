using System;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 引信状态的**格子 data 编码**（纯托管；P:\tmp\tsverify 直接编译本文件做离线自检）。
    ///
    /// 引擎事实（Survivalcraft/Game/SubsystemExplosivesBlockBehavior.cs）：
    ///   · 引信**不在格子里**：<c>m_explosiveDataByPoint</c>（:33）里的
    ///     <c>ExplosiveData.TimeToExplosion</c>（:16）是主机私有状态；
    ///     <c>IgniteFuse</c>（:41-55）只往表里加一条，**不写任何格子**；
    ///     <c>Update</c>（:57-92）每帧扣时间、到 0 才 <c>TryExplodeBlock</c>，也不写格子；
    ///   · 主机能看到的"引信在烧"只是 <c>FuseParticleSystem</c>（:69-73，且只对
    ///     <c>GunpowderKegBlock</c> 创建）。它是**本端表现**，不会随 CellChange 下发
    ///     ⇒ 客户端在爆炸前一直看到没点着的桶。
    ///
    /// 所以联机时主机必须把引信状态**变成格子状态**：在火药桶格子的 data 高位写入
    /// "剩余引信 tick"（1/4 秒一格）。客户端只按这个值做表现，**绝不自己倒计时**。
    ///
    /// 为什么火药桶可以写 data 高位：
    ///   · <c>GunpowderKegBlock</c> 完全不读 data —— 网格 (GunpowderKegBlock.cs:47-51)、
    ///     碰撞箱 (:58)、电连接 (:63-80) 都不用它，只有 6 种桶（小/中/大 × 普通/燃烧）
    ///     继承它，行为清单见 Pak/BlocksData.csv:137-142；
    ///   · <c>DetonatorBlock</c> 的 data 0..5 是**挂载面**（DetonatorBlock.cs:41-44 <c>&amp; 7</c>），
    ///     且 :54-62 用 <c>data &lt; m_blockMeshesByData.Length(6)</c> 决定画不画 ⇒ 写高位会让
    ///     起爆器**不渲染**；引擎也**不给起爆器建引信粒子**（SubsystemExplosivesBlockBehavior.cs:69
    ///     只判 <c>GunpowderKegBlock</c>）⇒ 起爆器引信不能走格子 data（见主机外壳里的显式计数/日志）。
    ///
    /// 编码：data 第 8..13 位 = 剩余 tick（0 = 没点着；1..63 = 剩余 0.25..15.75 秒）。
    /// 火药桶最短引信 6 秒（SubsystemExplosivesBlockBehavior.cs:46 <c>Float(6f, 7f)</c>）
    /// ⇒ 最多 28 tick，6 位足够。
    /// </summary>
    public static class FuseCellState
    {
        /// <summary>引信 tick 在 data 里的起始位（低 8 位留给方块自己的语义）。</summary>
        public const int Shift = 8;
        /// <summary>引信 tick 占多少位。</summary>
        public const int Bits = 6;
        /// <summary>引信 tick 的位掩码（已移到 data 位置）。</summary>
        public const int Mask = ((1 << Bits) - 1) << Shift;
        /// <summary>能表示的最大倒计时（63 × 0.25s = 15.75s）。</summary>
        public const int MaxTicks = (1 << Bits) - 1;
        /// <summary>一个 tick 的秒数（4 Hz 更新，与主机的逐帧扣时相比只做量化、不做模拟）。</summary>
        public const float SecondsPerTick = 0.25f;

        /// <summary>这一格 data 是否表示"引信正在烧"。</summary>
        public static bool IsLit(int data) => (data & Mask) != 0;

        /// <summary>这一格 data 里的剩余 tick（0 = 没点着）。</summary>
        public static int Ticks(int data) => (data & Mask) >> Shift;

        /// <summary>写入剩余 tick，保留 data 的其它位（<paramref name="ticks"/> 会被夹到 0..63）。</summary>
        public static int WithTicks(int data, int ticks)
        {
            if (ticks < 0) ticks = 0;
            else if (ticks > MaxTicks) ticks = MaxTicks;
            return (data & ~Mask) | (ticks << Shift);
        }

        /// <summary>清掉引信位（熄灭 / 卸下 / 区块丢弃），保留 data 的其它位。</summary>
        public static int Clear(int data) => data & ~Mask;

        /// <summary>
        /// 剩余秒数 → 格子里的 tick 数。**至少 1**：已经到 0（本帧该炸）时保持 1，
        /// 不再制造一次 "1 → 0" 的额外 CellChange —— 爆炸自己的格子改动已经会下发。
        /// </summary>
        public static int TicksFromSeconds(float seconds)
        {
            if (seconds <= 0f) return 1;
            int ticks = (int)Math.Ceiling(seconds / SecondsPerTick);
            if (ticks < 1) return 1;
            return ticks > MaxTicks ? MaxTicks : ticks;
        }

        /// <summary>tick → 剩余秒数（仅用于日志/自检换算，客户端不据此倒计时）。</summary>
        public static float Seconds(int data) => Ticks(data) * SecondsPerTick;
    }
}
