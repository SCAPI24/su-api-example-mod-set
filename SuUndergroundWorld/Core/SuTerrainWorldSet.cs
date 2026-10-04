using Game;

namespace SuUndergroundWorld.Core
{
    /// <summary>一个"世界"所需的整套地形管线（对应 SubsystemTerrain 的 6 个成员）。</summary>
    internal sealed class SuTerrainWorldSet
    {
        public Terrain Terrain;
        public TerrainUpdater Updater;
        public TerrainRenderer Renderer;
        public TerrainSerializer23 Serializer;
        public ITerrainContentsGenerator Generator;
        public BlockGeometryGenerator GeoGen;

        public string Name;

        /// <summary>
        /// 新建"地下世界"：自己的 Terrain / Updater / Renderer / Serializer("1") / 生成器 / 几何生成器。
        ///
        /// 顺序很关键：TerrainUpdater 构造时会把 subsystemTerrain.Terrain 缓存进 m_terrain，
        /// 所以构造前必须先把 SubsystemTerrain 的 Terrain 指向新世界，构造完再还原。
        /// </summary>
        public static SuTerrainWorldSet CreateDeep(
            SubsystemTerrain st,
            GameEntitySystem.Project project,
            string worldDirectory,
            ITerrainContentsGenerator generator)
        {
            var set = new SuTerrainWorldSet { Name = "deep" };
            set.Terrain = new Terrain();
            set.Generator = generator;
            set.Serializer = new TerrainSerializer23(worldDirectory, "1");   // → Regions1/Region x,y.dat

            Terrain previous = st.Terrain;
            SuTerrainBinding.SetTerrain(st, set.Terrain);
            try
            {
                set.Updater = new TerrainUpdater(st);
                set.Renderer = new TerrainRenderer(st);
                set.GeoGen = new BlockGeometryGenerator(
                    set.Terrain,
                    st,
                    project.FindSubsystem<SubsystemElectricity>(true),
                    project.FindSubsystem<SubsystemFurnitureBlockBehavior>(true),
                    project.FindSubsystem<SubsystemMetersBlockBehavior>(true),
                    project.FindSubsystem<SubsystemPalette>(true));
            }
            finally
            {
                SuTerrainBinding.SetTerrain(st, previous);
            }
            return set;
        }

        public void DisposeAll()
        {
            // 注意：TerrainUpdater 有 public Dispose() 但未实现 IDisposable，只能直接调用
            Try(() => Updater?.Dispose());
            Try(() => Renderer?.Dispose());
            Try(() => Serializer?.Dispose());
            Try(() => Terrain?.Dispose());
            Updater = null;
            Renderer = null;
            Serializer = null;
            Terrain = null;
            Generator = null;
            GeoGen = null;
        }

        private static void Try(System.Action action)
        {
            try
            {
                action();
            }
            catch (System.Exception e)
            {
                Engine.Log.Warning("[SuUndergroundWorld] dispose failed: {0}", e.Message);
            }
        }
    }
}
