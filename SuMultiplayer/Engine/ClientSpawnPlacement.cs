using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端"下载世界加载前"的本机角色位置信箱。
    ///
    /// 为什么需要它：客户端下载世界后由 GameLoading 直接实例化 Project.xml 里的 Players 段
    /// （那是主机存档角色），引擎会在**第一次 GameScreen Update 之前**就把本机角色放到
    /// 下载世界的主机位置并开始渲染。等联机角色模块第一次 tick 再搬走，玩家就会先看到
    /// 主机进入地图的位置、再瞬移（实机反馈 2026-10-27）。
    ///
    /// HelloAck 的 assigned_position/assigned_spawn_position 在下载世界导入前就已到达；
    /// 地形层把它存进本信箱，<see cref="SuSubsystemPlayers.Load"/> 在引擎读取 Players 段
    /// 时直接把本机 PlayerData 的出生点预置成记录位置。之后联机角色模块的
    /// ApplyAssignedPlacementOnce 再做一次精确贴合与复活点设置。
    /// </summary>
    public static class ClientSpawnPlacement
    {
        private static readonly object Gate = new object();
        private static bool m_hasPlacement;
        private static Vector3 m_position;
        private static Vector3 m_spawnPosition;

        public static void Set(Vector3 position, Vector3 spawnPosition)
        {
            lock (Gate)
            {
                m_hasPlacement = true;
                m_position = position;
                m_spawnPosition = spawnPosition;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                m_hasPlacement = false;
                m_position = Vector3.Zero;
                m_spawnPosition = Vector3.Zero;
            }
        }

        /// <summary>取走一次（消费后清空，绝不跨世界复用）。</summary>
        public static bool TryTake(out Vector3 position, out Vector3 spawnPosition)
        {
            lock (Gate)
            {
                position = m_position;
                spawnPosition = m_spawnPosition;
                bool has = m_hasPlacement;
                m_hasPlacement = false;
                m_position = Vector3.Zero;
                m_spawnPosition = Vector3.Zero;
                return has;
            }
        }
    }
}
