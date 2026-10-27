using System.Collections.Generic;
using Engine;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 一个在线角色的复活点（主机权威表的条目）。纯值，不依赖 Game。
    /// </summary>
    public readonly struct OnlineSpawnPoint
    {
        public readonly PlayerKey Key;
        public readonly Vector3 SpawnPosition;
        public readonly string Identity;

        public OnlineSpawnPoint(PlayerKey key, Vector3 spawnPosition, string identity)
        {
            Key = key;
            SpawnPosition = spawnPosition;
            Identity = identity ?? string.Empty;
        }
    }

    /// <summary>
    /// 复活点表的角色来源 / 落地端。主机通过 <see cref="CaptureOnlineSpawnPoints"/> 抓
    /// 主机本地角色 + 全部联机角色；客户端通过 <see cref="TryApplySpawnPoint"/> 把主机下发
    /// 的值写回本机/远端 PlayerData.SpawnPosition（**不写 Project.xml / 角色记录文件**）。
    /// </summary>
    public interface ISpawnPointProvider
    {
        /// <summary>
        /// 主机：抓当前在线角色的复活点。返回 &gt;= 0 = 成功（0 表示没有在线角色）；
        /// -1 = 世界/数据暂不可用（不要把这个结果当成"全部离线"广播删除）。
        /// </summary>
        int CaptureOnlineSpawnPoints(List<OnlineSpawnPoint> target, out string reason);

        /// <summary>
        /// 客户端：把一条权威复活点写进对应的本机角色或远端化身。
        /// false = 角色/本机 PlayerData 还没建好（调用方保留 pending，下一拍重试）。
        /// </summary>
        bool TryApplySpawnPoint(PlayerKey key, Vector3 spawnPosition, out string reason);
    }
}
