using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;

namespace ScMultiplayer
{
    public class SuSubsystemTerrain : SubsystemTerrain
    {
        public static Dictionary<Point3, bool> ModifiedCells;
        public static Dictionary<Point3, bool> ReModifiedCells = new Dictionary<Point3, bool>();
        public static List<int> CellValues = new List<int>();
        private bool IsInit = false;

        public override void Update(float dt)
        {

            if (!IsInit)
            {
                ModifiedCells = Game.Program.ModManager.ModParentField.GetParentField<Dictionary<Point3, bool>>(this, "m_modifiedCells", base.GetType());
                IsInit = true;
            }
            if (ScMultiplayer.client.IsConnected /*&& ScMultiplayer.client.ClientID==0*/)
            {
                if (ModifiedCells.Count != 0)
                    ScMultiplayer.client.SendInput(Message.Write(new GameModifiedCellsMessage(ModifiedCells), ScMultiplayer.client.Address));
            }
            if (ReModifiedCells.Count != 0)
            {
                int index = 0;


                foreach (var kvp in ReModifiedCells)
                {
                    SuSubsystemTerrain.ModifiedCells[kvp.Key] = kvp.Value;
                    ChangeCell(kvp.Key.X, kvp.Key.Y, kvp.Key.Z, CellValues[index], true);
                    index++; // 递增索引
                }
                ReModifiedCells.Clear();
            }
            TerrainUpdater.Update();
            ProcessModifiedCells();

        }
    }
}
