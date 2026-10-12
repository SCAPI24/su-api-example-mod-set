namespace Channel
{
    /// <summary>
    /// 信道元件的**纯决策逻辑**（不碰引擎类型，可离线真值表测试）。
    ///
    /// Bottom 四态（量化：v = Round(邻格输出 × 15)，与 memory bank 同款）：
    ///   未接线          → 默认激活（接入通道）
    ///   v ≥ 8（高）     → 激活（接入通道）
    ///   1 ≤ v ≤ 7（低） → **写入** X/Y 到 data bit 3–10（边沿触发），且不接入通道
    ///   v = 0           → 不接入通道
    /// </summary>
    public static class ChannelLogic
    {
        /// <summary>写入电平区间（memory bank 的写入档：0.1V–0.7V，即 1..7 档）。</summary>
        public const int WriteBandMin = 1;
        public const int WriteBandMax = 7;

        /// <summary>高电平门限（≥8 档 = 0.8V）。</summary>
        public const int HighLevelMin = 8;

        public struct Inputs
        {
            public bool BottomConnected;
            public int BottomLevel;     // 0..15
            public int LowNibble;       // Right（X），0..15
            public int HighNibble;      // Left（Y），0..15
            public int Channel;         // 当前通道号 0..255
            public bool WriteAllowed;   // 写入边沿锁存：必须先离开写入档
        }

        public struct Outputs
        {
            public bool Write;          // 本步是否写通道号
            public int NewChannel;      // 写入值（X 低位 + Y 高位）
            public bool ChannelActive;  // 是否接入通道总线
            public bool WriteAllowed;   // 下一状态的边沿锁存
            public bool LeaveChannel;   // 是否撤掉自己的总线贡献（不接入，或刚写过）
        }

        public static Outputs Decide(Inputs i)
        {
            bool writeBand = i.BottomConnected
                && i.BottomLevel >= WriteBandMin
                && i.BottomLevel <= WriteBandMax;
            bool channelActive = !i.BottomConnected || i.BottomLevel >= HighLevelMin;

            int newChannel = (i.LowNibble & 0xF) | ((i.HighNibble & 0xF) << 4);
            bool write = writeBand && i.WriteAllowed && newChannel != i.Channel;

            Outputs o = default(Outputs);
            o.Write = write;
            o.NewChannel = newChannel;
            o.ChannelActive = channelActive;
            o.WriteAllowed = !writeBand;
            o.LeaveChannel = !channelActive || write;
            return o;
        }
    }
}
