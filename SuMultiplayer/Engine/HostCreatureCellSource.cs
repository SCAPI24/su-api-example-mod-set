using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 实时地图 v9 的主机端动物位置来源：**只读**枚举主机自己世界里的非玩家生物。
    ///
    /// 枚举规则与 <c>Creatures/CreatureSpawnModule.CaptureHostCreatures</c> 完全一致
    /// （有 <c>ComponentCreature</c>、没有 <c>ComponentPlayer</c>、模板名非空），
    /// 但这里**不写任何同步状态**（活动档位 / 发送节奏 / 上次位置），因此可以在
    /// 每次实时地图快照时安全调用，不会扰动生物同步的 50 Hz 扫描。
    ///
    /// 为什么单独一个只读实现：本项开工时 <c>Creatures/**</c> 正由另一个 owner 编辑，
    /// 不碰它的 <c>m_creaturesByKey</c>；两者看到的是同一批主机实体（模块的注册表就是
    /// 由同一条枚举规则在 50 Hz 扫描里填出来的）。等生物模块提供只读访问器后，
    /// 只需把组合根注册的实现换成那个访问器（接口不变）。
    ///
    /// 只输出位置：条目里没有名字 / 网络 id / 生命值；客户端不经过这里。
    /// </summary>
    public sealed class HostCreatureCellSource : IHostCreatureCellSource
    {
        /// <summary>
        /// 把主机当前的非玩家生物位置写进 <paramref name="target"/>。
        /// 返回写入条数；-1 = 当前没有可枚举的世界（原因写清，调用方不得当成"没有动物"）。
        /// </summary>
        public int CaptureHostCreatureCells(List<LiveMapAnimal> target, out string reason)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Clear();
            reason = string.Empty;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "还没有加载中的 Project（没进世界）";
                return -1;
            }
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(throwOnError: false);
            if (bodies == null)
            {
                reason = "世界里找不到 SubsystemBodies";
                return -1;
            }
            int count = 0;
            foreach (ComponentBody body in bodies.Bodies)
            {
                Entity entity = body?.Entity;
                if (entity == null) continue;
                if (entity.FindComponent<ComponentCreature>() == null) continue;
                if (entity.FindComponent<ComponentPlayer>() != null) continue;
                string template = entity.ValuesDictionary?.DatabaseObject?.Name;
                if (string.IsNullOrWhiteSpace(template)) continue;
                Vector3 position = body.Position;
                // 排序键只用于"同一份世界状态下快照顺序稳定"（实体身份在它的生命周期内不变）；
                // 不是网络键、不上线。真正的主机网络键由 CreatureSpawnModule 持有。
                target.Add(new LiveMapAnimal
                {
                    Key = RuntimeHelpers.GetHashCode(entity),
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z
                });
                count++;
            }
            return count;
        }
    }
}
