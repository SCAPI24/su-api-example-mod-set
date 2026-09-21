using Game;
using TemplatesDatabase;

namespace ScMultiplayer
{
    /// <summary>
    /// `SubsystemPlayerStats` 的替换实现：唯一目的是**不让联机角色的统计写进 Project.xml**。
    ///
    /// 背景（实测）：`SubsystemPlayerStats.Save` 会把 `m_playerStats` 里的每一项都写进
    /// Project.xml 的 `Players/Stats/&lt;index&gt;`。而联机角色的统计属于"角色记录"
    /// （`ScMultiplayerPlayers.xml`），既不该污染世界文件，也要能跨重进保留 —— 所以
    /// 保存前把网络角色索引的那几项剔掉（主机上是各客户端的化身；客户端上是它自己
    /// 从主机下载来的那个化身）。
    ///
    /// 只重写 Save：Load 保持引擎行为（下载世界里的历史项留在内存里，保存时自然被剔除），
    /// 内存中的统计照常供统计面板读取。
    /// </summary>
    public class SuSubsystemPlayerStats : SubsystemPlayerStats
    {
        // Source: Survivalcraft/Game/SubsystemPlayerStats.cs:SubsystemPlayerStats.Save
        protected override void Save(ValuesDictionary valuesDictionary)
        {
            base.Save(valuesDictionary);
            ScMultiplayer.PruneNetworkPlayerStatsFromProject(valuesDictionary);
        }
    }
}
