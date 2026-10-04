using System;
using System.Reflection;
using Game;

namespace SuUndergroundWorld.Core
{
    /// <summary>
    /// 反射绑定 SubsystemTerrain 的 6 个"世界成员"。
    ///
    /// 为什么要反射：这 6 个属性是 { get; private set; } 且非 virtual，
    /// 派生类在别的程序集里既不能赋值也不能覆盖（引擎源码不做任何修改的前提）。
    ///
    /// 定位策略：**先按字段类型找**（实测 SubsystemTerrain 里每种类型各只出现一次），
    /// 名字兜底再按编译器生成的 backing field 名找 —— 这样即使引擎改名也大概率仍可用。
    /// </summary>
    internal static class SuTerrainBinding
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly FieldInfo s_terrain = Find(typeof(Terrain), "Terrain");
        private static readonly FieldInfo s_updater = Find(typeof(TerrainUpdater), "TerrainUpdater");
        private static readonly FieldInfo s_renderer = Find(typeof(TerrainRenderer), "TerrainRenderer");
        private static readonly FieldInfo s_serializer = Find(typeof(TerrainSerializer23), "TerrainSerializer");
        private static readonly FieldInfo s_generator = Find(typeof(ITerrainContentsGenerator), "TerrainContentsGenerator");
        private static readonly FieldInfo s_geogen = Find(typeof(BlockGeometryGenerator), "BlockGeometryGenerator");

        public static bool IsValid =>
            s_terrain != null && s_updater != null && s_renderer != null &&
            s_serializer != null && s_generator != null && s_geogen != null;

        public static string DescribeMissing()
        {
            var sb = new System.Text.StringBuilder();
            if (s_terrain == null) sb.Append("Terrain ");
            if (s_updater == null) sb.Append("TerrainUpdater ");
            if (s_renderer == null) sb.Append("TerrainRenderer ");
            if (s_serializer == null) sb.Append("TerrainSerializer ");
            if (s_generator == null) sb.Append("TerrainContentsGenerator ");
            if (s_geogen == null) sb.Append("BlockGeometryGenerator ");
            return sb.ToString();
        }

        private static FieldInfo Find(Type fieldType, string propertyName)
        {
            FieldInfo[] fields = typeof(SubsystemTerrain).GetFields(Flags);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType == fieldType)
                {
                    return fields[i];
                }
            }
            // 名字兜底
            return typeof(SubsystemTerrain).GetField("<" + propertyName + ">k__BackingField", Flags);
        }

        public static SuTerrainWorldSet Capture(SubsystemTerrain st)
        {
            return new SuTerrainWorldSet
            {
                Terrain = st.Terrain,
                Updater = st.TerrainUpdater,
                Renderer = st.TerrainRenderer,
                Serializer = st.TerrainSerializer,
                Generator = st.TerrainContentsGenerator,
                GeoGen = st.BlockGeometryGenerator,
            };
        }

        public static void Apply(SubsystemTerrain st, SuTerrainWorldSet set)
        {
            s_terrain.SetValue(st, set.Terrain);
            s_updater.SetValue(st, set.Updater);
            s_renderer.SetValue(st, set.Renderer);
            s_serializer.SetValue(st, set.Serializer);
            s_generator.SetValue(st, set.Generator);
            s_geogen.SetValue(st, set.GeoGen);
        }

        /// <summary>只临时改 Terrain 指向：构造 TerrainUpdater 前必须用（其构造会缓存 m_terrain）。</summary>
        public static void SetTerrain(SubsystemTerrain st, Terrain terrain)
        {
            s_terrain.SetValue(st, terrain);
        }
    }
}
