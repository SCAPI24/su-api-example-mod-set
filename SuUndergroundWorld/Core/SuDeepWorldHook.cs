using Game;

namespace SuUndergroundWorld.Core
{
    /// <summary>
    /// 每帧钩子：挂在工程的 SubsystemUpdate 上，UpdateOrder = BlocksScanner(99)。
    ///
    /// 为什么不用 "Frame.Update" 事件：SubsystemSky 的 UpdateOrder 是 Default(0)、SubsystemTerrain 是 Terrain(100)，
    /// 而"地下世界要压暗天光"必须发生在"天光算完"之后、"地形光照计算"之前，所以需要一个 0 &lt; order &lt; 100 的更新点。
    /// 99 同时也晚于 ComponentBody(2)，所以拿到的是本帧物理结算后的玩家位置，正好用于接缝判定。
    /// </summary>
    internal sealed class SuDeepWorldHook : IUpdateable
    {
        private readonly SuWorldManager m_manager;

        public SuDeepWorldHook(SuWorldManager manager)
        {
            m_manager = manager;
        }

        public UpdateOrder UpdateOrder => UpdateOrder.BlocksScanner;

        public void Update(float dt)
        {
            m_manager.Tick();
        }
    }
}
