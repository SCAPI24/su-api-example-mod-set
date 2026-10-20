using System;
using Engine;
using Google.Protobuf;
using P = SuMultiplayer.Protocol.Ambient;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 循环环境音（<c>Audio/Fire</c> / <c>Audio/Water</c> / <c>Audio/Magma</c>）同步的
    /// **纯逻辑层**（不引用 Game）：边界常量、音量值对象、线上编解码 / 严格校验、
    /// 有界格子扫描（返回距离 / 火焰强度，而不是直接写引擎字段）。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemAmbientSounds.cs）：
    ///   · 三条循环 Sound 由 <c>SubsystemAudio.CreateSound</c> 创建（:69-77），
    ///     **从不进入** <c>m_queuedSounds</c>，所以全局声音中继（队列差集）看不到它们；
    ///   · <c>Update</c> 用每一帧的 <c>FireSoundVolume / WaterSoundVolume / MagmaSoundVolume</c>
    ///     驱动音量（:30/39/48），然后在末尾把三个字段清 0（:61-63）；
    ///   · 这三个字段由方块行为 / <c>ComponentOnFire</c> 每帧写（见下面各 setter 的源码行），
    ///     而那些行为在客户端副本上被闸门挡住 ⇒ 客户端本地恒为 0。
    ///
    /// 本文件只做"主机按客户端化身位置算出同样的音量"所需的纯计算与协议：
    ///   · <see cref="AmbientSoundScanner"/>：有界半径内读格子，返回最近流体距离、
    ///     最近营火距离、火焰逆平方强度和风（与引擎公式一致）；
    ///   · <see cref="AmbientSoundCodec"/>：一条消息 4 个 float + revision / tick / target；
    ///   · <see cref="AmbientVolumes"/>：客户端要施加的最终值（水 / 岩浆 / 营火 0..1；
    ///     火焰是多个火格的逆平方和，可以 &gt;1，由引擎 Sound.Volume 夹到 0..1）。
    ///
    /// 无静态状态；不引用 Game；不写格 / 不生成实体 / 不调用 AudioManager。
    /// P:\tmp\tsverify 与生产构建编译同一份源码。
    /// </summary>
    public static class AmbientSoundPolicy
    {
        /// <summary>主机向每个客户端发送环境音状态的最小间隔：5 Hz（200 ms）。</summary>
        public const int SendIntervalMs = 200;

        /// <summary>只有任一音量变化超过该阈值才发送（"meaningful change"）。</summary>
        public const float ChangeEpsilon = 0.01f;

        /// <summary>
        /// 流体扫描半径：与引擎 <c>CalculateDistanceToFluid(p, 8, ...)</c> 完全一致
        /// （Source: Survivalcraft/Game/SubsystemMagmaBlockBehavior.cs:36 /
        /// SubsystemWaterBlockBehavior.cs:31）。
        /// </summary>
        public const int FluidRadius = 8;

        /// <summary>火焰 / 营火扫描半径（有界 12 格；任务允许 8..12）。</summary>
        public const int FireRadius = 12;

        /// <summary>一次扫描最多访问的格子数（25×25×25 = 15625；仅作硬上界保护）。</summary>
        public const int MaxScanCells = 32768;

        /// <summary>一次扫描最多累计的火焰格数（超出后停止累加并明确标记）。</summary>
        public const int MaxFireCells = 512;

        /// <summary>火焰音量公式的系数（Source: SubsystemFireBlockBehavior.cs:164）。</summary>
        public const float FireVolumeFactor = 0.75f;

        /// <summary>
        /// 火焰原始音量的安全上界。引擎的 <c>FireSoundVolume</c> 是多个火格的逆平方和
        /// （可能 &gt; 1），最终由 <c>Sound.Volume</c> 夹到 0..1；客户端必须拿到**未夹**的
        /// 原始值才能在不同 <c>SettingsManager.SoundsVolume</c> 下与主机一致。
        /// 64 只是防御性上界（极端火海），正常场景远低于它。
        /// </summary>
        public const float MaxFireVolume = 64f;

        /// <summary>引擎内容 id（Source: FireBlock.Index / CampfireBlock.Index）。</summary>
        public const int FireContents = 104;
        public const int CampfireContents = 209;

        /// <summary>燃料营火 data &gt; 0 才在燃烧（Source: SubsystemCampfireBlockBehavior.cs:135）。</summary>
        public const int CampfireMinData = 1;

        /// <summary>
        /// 复刻 <c>SubsystemAudio.CalculateVolume</c>（Source: Survivalcraft/Game/SubsystemAudio.cs:157-165）。
        /// 引擎适配层优先调用真实的 <c>SubsystemAudio.CalculateVolume</c>；本副本只用于纯逻辑自检。
        /// </summary>
        public static float CalculateVolume(float distance, float minDistance,
            float rolloffFactor = 2f)
        {
            if (distance > minDistance)
            {
                return minDistance / (minDistance +
                    MathUtils.Max(rolloffFactor * (distance - minDistance), 0f));
            }
            return 1f;
        }

        /// <summary>
        /// 火焰强度 → 音量（Source: SubsystemFireBlockBehavior.cs:164:
        /// <c>m_fireSoundVolume = 0.75f * m_fireSoundIntensity</c>）。有效音量由
        /// <c>Sound.Volume</c> 夹到 0..1（Source: Engine/Engine/Audio/BaseSound.cs:28-38）。
        /// </summary>
        public static float FireVolume(float intensity)
        {
            if (!(intensity > 0f)) return 0f;
            float volume = FireVolumeFactor * intensity;
            if (float.IsNaN(volume) || float.IsInfinity(volume)) return MaxFireVolume;
            return MathUtils.Min(volume, MaxFireVolume);
        }
    }

    /// <summary>
    /// 一条 AMBIENT_SOUNDS_STATE 携带的四个最终音量：水 / 岩浆 / 营火在 0..1；
    /// 火焰允许大于 1（引擎的 FireSoundVolume 是多个火格的逆平方和）。
    /// </summary>
    public readonly struct AmbientVolumes
    {
        public AmbientVolumes(float fire, float water, float magma, float campfire)
        {
            Fire = fire;
            Water = water;
            Magma = magma;
            Campfire = campfire;
        }

        public float Fire { get; }
        public float Water { get; }
        public float Magma { get; }
        public float Campfire { get; }

        public static AmbientVolumes Zero => default;

        /// <summary>
        /// 客户端真正写进引擎的三个字段（营火在引擎里也走 FireSoundVolume，
        /// Source: SubsystemCampfireBlockBehavior.cs:62 / ComponentOnFire.cs:83）。
        /// </summary>
        public float EngineFire => MathUtils.Max(Fire, Campfire);

        public float Max => MathUtils.Max(MathUtils.Max(Fire, Water), MathUtils.Max(Magma, Campfire));

        public bool IsZero => Fire == 0f && Water == 0f && Magma == 0f && Campfire == 0f;

        /// <summary>
        /// 线上 / 引擎边界：水 / 岩浆 / 营火是 [0,1] 的有限数；火焰允许
        /// [0,<see cref="AmbientSoundPolicy.MaxFireVolume"/>] —— 引擎的 FireSoundVolume
        /// 是多个火格的逆平方和，可以大于 1，最终由 Sound.Volume 夹到 0..1。
        /// </summary>
        public bool IsValid()
        {
            return IsFire(Fire) && IsUnit(Water) && IsUnit(Magma) && IsUnit(Campfire);
        }

        private static bool IsUnit(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) &&
                value >= 0f && value <= 1f;
        }

        private static bool IsFire(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) &&
                value >= 0f && value <= AmbientSoundPolicy.MaxFireVolume;
        }

        /// <summary>任一音量差超过 <paramref name="epsilon"/> = 值得发一条新消息。</summary>
        public bool IsMeaningfulChange(in AmbientVolumes previous, float epsilon)
        {
            if (epsilon < 0f) epsilon = 0f;
            return Math.Abs(Fire - previous.Fire) > epsilon ||
                Math.Abs(Water - previous.Water) > epsilon ||
                Math.Abs(Magma - previous.Magma) > epsilon ||
                Math.Abs(Campfire - previous.Campfire) > epsilon;
        }

        public string Describe() =>
            "fire=" + Fire.ToString("F3") +
            " campfire=" + Campfire.ToString("F3") +
            " water=" + Water.ToString("F3") +
            " magma=" + Magma.ToString("F3");
    }

    /// <summary>主机侧读格子的最小表面（实现 = Engine/AmbientSoundReplicaEngine.cs 的 Terrain 源）。</summary>
    public interface IAmbientCellSource
    {
        /// <summary>
        /// 返回格子的 packed value（contents = value &amp; 0x3FF，data = value &gt;&gt;&gt; 14）。
        /// 未加载的区块 / 越界必须返回 0（引擎 <c>Terrain.GetCellValueFast</c> 的语义），
        /// 绝不能为了读一格而加载区块。
        /// </summary>
        int GetCellValue(int x, int y, int z);
    }

    /// <summary>
    /// 哪些内容 id 算"同一个流体"：引擎用 <c>FluidBlock.IsTheSameFluid</c>，它把
    /// <c>WaterBlock</c> / <c>MagmaBlock</c> 的子类（水草 / 海参）也算进去
    /// （Source: Survivalcraft/Game/FluidBlock.cs:62-65）。主机适配层按
    /// <c>BlocksManager.Blocks[i] is WaterBlock / MagmaBlock</c> 一次性建表。
    /// </summary>
    public sealed class AmbientContentMask
    {
        private readonly bool[] m_water;
        private readonly bool[] m_magma;

        public AmbientContentMask(bool[] water, bool[] magma)
        {
            m_water = water;
            m_magma = magma;
        }

        public bool HasAnyWater => m_water != null && m_water.Length > 0;
        public bool HasAnyMagma => m_magma != null && m_magma.Length > 0;
        public int Length => Math.Max(m_water?.Length ?? 0, m_magma?.Length ?? 0);

        public bool IsWater(int contents) =>
            m_water != null && contents >= 0 && contents < m_water.Length && m_water[contents];

        public bool IsMagma(int contents) =>
            m_magma != null && contents >= 0 && contents < m_magma.Length && m_magma[contents];
    }

    /// <summary>一次有界扫描的原始结果（距离 / 强度；音量换算由引擎适配层用真实公式完成）。</summary>
    public struct AmbientScanResult
    {
        public bool HasMagma;
        public float MagmaDistance;
        public bool HasWater;
        public float WaterDistance;
        public bool HasCampfire;
        public float CampfireDistance;
        public float FireIntensity;
        public int FireCells;
        public int CampfireCells;
        public int WaterCells;
        public int MagmaCells;
        public int CellsRead;
        public bool FireCellCapHit;
        public bool CellCapHit;

        public string Describe() =>
            "cells=" + CellsRead +
            " fireCells=" + FireCells + " capHit=" + FireCellCapHit +
            " campfireCells=" + CampfireCells +
            " waterCells=" + WaterCells +
            " magmaCells=" + MagmaCells +
            " fireIntensity=" + FireIntensity.ToString("F3") +
            " waterDistance=" + (HasWater ? WaterDistance.ToString("F2") : "none") +
            " magmaDistance=" + (HasMagma ? MagmaDistance.ToString("F2") : "none") +
            " campfireDistance=" + (HasCampfire ? CampfireDistance.ToString("F2") : "none");
    }

    /// <summary>
    /// 主机侧有界扫描：以客户端化身位置为中心，一次遍历同时找
    /// 岩浆 / 水 / 火 / 营火，避免为每个 setter 各扫一遍。
    ///
    /// 与引擎 setter 的逐条对应（距离口径完全照抄）：
    ///   · 岩浆：<c>CalculateDistanceToFluid(avatar, 8, flowingFluidOnly:false)</c>，
    ///     距离 = min over magma cells of
    ///     <c>(p.X-(x+0.5), p.Y-(y+1), p.Z-(z+0.5))</c>
    ///     （Source: SubsystemMagmaBlockBehavior.cs:36 / SubsystemFluidBlockBehavior.cs:327-380）；
    ///   · 水：同上但 <c>flowingFluidOnly:true</c> —— 跳过 level==0，且上方仍是水时跳过
    ///     （Source: SubsystemWaterBlockBehavior.cs:31）；
    ///   · 火：<c>1 / (distanceSquared(cellOrigin, avatar) + 0.01)</c> 累加，最后 ×0.75
    ///     （Source: SubsystemFireBlockBehavior.cs:128 / :164）；
    ///   · 营火：最近的 lit campfire cell origin（data &gt; 0）
    ///     （Source: SubsystemCampfireBlockBehavior.cs:51-61 / :135）。
    ///
    /// 半径：流体严格 8（与引擎一致）；火 / 营火 12（有界，远处逆平方贡献 &lt; 0.006）。
    /// 未加载区块返回 0（无内容）；绝不触发区块加载，也不写任何格子。
    /// </summary>
    public static class AmbientSoundScanner
    {
        private const int ContentsMask = 0x3FF;

        public static bool TryScan(Vector3 position, IAmbientCellSource source,
            AmbientContentMask mask, out AmbientScanResult result, out string reason)
        {
            result = default;
            reason = null;
            if (source == null)
            {
                reason = "cell-source-null";
                return false;
            }
            if (mask == null)
            {
                reason = "content-mask-null";
                return false;
            }
            if (float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z) ||
                float.IsInfinity(position.X) || float.IsInfinity(position.Y) ||
                float.IsInfinity(position.Z))
            {
                reason = "position-not-finite";
                return false;
            }
            if (Math.Abs(position.X) > 30000000f || Math.Abs(position.Z) > 30000000f ||
                Math.Abs(position.Y) > 30000000f)
            {
                reason = "position-out-of-range";
                return false;
            }

            int centerX = (int)Math.Floor(position.X);
            int centerY = (int)Math.Floor(position.Y);
            int centerZ = (int)Math.Floor(position.Z);
            int minX = centerX - AmbientSoundPolicy.FireRadius;
            int maxX = centerX + AmbientSoundPolicy.FireRadius;
            int minZ = centerZ - AmbientSoundPolicy.FireRadius;
            int maxZ = centerZ + AmbientSoundPolicy.FireRadius;
            int minY = Math.Max(0, centerY - AmbientSoundPolicy.FireRadius);
            int maxY = Math.Min(255, centerY + AmbientSoundPolicy.FireRadius);
            int fluidMinY = Math.Max(0, centerY - AmbientSoundPolicy.FluidRadius);
            int fluidMaxY = Math.Min(254, centerY + AmbientSoundPolicy.FluidRadius);
            float bestWater = float.MaxValue;
            float bestMagma = float.MaxValue;
            float bestCampfire = float.MaxValue;
            float fireIntensity = 0f;
            int cellsRead = 0;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    bool fluidColumn =
                        x >= centerX - AmbientSoundPolicy.FluidRadius &&
                        x <= centerX + AmbientSoundPolicy.FluidRadius &&
                        z >= centerZ - AmbientSoundPolicy.FluidRadius &&
                        z <= centerZ + AmbientSoundPolicy.FluidRadius;
                    for (int y = minY; y <= maxY; y++)
                    {
                        if (cellsRead >= AmbientSoundPolicy.MaxScanCells)
                        {
                            result.CellCapHit = true;
                            goto ScanComplete;
                        }
                        cellsRead++;
                        int value = source.GetCellValue(x, y, z);
                        if (value == 0) continue;
                        // Source: Survivalcraft/Game/Terrain.cs:404-416
                        // contents = value & 0x3FF, data = value >>> 14
                        int contents = value & ContentsMask;
                        if (contents == 0) continue;
                        int data = (int)((uint)value >> 14);

                        if (contents == AmbientSoundPolicy.FireContents)
                        {
                            if (result.FireCells >= AmbientSoundPolicy.MaxFireCells)
                            {
                                result.FireCellCapHit = true;
                                continue;
                            }
                            result.FireCells++;
                            float dx = position.X - x;
                            float dy = position.Y - y;
                            float dz = position.Z - z;
                            fireIntensity += 1f / (dx * dx + dy * dy + dz * dz + 0.01f);
                            continue;
                        }

                        if (contents == AmbientSoundPolicy.CampfireContents)
                        {
                            // 只有 data > 0 的营火在燃烧（data==0 是灰烬，没有火焰粒子 / 声音）。
                            if (data < AmbientSoundPolicy.CampfireMinData) continue;
                            result.CampfireCells++;
                            float dx = position.X - x;
                            float dy = position.Y - y;
                            float dz = position.Z - z;
                            float distanceSquared = dx * dx + dy * dy + dz * dz;
                            if (distanceSquared < bestCampfire) bestCampfire = distanceSquared;
                            continue;
                        }

                        if (mask.IsWater(contents))
                        {
                            if (!fluidColumn || y < fluidMinY || y > fluidMaxY) continue;
                            // Source: SubsystemFluidBlockBehavior.cs:359-366（flowingFluidOnly）
                            if ((data & 0xF) == 0) continue;
                            if (mask.IsWater(ExtractContents(source.GetCellValue(x, y + 1, z)))) continue;
                            result.WaterCells++;
                            float dx = position.X - (x + 0.5f);
                            float dy = position.Y - (y + 1f);
                            float dz = position.Z - (z + 0.5f);
                            float distanceSquared = dx * dx + dy * dy + dz * dz;
                            if (distanceSquared < bestWater) bestWater = distanceSquared;
                            continue;
                        }

                        if (mask.IsMagma(contents))
                        {
                            if (!fluidColumn || y < fluidMinY || y > fluidMaxY) continue;
                            result.MagmaCells++;
                            float dx = position.X - (x + 0.5f);
                            float dy = position.Y - (y + 1f);
                            float dz = position.Z - (z + 0.5f);
                            float distanceSquared = dx * dx + dy * dy + dz * dz;
                            if (distanceSquared < bestMagma) bestMagma = distanceSquared;
                        }
                    }
                }
            }

            ScanComplete:
            result.CellsRead = cellsRead;
            result.FireIntensity = fireIntensity;
            if (bestWater != float.MaxValue)
            {
                result.HasWater = true;
                result.WaterDistance = MathUtils.Sqrt(bestWater);
            }
            if (bestMagma != float.MaxValue)
            {
                result.HasMagma = true;
                result.MagmaDistance = MathUtils.Sqrt(bestMagma);
            }
            if (bestCampfire != float.MaxValue)
            {
                result.HasCampfire = true;
                result.CampfireDistance = MathUtils.Sqrt(bestCampfire);
            }
            return true;
        }

        private static int ExtractContents(int value) => value & ContentsMask;
    }

    /// <summary>
    /// AMBIENT_SOUNDS_STATE 的严格编解码：非法值整条拒绝（false + 原因），绝不猜默认值。
    /// 合法消息要求 target_client_id &gt; 0、revision &gt; 0、server_tick_ms ≥ 0、
    /// 水 / 岩浆 / 营火在 [0,1]，火焰在 [0,<see cref="AmbientSoundPolicy.MaxFireVolume"/>]，
    /// 且都是有限数。
    /// </summary>
    public static class AmbientSoundCodec
    {
        public static bool TryEncode(uint targetClientId, uint revision, uint serverTickMs,
            in AmbientVolumes volumes, out P.AmbientSoundsState message, out string reason)
        {
            message = null;
            reason = null;
            if (targetClientId == 0)
            {
                reason = "target-client-zero";
                return false;
            }
            if (revision == 0)
            {
                reason = "revision-zero";
                return false;
            }
            if (!volumes.IsValid())
            {
                reason = "volumes-invalid(" + volumes.Describe() + ")";
                return false;
            }
            message = new P.AmbientSoundsState
            {
                Revision = revision,
                ServerTickMs = serverTickMs,
                TargetClientId = targetClientId,
                FireVolume = volumes.Fire,
                CampfireVolume = volumes.Campfire,
                WaterVolume = volumes.Water,
                MagmaVolume = volumes.Magma
            };
            return true;
        }

        public static bool TryDecode(P.AmbientSoundsState message, out AmbientVolumes volumes,
            out string reason)
        {
            volumes = default;
            reason = null;
            if (message == null)
            {
                reason = "ambient-null";
                return false;
            }
            if (message.TargetClientId == 0)
            {
                reason = "ambient-target-zero";
                return false;
            }
            if (message.Revision == 0)
            {
                reason = "ambient-revision-zero";
                return false;
            }
            var decoded = new AmbientVolumes(message.FireVolume, message.WaterVolume,
                message.MagmaVolume, message.CampfireVolume);
            if (!decoded.IsValid())
            {
                reason = "ambient-volumes-invalid(" + decoded.Describe() + ")";
                return false;
            }
            volumes = decoded;
            return true;
        }

        public static byte[] Encode(uint targetClientId, uint revision, uint serverTickMs,
            in AmbientVolumes volumes)
        {
            if (!TryEncode(targetClientId, revision, serverTickMs, in volumes,
                    out P.AmbientSoundsState message, out string reason))
                throw new InvalidOperationException("环境音状态编码失败：" + reason);
            return message.ToByteArray();
        }
    }
}
