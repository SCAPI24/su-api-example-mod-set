using System;
using Engine;
using Game;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 引擎 <c>SubsystemAmbientSounds</c> 的注入外壳（数据库 Class 参数替换，与
    /// <see cref="SuSubsystemAudio"/> / <see cref="SuSubsystemTerrain"/> 同一机制）。
    ///
    /// 为什么必须替换子系统而不是在别处每帧写字段：
    ///   · <c>SubsystemAmbientSounds.Update</c> 是 **public 非虚** 方法
    ///     （Source: Survivalcraft/Game/SubsystemAmbientSounds.cs:28），派生类用 <c>new</c>
    ///     隐藏拦不到；引擎通过 <c>IUpdateable</c> 派发，所以本类**重新实现** IUpdateable
    ///     （显式接口方法），在 <c>base.Update</c> 之前把网络音量写进三个字段；
    ///   · <c>base.Update</c> 本身仍拥有循环播放 / 淡入淡出 / 暂停 / 随机 Sizzles 的全部逻辑
    ///     （:30-59），并在末尾把字段清 0（:61-63）—— 本外壳不改这些语义。
    ///
    /// 方向：
    ///   · 主机 / 单机：<see cref="IAmbientSoundVolumeSource.IsClientReplica"/> = false →
    ///     一个字段都不碰，原样调用 base.Update（主机听感与改前完全一致）；
    ///   · 客户端副本：只在收到主机音量时施加；尚未收到 / 已断线时明确清零，
    ///     循环随引擎的音量阈值暂停（不会卡在旧音量上）。客户端不跑任何被闸门挡住的
    ///     方块行为，不写格 / 不生成实体。
    ///
    /// 无静态状态：音量源由组合根以实例注入（Engine/AmbientSoundReplicaEngine.cs）。
    /// </summary>
    public sealed class SuSubsystemAmbientSounds : SubsystemAmbientSounds, IUpdateable
    {
        private const int ProblemLogMs = 10000;

        private IAmbientSoundVolumeSource m_source;
        private long m_appliedFrames;
        private long m_zeroFrames;
        private long m_hostFrames;
        private long m_failures;
        private long m_lastProblemMs;

        /// <summary>组合根 / 引擎适配层：挂上网络音量源（同一实例可重复调用，幂等）。</summary>
        public void AttachVolumeSource(IAmbientSoundVolumeSource source)
        {
            if (source == null) return;
            m_source = source;
        }

        /// <summary>卸载：只清掉自己持有的引用，不触碰引擎字段 / 已播放的 Sound。</summary>
        public void DetachVolumeSource(IAmbientSoundVolumeSource source)
        {
            if (ReferenceEquals(m_source, source)) m_source = null;
        }

        void IUpdateable.Update(float dt)
        {
            IAmbientSoundVolumeSource source = m_source;
            if (source == null)
            {
                base.Update(dt);
                return;
            }

            bool clientReplica;
            try
            {
                clientReplica = source.IsClientReplica;
            }
            catch (Exception e)
            {
                m_failures++;
                LogProblem("is-client: " + e.GetType().Name + " " + e.Message);
                // 方向未知时不改字段：主机上已有的本地 setter 值必须原样交给 base.Update。
                base.Update(dt);
                return;
            }

            if (!clientReplica)
            {
                m_hostFrames++;
                base.Update(dt);
                return;
            }

            try
            {
                if (source.TryGetClientVolumes(out AmbientVolumes volumes, out _))
                {
                    // 营火在引擎里同样走 FireSoundVolume（Source:
                    // SubsystemCampfireBlockBehavior.cs:62 / ComponentOnFire.cs:83）。
                    FireSoundVolume = volumes.EngineFire;
                    WaterSoundVolume = volumes.Water;
                    MagmaSoundVolume = volumes.Magma;
                    m_appliedFrames++;
                }
                else
                {
                    // 客户端尚未收到首条同步 / 已断线：显式清零，让引擎按 MinAudibleVolume 暂停
                    // 三条循环。不是"静默兜底" —— 模块侧有 ambientRecv / ambientApplied /
                    // ambientSkipped 计数与限流日志。
                    FireSoundVolume = 0f;
                    WaterSoundVolume = 0f;
                    MagmaSoundVolume = 0f;
                    m_zeroFrames++;
                }
            }
            catch (Exception e)
            {
                m_failures++;
                FireSoundVolume = 0f;
                WaterSoundVolume = 0f;
                MagmaSoundVolume = 0f;
                LogProblem("apply: " + e.GetType().Name + " " + e.Message);
            }

            base.Update(dt);
        }

        /// <summary>状态行（组合根启动日志用）：网络音量施加 / 清零 / 主机直通 / 失败。</summary>
        public string Describe() =>
            "ambientShell appliedFrames=" + m_appliedFrames +
            " zeroFrames=" + m_zeroFrames +
            " hostFrames=" + m_hostFrames +
            " failures=" + m_failures;

        private void LogProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs != 0 && nowMs - m_lastProblemMs < ProblemLogMs) return;
            m_lastProblemMs = nowMs;
            Log.Warning("[SuMP] ambientApply: 读取客户端环境音音量失败（" + message +
                "）→ 本帧三条循环按 0 处理（累计 " + m_failures + " 次）");
        }
    }
}
