using System;
using System.Collections.Generic;
using Game;
using SuAPI;
using TemplatesDatabase;

namespace Channel
{
    /// <summary>
    /// 信道 Mod：把 button 的 index(142) 换成语义扩展过的 ButtonBlock，
    /// 并把按钮行为子系统换成交道行为子系统（含信道编辑对话框）。
    /// 按钮/信道的编辑与电路元件创建都先经过 SuEventBus，联机 Mod 可无耦合接管。
    /// </summary>
    public class ChannelMod : IMod
    {
        public const string ModIdentifier = "Channel";

        private IModInjector m_modInjector;
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_databaseToken;
        private EventSubscriptionToken m_blocksToken;

        public string Name => ModIdentifier;
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => new string[] { "SuEventBus" };
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            if (m_eventBus != null)
                throw new InvalidOperationException("[Channel] The Mod is already loaded.");
            m_modInjector = modInjector ?? throw new ArgumentNullException(nameof(modInjector));
            m_eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            // 1) 子系统：按钮行为 → 信道行为（同一个模板，只换 Class 参数）
            // Source: Pak/Database.xml:ButtonBlockBehavior.Class (Guid 4407e30b-...)
            modInjector.Register("Game.SubsystemButtonBlockBehavior",
                "Channel.SubsystemChannelBlockBehavior");

            // 2) 方块：button 的 index 换成我们的 ButtonBlock
            //    （类名保持 "ButtonBlock"，否则 Pak/CraftingRecipes.xml 的 Result="ButtonBlock" 解析失败）
            modInjector.RegisterBlock(Game.ButtonBlock.Index, typeof(ButtonBlock), Name);

            // 3) 类名替换必须在 GameDatabase 事件里 Apply（引擎不会自动 Apply）
            m_databaseToken = eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                if (args == null || args.Length != 1 || !(args[0] is Database database))
                    throw new InvalidOperationException("[Channel] GameDatabase.GameDatabase requires one Database.");
                m_modInjector.Apply(database, Name);
                return new object[] { true, args[0] };
            }, EventPriority.NORMAL);

            // 4) 方块替换必须在 BlocksManager.Initialize 事件里 ApplyBlocks
            //    NORMAL：晚于 ScMultiplayer 的 HIGHEST（ScMultiplayer 已不再抢 142，这里只是保序）
            m_blocksToken = eventBus.SubscribeEvent("BlocksManager.Initialize", args =>
            {
                // BlocksManager 会直接索引 item[0]，绝不能返回 null
                if (args == null || args.Length != 1)
                    throw new InvalidOperationException("[Channel] BlocksManager.Initialize requires one Block array.");

                // Source: ClothingBlock.cs:Initialize — the native game reuses this event for ClothingData[].
                // Channel participates only in Block[] initialization; clothing data remains with its original owner.
                if (args[0] is ClothingData[]) return new object[] { false, args[0] };
                if (!(args[0] is Block[] blocks))
                    throw new InvalidOperationException("[Channel] Unsupported block initialization payload: " + args[0]?.GetType());

                m_modInjector.ApplyBlocks(blocks, Name);
                return new object[] { false };
            }, EventPriority.NORMAL);

            // 5) 自带中文词条交给翻译 Mod（未安装则纯英文）
            ChannelText.RegisterWithTranslationMod();
        }

        public void OnUnload()
        {
            // Source: IModEventBus.UnsubscribeEvent; release only this Mod's subscriptions.
            // SuEventHub belongs to all protocol users and must never be cleared here.
            if (m_databaseToken != null) m_eventBus.UnsubscribeEvent(m_databaseToken);
            if (m_blocksToken != null) m_eventBus.UnsubscribeEvent(m_blocksToken);
            m_databaseToken = null;
            m_blocksToken = null;
            m_eventBus = null;
            m_modInjector = null;
        }
    }
}
