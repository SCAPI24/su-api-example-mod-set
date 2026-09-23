using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    /// <summary>
    /// `ComponentHealth` 的联机替换：**客户端不该自己死**（方案 B）。
    ///
    /// 引擎里"死"和"倒地"都只看血量这一个值：
    ///   · 死亡判定：`HealthChange = Health - m_lastHealth`，只有
    ///     `Health == 0 && HealthChange < 0` 才执行死亡处理（ComponentHealth.cs:251 / :267）；
    ///   · 倒地：`ComponentHumanModel.Update:86`，条件是 `IsSleeping || Health <= 0f`；
    ///   · `PlayerData` 的死亡状态机（PlayerData.cs:266-273）只要看到一次 `Health <= 0f`
    ///     就记下 `m_playerDeathTime` 并切到 `PlayerDead`（死亡提示 + 死亡相机 + 复活界面），
    ///     之后不会再退回 `Playing`。
    ///
    /// 问题（实测）：客户端本地**也会跑原生伤害**（窒息 / 摔落 / 岩浆 / 饥饿 / 尖刺），
    /// 因此能在主机不知情的情况下把本地血量打到 0 —— 于是本地"死了"（死亡界面锁存、
    /// 身体开始按倒地处理），而主机那份角色还活着；随后主机 1Hz 的权威血量（正值）把本地
    /// 血量顶回 >0 —— 结果就是"复活界面还开着、时不时还在提示饥饿扣血、人却站着"，
    /// 而另一端看这个角色一直是活的。
    ///
    /// 分工（与 `SuComponentVitalStats` 同一套路）：
    ///   · **主机 / 未联机**：完全走原生 `base.Update(dt)`，一个字都不改；
    ///   · **客户端**：照常跑原生伤害（血量条、红屏、痛叫这些表现全部保留），只在伤害算完之后
    ///     把**本端自己**的血量钉在一条正值地板上，并撤销这一帧可能已被死亡状态机锁存的状态。
    ///     真正的死亡只认主机广播的 0：那时 `m_localAuthoritativeDeath` 已置位（见
    ///     `HandleGamePlayerHealthMessage`），本组件不再拦，死亡 / 倒地 / 复活全部按原生走。
    /// </summary>
    public class SuComponentHealth : ComponentHealth, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>();
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        // `ComponentHealth.Update` 不是 virtual，因此按 SuComponentVitalStats 的做法
        // 在子类里**重新实现 IUpdateable**，让 SubsystemUpdate 走到这里。
        void IUpdateable.Update(float dt)
        {
            ScMultiplayer instance = ScMultiplayer.currentInstance;
            // 跑原生更新**之前**先跟随一次：本地血量被写回主机最近一次的权威值，
            // 于是原生伤害（含身体界面"骷髅头强制重生"按钮那种在 UI 里落下的伤害）
            // 不会在本帧跨过 0，死亡处理 / 状态机锁存 / 关身体界面都不会发生；
            // 本地被打掉的那部分记成待上报差额，交给主机去施加。
            instance?.SyncClientLocalHealthFromAuthority(m_componentPlayer, this);
            base.Update(dt);
            // 原生更新自己造成的伤害（饥饿/窒息/岩浆/摔落…）也在这次调用里落到血量上，再跟随一次。
            instance?.SyncClientLocalHealthFromAuthority(m_componentPlayer, this);
        }
    }
}
