using Comms;
using System;

namespace ScMultiplayer
{
    /// <summary>
    /// 客户端 → 主机：联机角色的游戏统计上报（低频，默认 2 秒一次或内容变化时）。
    ///
    /// 为什么要单独一条消息：统计（挖掘数/行走距离/死亡记录…）只有**客户端**算得准
    /// （它才有真实输入驱动的 ComponentMiner / ComponentLocomotion）。主机把它写进角色记录
    /// （`ScMultiplayerPlayers.xml`），加入时再随 `GamePakWorldMessage` 回填给客户端。
    /// </summary>
    [Serializable]
    public class PlayerStatsMessage : Message
    {
        /// <summary>发送方 ClientID（沿用本项目"msg.PlayerIndex = 发送方 ClientID"的约定）。</summary>
        public int PlayerIndex;

        public PlayerStatsSnapshot Stats = new PlayerStatsSnapshot();

        public PlayerStatsMessage()
        {
        }

        public PlayerStatsMessage(int playerIndex, PlayerStatsSnapshot stats)
        {
            PlayerIndex = playerIndex;
            Stats = stats ?? new PlayerStatsSnapshot();
        }

        protected override void Read(SuReader reader)
        {
            PlayerIndex = reader.ReadInt32();
            Stats = PlayerStatsSnapshot.Read(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(PlayerIndex);
            PlayerStatsSnapshot.Write(writer, Stats);
        }
    }
}
