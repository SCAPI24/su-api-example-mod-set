using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuUndergroundWorld.Core;

namespace SuUndergroundWorld.Plug
{
    /// <summary>
    /// 入口：纯 Mod 的"双世界垂直叠层"（v1）。
    ///
    /// 行为：
    ///   1) 世界加载后，在玩家旁边 6 格处凿一口 3x3 竖井，从地表一直凿穿基岩到 y=0；
    ///   2) 玩家掉出世界（localY &lt; 0）→ 切换到"地下世界"，并把 localY + 256（位置连续，统一坐标不变）；
    ///   3) 地下世界压暗天光（微光），走到返回标记柱旁 → 切回地表竖井旁边；
    ///   4) 每次换界原子替换 SubsystemTerrain 的 6 个世界成员（反射），并用 UpdateEvent 保护后台地形线程；
    ///   5) 离开的世界立即落盘（引擎自己的 Save 只保存活动世界）。
    ///
    /// 存档：地表 = Regions/Region x,y.dat；地下 = Regions1/Region x,y.dat（TerrainSerializer23 的目录后缀）。
    ///
    /// 不做的（v1 已知边界）：两个世界不同屏渲染（站在地表看不到地下世界的方块）；
    /// 换界时速度归零（避免长距离坠落伤害）。
    /// </summary>
    public sealed class SuUndergroundWorldMod : IMod
    {
        public string Name => "SuUndergroundWorld";
        public string Version => "0.1.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; }
        public bool IsMergeLib => true;

        private SuWorldManager m_manager;
        private SuDeepWorldHook m_hook;
        private Project m_hookProject;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            m_manager = new SuWorldManager();
            m_hook = new SuDeepWorldHook(m_manager);
            GameManager.ProjectDisposed += OnProjectDisposed;
            eventBus?.SubscribeEvent("Frame.Update", OnFrameUpdate, EventPriority.NORMAL);
            Log.Information("[SuUndergroundWorld] v{0} 已加载（纯 mod 双世界；v1 不同屏渲染）", Version);
        }

        public void OnUnload()
        {
            GameManager.ProjectDisposed -= OnProjectDisposed;
            m_manager?.Shutdown();
            Log.Information("[SuUndergroundWorld] 已卸载");
        }

        private object[] OnFrameUpdate(object[] args)
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                m_hookProject = null;
                return args;
            }
            if (project != m_hookProject)
            {
                if (m_manager.TryBind(project))
                {
                    SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(false);
                    if (subsystemUpdate != null)
                    {
                        subsystemUpdate.AddUpdateable(m_hook);
                        m_hookProject = project;
                        Log.Information("[SuUndergroundWorld] 已挂入 SubsystemUpdate（UpdateOrder={0}）", m_hook.UpdateOrder);
                    }
                    else
                    {
                        Log.Warning("[SuUndergroundWorld] 找不到 SubsystemUpdate，无法挂接每帧钩子");
                    }
                }
            }
            return args;
        }

        private void OnProjectDisposed(Project project)
        {
            m_hookProject = null;
            m_manager?.OnProjectDisposed(project);
        }
    }
}
