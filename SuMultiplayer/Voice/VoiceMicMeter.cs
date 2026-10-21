using System;
using System.Globalization;

namespace SuMultiplayer.Voice
{
    /// <summary>
    /// 麦克风采集电平（右下角图标里的 5 格音量条）的**纯逻辑**：
    ///   · 输入：每 20 ms 一帧的 <c>VoiceActivityDetector.LastLevel</c>（该帧 mean(|sample|)，0..32767），
    ///     由 <c>Modules/VoiceChatModule.cs</c> 的采集回调 <c>ProcessCapturedFrame</c> 推进来；
    ///   · 归一化：**统一 dBFS 标尺**（与采集接口 / 设备无关）：
    ///         dbfs  = 20·log10(max(mean, 1) / 32768)          // 32768 = int16 满幅
    ///         level = (dbfs − FloorDbfs) / (FullDbfs − FloorDbfs)，clamp 0..1
    ///     标尺常量只在下面一处（<see cref="FloorDbfs"/> / <see cref="FullDbfs"/>；
    ///     dBFS 参考满幅 <see cref="FullScaleMean"/> 也只在这里）。
    ///     旧实现（2026-10-21 之前）是 FloorLevel=180 / FullLevel=3000 对**采集均值**的线性经验值
    ///     （按平板麦克风标定）；PC 麦克风正常说话 mean 常在 3000..10000（约 −20.8 .. −10.3 dBFS），
    ///     直接越过 full → 图标永远 5 格。dBFS 把不同设备的绝对电平统一到同一物理量。
    ///   · 平滑：起音快（0.6 / 帧）、释放慢（0.18 / 帧），跟手且不闪；静音帧照样推进 → 落回 0；
    ///   · 停滞：超过 <see cref="StaleAfterMs"/> 没有新帧（设备挂住 / 采集停了 / 读失败）
    ///     按 <see cref="StaleDecayPerMs"/> 每毫秒线性衰减，不会把旧电平一直显示在图标上；
    ///   · 格数：<c>round(level × 5)</c>（中点远离零），0 = 静音，5 = 满格。
    ///
    /// dBFS ↔ 采集均值（<see cref="MeanAtDbfs"/>：32768 × 10^(dBFS/20)）：
    ///     −50 dBFS ≈ 104    安静房间底噪（<see cref="FloorDbfs"/>）
    ///     −40 dBFS ≈ 328    耳语
    ///     −30 dBFS ≈ 1036   轻声说话
    ///     −20 dBFS ≈ 3277   正常说话（PC 实测均值 3000..10000）
    ///     −10 dBFS ≈ 10363  大声说话
    ///      −8 dBFS ≈ 13044  喊叫（用户实测"大声约 mean 13000"）
    ///    <see cref="FullDbfs"/> = −8.5 dBFS ≈ 12315（贴着实测的 13000）。
    /// 典型值映射（mean → 格）：104→0、200→1、1000→2、1500→3、3000→4、≥12315→5。
    ///
    /// 为什么 full 取 −8.5 而不是 −6：−6 dBFS 的采集均值是 16424，正常说话 3000（−20.8 dBFS）
    /// 只有 3 格、8000 只有 4 格；−8.5 dBFS（≈12315，与实测量的"大声约 13000"一致）让 3000 落在
    /// 4 格、13000 落在 5 格，正常说话覆盖 2..5 格。常量只此一处，改这里就改整条曲线。
    ///
    /// 不依赖 Engine / Game，可被 P:\tmp\tsverify 直接编译自检（与 Voice/VoiceHudIcon.cs 同一做法）；
    /// 生产侧唯一调用点是 <c>Modules/VoiceChatModule.cs</c>（采集回调 Push、每帧 Advance、停采 Reset）。
    /// 失败不静默：非法输入（负电平 / 时钟倒退）显式拒绝并计入 <see cref="Rejected"/>。
    /// </summary>
    public sealed class VoiceMicMeter
    {
        /// <summary>音量条格数（图标里画 5 格）。</summary>
        public const int BarCount = 5;

        /// <summary>
        /// dBFS 的参考满幅：int16 的最大幅值（2^15）。dBFS 0 = mean 32768（理论满幅均值）。
        /// 与采集接口无关，Windows / Android 的 16-bit PCM 同一定义。
        /// </summary>
        public const double FullScaleMean = 32768.0;

        /// <summary>
        /// 标尺下界（dBFS）：低于它 = 0 格。−50 dBFS ≈ mean 104，
        /// 是实测安静房间底噪（PC 麦克风 mean ~100）；再低只会把底噪画成 1 格。
        /// </summary>
        public const float FloorDbfs = -50f;

        /// <summary>
        /// 标尺上界（dBFS）：达到它 = 满格。−8.5 dBFS ≈ mean 12315，贴近实测"大声约 13000"
        /// （−8 dBFS = mean 13044）。取 −6 dBFS（mean 16424）会让正常说话只到 3 格。
        /// </summary>
        public const float FullDbfs = -8.5f;

        /// <summary>起音系数（每 20 ms 帧）：目标是变大声时向目标靠近的比例。</summary>
        public const float AttackPerFrame = 0.6f;

        /// <summary>释放系数（每 20 ms 帧）：目标是变安静时的回落比例。</summary>
        public const float ReleasePerFrame = 0.18f;

        /// <summary>停滞判定：超过这么久没有新采集帧就开始按时间衰减。</summary>
        public const long StaleAfterMs = 250;

        /// <summary>停滞衰减速率（每毫秒）：0.004 × 250 ms = 1.0，满格 250 ms 掉光。</summary>
        public const float StaleDecayPerMs = 0.004f;

        private float m_level;
        private long m_lastPushMs;
        private long m_lastAdvanceMs;
        private bool m_hasPush;

        /// <summary>平滑后的电平 0..1（麦克风关 / 停采时由 Reset 归零）。</summary>
        public float Level => m_level;

        /// <summary>点亮格数 0..5 = round(level × 5)。</summary>
        public int Bars => BarsFor(m_level);

        /// <summary>推进过的采集帧数（20 ms 一帧）。</summary>
        public long Frames { get; private set; }

        /// <summary>被显式拒绝的输入数（负电平 / 时钟倒退）——真正的失败计数，不静默吞掉。</summary>
        public long Rejected { get; private set; }

        /// <summary>因采集停滞而发生的衰减次数（每次 Advance 真的降低了电平才 +1）。</summary>
        public long StaleDecays { get; private set; }

        /// <summary>是否至少收到过一帧（没有帧时 Advance 不动作）。</summary>
        public bool HasFrames => m_hasPush;

        /// <summary>
        /// 采集均值 → dBFS（纯函数）。<c>20·log10(max(mean,1)/32768)</c>：
        /// mean=0 / 负数按 1 处理（log 不会变成 −∞），32767 ≈ −0.0003 dBFS。
        /// </summary>
        public static double ToDbfs(int meanAbs)
        {
            int mean = meanAbs < 1 ? 1 : meanAbs;
            return 20.0 * Math.Log10(mean / FullScaleMean);
        }

        /// <summary>dBFS → 采集均值（纯函数，换算表 / 自检用；与 <see cref="ToDbfs"/> 互逆）。</summary>
        public static double MeanAtDbfs(double dbfs) =>
            FullScaleMean * Math.Pow(10.0, dbfs / 20.0);

        /// <summary>
        /// 原始电平 → 0..1（统一 dBFS 映射）。纯函数：自检逐点断言，不依赖任何状态 / 计时。
        /// 低于 <see cref="FloorDbfs"/> 记 0；高于 <see cref="FullDbfs"/> 记 1。
        /// </summary>
        public static float Normalize(int meanAbs)
        {
            double span = FullDbfs - FloorDbfs;
            double level = (ToDbfs(meanAbs) - FloorDbfs) / span;
            if (level <= 0.0) return 0f;
            return level >= 1.0 ? 1f : (float)level;
        }

        /// <summary>0..1 → 0..5 格（round(level × 5)，中点远离零；NaN 记 0）。纯函数。</summary>
        public static int BarsFor(float level)
        {
            if (float.IsNaN(level) || level <= 0f) return 0;
            if (level >= 1f) return BarCount;
            int bars = (int)Math.Round(level * BarCount, MidpointRounding.AwayFromZero);
            if (bars < 0) return 0;
            return bars > BarCount ? BarCount : bars;
        }

        /// <summary>
        /// 推进一个 20 ms 采集帧。非法输入（负电平 / 时钟倒退）显式拒绝并计数，不改变电平。
        /// </summary>
        public void Push(int meanAbs, long nowMs)
        {
            if (meanAbs < 0)
            {
                Rejected++;
                return;
            }
            if (m_hasPush && nowMs < m_lastPushMs)
            {
                Rejected++;
                return;
            }
            float target = Normalize(meanAbs);
            float coefficient = target >= m_level ? AttackPerFrame : ReleasePerFrame;
            float next = m_level + (target - m_level) * coefficient;
            if (next < 0f) next = 0f;
            else if (next > 1f) next = 1f;
            m_level = next;
            m_lastPushMs = nowMs;
            m_lastAdvanceMs = nowMs;
            m_hasPush = true;
            Frames++;
        }

        /// <summary>
        /// 每帧调用：采集停滞（超过 <see cref="StaleAfterMs"/> 没有新帧）时按毫秒线性衰减。
        /// 幂等：同一 nowMs 重复调用不会再衰减；新帧到达后停滞计时从该帧重新起算。
        /// </summary>
        public void Advance(long nowMs)
        {
            if (!m_hasPush) return;
            long staleFrom = m_lastPushMs + StaleAfterMs;
            if (nowMs <= staleFrom) return;
            if (m_lastAdvanceMs < staleFrom) m_lastAdvanceMs = staleFrom;
            long decayMs = nowMs - m_lastAdvanceMs;
            if (decayMs <= 0) return;
            m_lastAdvanceMs = nowMs;
            if (m_level <= 0f) return;
            float next = m_level - StaleDecayPerMs * decayMs;
            if (next < 0f) next = 0f;
            m_level = next;
            StaleDecays++;
        }

        /// <summary>停采 / 关麦 / 离开房间：电平与停滞计时归零，帧数等累计计数保留。</summary>
        public void Reset()
        {
            m_level = 0f;
            m_hasPush = false;
            m_lastPushMs = 0;
            m_lastAdvanceMs = 0;
        }

        /// <summary>状态行 / 日志用（10 s 一次，字符串分配不敏感）。</summary>
        public string Describe() =>
            "micMeter(frames=" + Frames +
            " rejected=" + Rejected +
            " staleDecays=" + StaleDecays +
            " level=" + m_level.ToString("0.00", CultureInfo.InvariantCulture) +
            " bars=" + Bars + "/" + BarCount + ")";
    }

    /// <summary>
    /// 10 s 统计窗口（诊断 dBFS 标尺用）：收集每个真实推进到电平计的采集帧的
    /// **原始均值**（mean）与**平滑后电平 / 格数**（level / bars），输出 min/avg/max；
    /// 同时按窗口对账"采集帧数 vs 电平计帧数"，把任何不一致显式记到
    /// <see cref="FramesNotPushed"/> / <see cref="PushedWithoutCapture"/>（不静默）。
    ///
    /// 数据源全部是生产路径的累计计数（模块每 10 s 调一次 <see cref="Reconcile"/>）：
    ///   captureFrames = VoiceCounters.CaptureFrames（PumpCapture 每 20 ms 一帧）
    ///   meterFrames   = VoiceMicMeter.Frames（Push 成功推进的帧）
    ///   rejected      = VoiceMicMeter.Rejected（负电平 / 时钟倒退被拒绝）
    ///   skipped       = 采集帧到了、但闸门不允许更新电平计（voice off / 非 capturing）
    /// 正常路径 expected == meterDelta；不等就说明有帧没进电平计（或反过来），
    /// 正是"voiceCaptureFrames 涨但 micMeter.frames 不涨"的排查口径。
    /// </summary>
    public sealed class VoiceMicMeterWindow
    {
        private long m_count;
        private int m_meanMin = int.MaxValue;
        private int m_meanMax = int.MinValue;
        private long m_meanSum;
        private float m_levelMin = float.MaxValue;
        private float m_levelMax = float.MinValue;
        private double m_levelSum;
        private int m_barsMin = int.MaxValue;
        private int m_barsMax = int.MinValue;

        private long m_lastCaptureFrames;
        private long m_lastMeterFrames;
        private long m_lastRejectedFrames;
        private long m_skippedFrames;

        /// <summary>本窗口收集到的帧数（= 真实推进到电平计的帧）。</summary>
        public long Count => m_count;

        /// <summary>本窗口是否有样本（没有时不写统计值，日志只打 n=0）。</summary>
        public bool HasSamples => m_count > 0;

        /// <summary>窗口内原始采集均值的最小值（无样本时为 0）。</summary>
        public int MeanMin => m_count > 0 ? m_meanMin : 0;

        /// <summary>窗口内原始采集均值的最大值（无样本时为 0）。</summary>
        public int MeanMax => m_count > 0 ? m_meanMax : 0;

        /// <summary>窗口内原始采集均值的平均值（四舍五入；无样本时为 0）。</summary>
        public int MeanAvg => m_count > 0 ? (int)((m_meanSum + m_count / 2) / m_count) : 0;

        /// <summary>窗口内平滑后电平的最小值。</summary>
        public float LevelMin => m_count > 0 ? m_levelMin : 0f;

        /// <summary>窗口内平滑后电平的最大值。</summary>
        public float LevelMax => m_count > 0 ? m_levelMax : 0f;

        /// <summary>窗口内平滑后电平的平均值。</summary>
        public float LevelAvg => m_count > 0 ? (float)(m_levelSum / m_count) : 0f;

        /// <summary>窗口内点亮格数的最小值。</summary>
        public int BarsMin => m_count > 0 ? m_barsMin : 0;

        /// <summary>窗口内点亮格数的最大值。</summary>
        public int BarsMax => m_count > 0 ? m_barsMax : 0;

        /// <summary>累计：采集帧到了但没有推进电平计的帧数（含闸门跳过 + 对账缺失）。</summary>
        public long FramesNotPushed { get; private set; }

        /// <summary>累计：电平计推进数多于采集帧的帧数（正常应为 0；出现即接线错误）。</summary>
        public long PushedWithoutCapture { get; private set; }

        /// <summary>上一窗口的采集帧增量。</summary>
        public long LastCaptureDelta { get; private set; }

        /// <summary>上一窗口的电平计帧增量。</summary>
        public long LastMeterDelta { get; private set; }

        /// <summary>上一窗口的显式拒绝增量。</summary>
        public long LastRejectedDelta { get; private set; }

        /// <summary>上一窗口因闸门未推送的帧数。</summary>
        public long LastSkippedDelta { get; private set; }

        /// <summary>记录一个真实推进到电平计的帧（原始均值 + 平滑后电平 / 格数）。</summary>
        public void Add(int meanAbs, float level, int bars)
        {
            m_count++;
            if (meanAbs < m_meanMin) m_meanMin = meanAbs;
            if (meanAbs > m_meanMax) m_meanMax = meanAbs;
            m_meanSum += meanAbs;
            if (level < m_levelMin) m_levelMin = level;
            if (level > m_levelMax) m_levelMax = level;
            m_levelSum += level;
            if (bars < m_barsMin) m_barsMin = bars;
            if (bars > m_barsMax) m_barsMax = bars;
        }

        /// <summary>采集帧到了、但闸门（voice=on &amp;&amp; capturing）不允许更新电平计。</summary>
        public void NoteSkipped() => m_skippedFrames++;

        /// <summary>
        /// 窗口对账：把"采集帧 / 电平计帧 / 拒绝 / 闸门跳过"的累计值对齐，算出本窗口的
        /// 缺失或多余帧数。返回本窗口的 missing（&gt;0 = 有帧没进电平计；&lt;0 = 电平计多推）。
        /// 累计计数单调递增；重置模块不会清零（模块实例重建时窗口一起重建）。
        /// </summary>
        public long Reconcile(long captureFrames, long meterFrames, long rejectedFrames)
        {
            long captureDelta = captureFrames - m_lastCaptureFrames;
            long meterDelta = meterFrames - m_lastMeterFrames;
            long rejectedDelta = rejectedFrames - m_lastRejectedFrames;
            long skippedDelta = m_skippedFrames;
            long expected = captureDelta - rejectedDelta - skippedDelta;
            long missing = expected - meterDelta;
            if (missing > 0) FramesNotPushed += missing;
            else if (missing < 0) PushedWithoutCapture += -missing;
            LastCaptureDelta = captureDelta;
            LastMeterDelta = meterDelta;
            LastRejectedDelta = rejectedDelta;
            LastSkippedDelta = skippedDelta;
            m_skippedFrames = 0;
            m_lastCaptureFrames = captureFrames;
            m_lastMeterFrames = meterFrames;
            m_lastRejectedFrames = rejectedFrames;
            return missing;
        }

        /// <summary>
        /// 清空本窗口的统计值（mean / level / bars 的样本）。
        /// 累计对账计数（<see cref="FramesNotPushed"/> / <see cref="PushedWithoutCapture"/>）
        /// 与上一窗口增量（Last*）保留，日志之后仍可查。
        /// </summary>
        public void ResetWindow()
        {
            m_count = 0;
            m_meanMin = int.MaxValue;
            m_meanMax = int.MinValue;
            m_meanSum = 0;
            m_levelMin = float.MaxValue;
            m_levelMax = float.MinValue;
            m_levelSum = 0.0;
            m_barsMin = int.MaxValue;
            m_barsMax = int.MinValue;
        }

        /// <summary>日志用（10 s 一次）：mean(min/avg/max) + level(min/avg/max) + bars(min/max)。</summary>
        public string DescribeWindow()
        {
            if (m_count == 0) return "micWin(n=0)";
            return "micWin(n=" + m_count +
                " mean(min/avg/max)=" + MeanMin + "/" + MeanAvg + "/" + MeanMax +
                " level(min/avg/max)=" +
                m_levelMin.ToString("0.00", CultureInfo.InvariantCulture) + "/" +
                LevelAvg.ToString("0.00", CultureInfo.InvariantCulture) + "/" +
                m_levelMax.ToString("0.00", CultureInfo.InvariantCulture) +
                " bars(min/max)=" + m_barsMin + "/" + m_barsMax + ")";
        }
    }
}
