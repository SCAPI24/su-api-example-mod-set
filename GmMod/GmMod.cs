using Engine;
using Game;
using ScMultiplayer;
using SuAPI;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace GmMod
{
    /// <summary>
    /// GM 工具（独立 mod，**纯客户端 UI**）。
    ///
    /// 它只做三件事：加一个 HUD 按钮、给出菜单、把"要改什么"通过联机 mod 的数据修改通道提交。
    /// **不做**任何落地、同步或分发：
    ///   · 主机收到请求 → 按 `dataModificationMode` 审批（受信任客户端自动同意）→ **主机自己**把
    ///     `WorldSettings` 改掉（`ScMP.Data.WorldSettings` 是联机 mod 的通用数据修改，联机 mod
    ///     不认识"季节"，只是按字段名写设置）；
    ///   · 主机随后随世界信息广播把整份设置快照发给**所有客户端**，客户端各自应用。
    /// 因此**主机端不需要安装 GmMod**，本 mod 也不必安装在主机上。
    /// </summary>
    public class GmMod : IMod
    {
        private const string GameDatabaseEvent = "GameDatabase.GameDatabase";

        /// <summary>数据修改结果事件（与 ScMultiplayer.DataModificationEvents.Result 同名）。</summary>
        private const string DataModificationResultEvent = "ScMultiplayer.DataModification.Result";

        private readonly List<EventSubscriptionToken> m_tokens = new List<EventSubscriptionToken>();

        private IModEventBus m_eventBus;

        public static GmMod Current { get; private set; }

        /// <summary>最近一次数据修改结果（供排查/显示）。</summary>
        public string LastResultText { get; private set; } = "尚无请求";

        public string Name => "GmMod";

        public string Version => "1.0.0";

        public IEnumerable<string> Dependencies => new[] { "ScMultiplayer" };

        public bool IsEnabled { get; set; } = true;

        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            Current = this;
            if (eventBus == null)
            {
                Log.Warning("[GmMod] No event bus: GM operations are unavailable.");
                return;
            }
            m_eventBus = eventBus;
            m_tokens.Add(eventBus.SubscribeEvent(GameDatabaseEvent,
                args => HandleGameDatabase(args != null && args.Length > 0 ? args[0] as Database : null),
                EventPriority.HIGHEST));
            // 只订阅回执（冒泡提醒）；落地/分发都在主机侧的联机 mod，本 mod 不参与。
            m_tokens.Add(eventBus.SubscribeEvent(DataModificationResultEvent,
                args =>
                {
                    HandleDataModificationResult(args != null && args.Length > 0
                        ? args[0] as DataModificationResult : null);
                    return Array.Empty<object>();
                },
                EventPriority.HIGHEST));
            Log.Information("[GmMod] Loaded (client-side UI only). GM operation: " +
                GmOperations.SetWorldSettings + " [" + GmOperations.TimeOfYearField + "]");
        }

        public void OnUnload()
        {
            foreach (EventSubscriptionToken token in m_tokens)
            {
                try
                {
                    m_eventBus?.UnsubscribeEvent(token);
                }
                catch (Exception ex)
                {
                    Log.Warning("[GmMod] Unsubscribe failed: " + ex.Message);
                }
            }
            m_tokens.Clear();
            m_eventBus = null;
            Current = null;
        }

        // ---------------------------------------------------------------- 回执（只冒泡，不弹窗）

        private void HandleDataModificationResult(DataModificationResult result)
        {
            if (result == null)
                return;
            LastResultText = result.Operation + " -> " + result.Code +
                (string.IsNullOrEmpty(result.Details) ? string.Empty : " (" + result.Details + ")");
            Log.Information("[GmMod] Result: " + LastResultText);
            // 季节值本身不需要在这里同步：主机广播会把整份 WorldSettings 发给所有客户端，
            // 客户端由联机 mod 自行应用（见 ScMultiplayer.ApplyRemoteWeatherState）。
            GmUiComponent.ShowToast(BuildResultToast(result), ResultToastColor);
        }

        private static readonly Color ResultToastColor = new Color(210, 255, 210);

        /// <summary>把回执编成一条中文冒泡文案（Applied/Rejected/… 都覆盖）。</summary>
        private static string BuildResultToast(DataModificationResult result)
        {
            string what = string.Equals(result.Operation, GmOperations.SetWorldSettings,
                StringComparison.Ordinal)
                ? "季节/时段"
                : string.Equals(result.Operation, DataModificationOperationNames.SafeRespawnRelocate,
                    StringComparison.Ordinal)
                    ? "回到复活点"
                    : result.Operation;
            string outcome = result.Code switch
            {
                DataModificationResultCode.Applied => "主机已同意并生效",
                DataModificationResultCode.Rejected => "主机拒绝了该操作",
                DataModificationResultCode.Accepted => "主机已接受",
                DataModificationResultCode.Busy => "主机忙，请稍后再试",
                DataModificationResultCode.Invalid => "请求无效",
                DataModificationResultCode.NotSupported => "主机不支持该操作",
                DataModificationResultCode.Cancelled => "请求已取消",
                _ => "主机处理失败"
            };
            return what + "：" + outcome;
        }

        /// <summary>当前世界的季节值（菜单里用来标注"当前"）。</summary>
        public static float? CurrentTimeOfYear()
        {
            SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            return gameInfo?.WorldSettings.TimeOfYear;
        }

        // ---------------------------------------------------------------- 玩家实体上挂 UI 组件

        // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerLifecycle.cs:HandleGameDatabase
        // Source: Mod/WatchMod/Plug/WatchMod.cs:WatchMod.HandleGameDatabase
        private object[] HandleGameDatabase(Database database)
        {
            if (database == null)
                return new object[] { false, database };
            try
            {
                var template = new DatabaseObject(
                    database.FindDatabaseObjectType("ComponentTemplate", true),
                    new Guid("7c0f5b31-6c2a-4f5e-9d3a-51b0c2e7a410"),
                    "GmToolsUI", null);
                template.ExplicitInheritanceParent = database.FindDatabaseObject(
                    new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
                    database.FindDatabaseObjectType("ComponentTemplate", true), true);
                template.NestingParent = database.FindDatabaseObject(
                    "Gameplay", database.FindDatabaseObjectType("Folder", true), true);

                var componentClass = new DatabaseObject(
                    database.FindDatabaseObjectType("Parameter", true),
                    new Guid("2a6d9d84-3b57-4c1f-8f60-9a0e2d7c1b52"),
                    "Class", "GmMod.GmUiComponent");
                componentClass.NestingParent = template;

                var member = new DatabaseObject(
                    database.FindDatabaseObjectType("MemberComponentTemplate", true),
                    new Guid("b18f4a26-5d70-4a93-9c8e-7f31b6d40c63"),
                    "GmToolsUI", null);
                member.ExplicitInheritanceParent = template;
                member.NestingParent = database.FindDatabaseObject(
                    "Player", database.FindDatabaseObjectType("EntityTemplate", true), true);

                Log.Information("[GmMod] Player UI component registered");
            }
            catch (Exception ex)
            {
                Log.Error("[GmMod] Failed to register the player UI component: " + ex.Message);
            }
            return new object[] { true, database };
        }
    }
}
