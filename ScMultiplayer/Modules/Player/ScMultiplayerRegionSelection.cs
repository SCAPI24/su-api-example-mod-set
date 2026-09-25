using Engine;
using Game;
using System;
using System.Linq;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P1：**选区状态**（本端本地）。
    ///
    /// 需求口径（用户 2026-09-24）：点1 / 点2 只决定 **X/Z 范围**，高度默认 **0–255（全高）**；
    /// 两点齐备后画出高亮线框（颜色 = 本端角色在主机上的序号 → 导线/paint 调色板 16 色），
    /// **此阶段不填充面**，并额外画"地面矩形 + 顶部矩形"加强可读性。
    ///
    /// 分工：状态与绘制在联机 Mod；**面板在 GmMod**（`GmUiComponent` 通过 `ProjectReference`
    /// 直接调用这里的 public 方法）。绘制由挂在玩家实体上的
    /// <see cref="SuComponentRegionOverlay"/> 完成（引擎的 `SubsystemDrawing` 会收集实体组件里的 `IDrawable`）。
    /// </summary>
    public partial class ScMultiplayer
    {
        public const int RegionSelectionMinY = 0;
        public const int RegionSelectionMaxY = 255;

        private Point3? m_regionSelectionPoint1;
        private Point3? m_regionSelectionPoint2;

        /// <summary>某个 PlayerData 是不是本端自己的角色（网络玩家不是）。</summary>
        public bool IsLocalPlayerData(PlayerData playerData) =>
            playerData != null && !m_networkPlayerData.Values.Contains(playerData);

        /// <summary>设置点1（index=1）或点2（index=2）。</summary>
        public void RegionSelectionSetPoint(int index, Point3 point)
        {
            if (index == 1)
                m_regionSelectionPoint1 = point;
            else
                m_regionSelectionPoint2 = point;
        }

        public void RegionSelectionClear()
        {
            m_regionSelectionPoint1 = null;
            m_regionSelectionPoint2 = null;
        }

        public bool RegionSelectionHasPoint(int index) =>
            index == 1 ? m_regionSelectionPoint1.HasValue : m_regionSelectionPoint2.HasValue;

        public bool TryGetRegionSelectionPoint(int index, out Point3 point)
        {
            Point3? value = index == 1 ? m_regionSelectionPoint1 : m_regionSelectionPoint2;
            point = value ?? default(Point3);
            return value.HasValue;
        }

        /// <summary>
        /// 两点齐备时给出选区长方体（闭区间格子坐标；Y 固定全高）。
        /// </summary>
        public bool TryGetRegionSelectionBox(out Point3 min, out Point3 max)
        {
            min = default(Point3);
            max = default(Point3);
            if (!m_regionSelectionPoint1.HasValue || !m_regionSelectionPoint2.HasValue)
                return false;
            Point3 a = m_regionSelectionPoint1.Value;
            Point3 b = m_regionSelectionPoint2.Value;
            min = new Point3(Math.Min(a.X, b.X), RegionSelectionMinY, Math.Min(a.Z, b.Z));
            max = new Point3(Math.Max(a.X, b.X), RegionSelectionMaxY, Math.Max(a.Z, b.Z));
            return true;
        }

        /// <summary>
        /// 选区线框颜色：本端角色**在主机上的序号** → paint 调色板索引（16 色，导线用的就是这套）。
        /// </summary>
        public int RegionSelectionPaletteIndex()
        {
            int index = -1;
            try
            {
                if (client != null && playerMappingManager != null)
                    index = playerMappingManager.GetPlayerIndex(client.ClientID);
            }
            catch (Exception)
            {
                index = -1;
            }
            if (index < 0)
                index = client?.ClientID ?? 0;
            return MathUtils.Abs(index) % 16;
        }
    }

    /// <summary>
    /// 《玩家领地》选区**跨程序集门面**：独立 mod（GmMod 等）只允许通过这里读写选区。
    ///
    /// 为什么必须有它（2026-09-25 实测）：Obfuscar 的 <c>&lt;SkipType name="..." /&gt;</c> 只保**类型名**，
    /// 主类 `ScMultiplayer` 的成员照样被改名 → 第三方 mod 直接引用 `currentInstance` /
    /// `RegionSelection*` 时运行期抛
    /// `Field not found: ScMultiplayer.ScMultiplayer ScMultiplayer.ScMultiplayer.currentInstance
    /// Due to: Could not find field in class`；给主类补 `skipMethods` 又撞上它实现的内部接口
    /// `Ports.IMultiplayerUiCommandPort`（Obfuscar: Inconsistent virtual method obfuscation state）。
    /// 所以跨程序集只走这个**静态、无虚方法**的门面类，在 Obfuscar.xml 里整类跳过
    /// （与 `DataModificationTool` 同一套路）。
    /// </summary>
    public static class RegionSelectionApi
    {
        public const int MinY = ScMultiplayer.RegionSelectionMinY;
        public const int MaxY = ScMultiplayer.RegionSelectionMaxY;

        /// <summary>联机 Mod 是否已就绪（未加载 / 未初始化时为 false）。</summary>
        public static bool IsAvailable => ScMultiplayer.currentInstance != null;

        /// <summary>设置点1（index=1）或点2（index=2）；Mod 未就绪返回 false。</summary>
        public static bool SetPoint(int index, Point3 point)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
                return false;
            mod.RegionSelectionSetPoint(index, point);
            return true;
        }

        public static bool Clear()
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
                return false;
            mod.RegionSelectionClear();
            return true;
        }

        public static bool TryGetPoint(int index, out Point3 point)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
            {
                point = default(Point3);
                return false;
            }
            return mod.TryGetRegionSelectionPoint(index, out point);
        }

        public static bool TryGetBox(out Point3 min, out Point3 max)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null)
            {
                min = default(Point3);
                max = default(Point3);
                return false;
            }
            return mod.TryGetRegionSelectionBox(out min, out max);
        }
    }
}
