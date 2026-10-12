using System;
using System.Collections.Generic;

namespace Channel
{
    /// <summary>
    /// 每通道电压表（纯逻辑，无引擎依赖，可离线真值测试）。
    ///
    /// 语义：某通道的值 = 该通道**所有参与元件**"本地导线电压"的最大值（线或）。
    ///   · 与上报顺序无关（取最大），不会因为某步没被模拟而丢贡献；
    ///   · 元件退出/换通道立刻重算，避免旧电平挂在通道上；
    ///   · 值变化时通过 <see cref="ChannelChanged"/> 通知宿主（宿主负责把该通道元件排进下一电路步）。
    /// </summary>
    public sealed class ChannelBus
    {
        public const float Epsilon = 0.0001f;

        private readonly Dictionary<object, float> m_local = new Dictionary<object, float>();
        private readonly Dictionary<object, int> m_memberChannel = new Dictionary<object, int>();
        private readonly Dictionary<int, List<object>> m_members = new Dictionary<int, List<object>>();
        private readonly Dictionary<int, float> m_values = new Dictionary<int, float>();

        /// <summary>通道值发生变化时回调（参数=通道号）。</summary>
        public Action<int> ChannelChanged;

        /// <summary>上报某成员的本地电压并取回本通道的线或值。</summary>
        public float Join(object member, int channel, float local)
        {
            if (member == null)
                return GetValue(channel);

            if (m_memberChannel.TryGetValue(member, out int previousChannel) && previousChannel != channel)
            {
                RemoveFromChannel(member, previousChannel);
                Recompute(previousChannel);
            }

            m_memberChannel[member] = channel;
            m_local[member] = local;

            if (!m_members.TryGetValue(channel, out List<object> list))
            {
                list = new List<object>();
                m_members.Add(channel, list);
            }
            if (!list.Contains(member))
                list.Add(member);

            return Recompute(channel);
        }

        /// <summary>成员退出（不接入通道 / 被移除）：撤掉它的贡献并重算。</summary>
        public void Leave(object member)
        {
            if (member == null)
                return;
            if (!m_local.Remove(member))
                return;

            if (m_memberChannel.TryGetValue(member, out int channel))
            {
                RemoveFromChannel(member, channel);
                m_memberChannel.Remove(member);
                Recompute(channel);
            }
        }

        public float GetValue(int channel)
        {
            return m_values.TryGetValue(channel, out float value) ? value : 0f;
        }

        public int MemberCount(int channel)
        {
            return m_members.TryGetValue(channel, out List<object> list) ? list.Count : 0;
        }

        private void RemoveFromChannel(object member, int channel)
        {
            if (m_members.TryGetValue(channel, out List<object> list))
                list.Remove(member);
        }

        private float Recompute(int channel)
        {
            float value = 0f;
            if (m_members.TryGetValue(channel, out List<object> list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    float memberValue;
                    if (m_local.TryGetValue(list[i], out memberValue) && memberValue > value)
                        value = memberValue;
                }
            }

            float previous = GetValue(channel);
            if (Math.Abs(value - previous) > Epsilon)
            {
                m_values[channel] = value;
                ChannelChanged?.Invoke(channel);
            }
            return value;
        }
    }
}
