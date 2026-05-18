using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ScMultiplayer
{
    /// <summary>
    /// 网络同步版 SubsystemTerrain
    /// 发送本地方块修改, 接收远程方块修改并应用
    /// 防止回环: 追踪网络接收的 Cell, 不重复发送
    /// </summary>
    public class SuSubsystemTerrain : SubsystemTerrain
    {
        public static Dictionary<Point3, bool> ModifiedCells;
        public static Dictionary<Point3, bool> ReModifiedCells = new Dictionary<Point3, bool>();
        public static List<int> CellValues = new List<int>();
        private static HashSet<Point3> m_networkReceivedCells = new HashSet<Point3>();
        private bool IsInit = false;

        public override void Update(float dt)
        {
            // 延迟初始化: 用反射获取父类私有字段
            if (!IsInit)
            {
                ModifiedCells = Game.Program.ModManager.ModParentField
                    .GetParentField<Dictionary<Point3, bool>>(this, "m_modifiedCells", base.GetType());

                // 验证获取成功
                if (ModifiedCells != null)
                    IsInit = true;
                else
                {
                    base.Update(dt);
                    return; // 等下次再试
                }
            }

            // 发送本地方块修改 (排除网络接收的)
            if (ScMultiplayer.client.IsConnected && ModifiedCells.Count != 0)
            {
                var toSend = new Dictionary<Point3, bool>();
                foreach (var kvp in ModifiedCells)
                {
                    if (!m_networkReceivedCells.Contains(kvp.Key))
                        toSend[kvp.Key] = kvp.Value;
                }
                if (toSend.Count > 0)
                {
                    var msg = new GameModifiedCellsMessage(toSend);
                    ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
                }
            }

            // 应用远程方块修改
            if (ReModifiedCells.Count != 0)
            {
                lock (ReModifiedCells)
                {
                    int index = 0;
                    foreach (var kvp in ReModifiedCells)
                    {
                        if (index < CellValues.Count)
                        {
                            // 标记为网络接收, 防止回环
                            m_networkReceivedCells.Add(kvp.Key);
                            ChangeCell(kvp.Key.X, kvp.Key.Y, kvp.Key.Z, CellValues[index], true);
                        }
                        index++;
                    }
                    ReModifiedCells.Clear();
                }
            }

            // 调用父类 Update (处理 TerrainUpdater + ProcessModifiedCells)
            base.Update(dt);

            // 清理旧的网络接收标记 (父类已处理完, 新产生的才是本地修改)
            m_networkReceivedCells.Clear();
        }
    }
}
