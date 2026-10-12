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

        public string Name => ModIdentifier;
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => new string[] { "SuEventBus" };
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            m_modInjector = modInjector;

            // 1) 子系统：按钮行为 → 信道行为（同一个模板，只换 Class 参数）
            // Source: Pak/Database.xml:ButtonBlockBehavior.Class (Guid 4407e30b-...)
            modInjector?.Register("Game.SubsystemButtonBlockBehavior",
                "Channel.SubsystemChannelBlockBehavior");

            // 2) 方块：button 的 index 换成我们的 ButtonBlock
            //    （类名保持 "ButtonBlock"，否则 Pak/CraftingRecipes.xml 的 Result="ButtonBlock" 解析失败）
            modInjector?.RegisterBlock(Game.ButtonBlock.Index, typeof(ButtonBlock), Name);

            // 3) 类名替换必须在 GameDatabase 事件里 Apply（引擎不会自动 Apply）
            eventBus?.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                m_modInjector?.Apply((Database)args[0], Name);
                return new object[] { true, args[0] };
            }, EventPriority.NORMAL);

            // 4) 方块替换必须在 BlocksManager.Initialize 事件里 ApplyBlocks
            //    NORMAL：晚于 ScMultiplayer 的 HIGHEST（ScMultiplayer 已不再抢 142，这里只是保序）
            eventBus?.SubscribeEvent("BlocksManager.Initialize", args =>
            {
                // BlocksManager 会直接索引 item[0]，绝不能返回 null
                if (args == null || args.Length != 1 || !(args[0] is Block[] blocks))
                    return new object[] { false };

                m_modInjector?.ApplyBlocks(blocks, Name);
                return new object[] { false };
            }, EventPriority.NORMAL);

            // 5) 自带中文词条交给翻译 Mod（未安装则纯英文）
            ChannelText.RegisterWithTranslationMod();
        }

        public void OnUnload()
        {
            m_modInjector = null;
        }
    }
}
