using Engine;
using Game;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public class SuSubsystemDeciduousLeavesBlockBehavior :
        SubsystemDeciduousLeavesBlockBehavior
    {
        private SubsystemGameInfo m_subsystemGameInfo;

        public override void OnBlockGenerated(
            int value, int x, int y, int z, bool isLoaded)
        {
            // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:
            // SubsystemDeciduousLeavesBlockBehavior.OnBlockGenerated
            // Chunk notification runs before terrain vertices are ready. Normalize derived leaf
            // season data in-place so a dense canopy does not issue one ChangeCell per leaf.
            int normalizedValue = GetNormalizedValue(value, x, y, z);
            if (normalizedValue != value)
            {
                int oldData = Terrain.ExtractData(value);
                int newData = Terrain.ExtractData(normalizedValue);
                SubsystemTerrain.Terrain.SetCellValueFast(x, y, z, normalizedValue);
                // 2026-10-01（用户口径）：直接写格不进引擎改动表 ⇒ 主动登记进下发批次，
                // 否则这次"规范化"只发生在主机本地，客户端永远看不到。
                (SubsystemTerrain as SuSubsystemTerrain)?.MarkCellModifiedForBroadcast(
                    new Point3(x, y, z));

                // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:
                // SubsystemDeciduousLeavesBlockBehavior.UpdateTimeOfYear
                // Fallen-leaf terrain remains host-authoritative; clients receive that change.
                if (HostTerrainAuthority.IsReadyForAuthoritativeMutation(
                        SubsystemTerrain, x, z) &&
                    DeciduousLeavesBlock.GetSeason(newData) == Season.Winter &&
                    DeciduousLeavesBlock.GetSeason(oldData) != Season.Winter)
                {
                    CreateFallenLeaves(new Point3(x, y, z), applyImmediately: true);
                }
            }

            // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:
            // SubsystemDeciduousLeavesBlockBehavior.OnBlockGenerated
            // Passing the normalized value preserves native leaf-particle scheduling without
            // causing UpdateTimeOfYear to modify terrain again.
            base.OnBlockGenerated(normalizedValue, x, y, z, isLoaded);
        }

        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:
            // SubsystemDeciduousLeavesBlockBehavior.OnPoll
            // Only the host advances persistent leaf season data. A synchronized client still
            // runs the native path to retain local falling-leaf particles.
            if (HostTerrainAuthority.IsAuthoritative ||
                GetNormalizedValue(value, x, y, z) == value)
                base.OnPoll(value, x, y, z, pollPass);
        }

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_subsystemGameInfo = Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
        }

        private int GetNormalizedValue(int value, int x, int y, int z)
        {
            // Source: Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs:
            // SubsystemDeciduousLeavesBlockBehavior.UpdateTimeOfYear
            var block = (DeciduousLeavesBlock)BlocksManager.Blocks[
                Terrain.ExtractContents(value)];
            float offset = 0.03f * MathUtils.Hash((uint)(x + y * 59 + z * 3319)) /
                4.2949673E+09f;
            float timeOfYear = IntervalUtils.Normalize(
                m_subsystemGameInfo.WorldSettings.TimeOfYear + offset);
            int data = block.SetTimeOfYear(Terrain.ExtractData(value), timeOfYear);
            return Terrain.ReplaceData(value, data);
        }
    }
}
