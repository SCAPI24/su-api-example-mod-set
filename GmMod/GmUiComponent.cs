using Engine;
using Game;
using GameEntitySystem;
using ScMultiplayer;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TemplatesDatabase;

namespace GmMod
{
    /// <summary>
    /// 玩家实体上的 GM 界面组件：进入世界后在**右上角三个点（MoreButton）那一排**加一个 `GM` 按钮，
    /// 点击打开 GM 菜单：
    ///   · 玩家操作（**所有玩家**）：等级 / 满血 / 送回睡觉点，目标由主机提供的在线玩家列表选择；
    ///   · 世界设置：季节/时段 12 档；
    ///   · 世界设置：昼夜（白天 / 日出 / 日落 / 夜晚 / 循环）——加入后全黑时用它定格到白天；
    ///   · 世界设置：天气总开关（关掉后立刻停雨/停雪/散雾）；
    ///   · 地图方块：破坏准星方块 / 贴着准星面放置手持方块；
    ///   · 回到复活点（自己）。
    ///
    /// 所有操作都只是**提交请求**：主机授信（审批策略 / 受信身份）后由**主机**落地并分发；
    /// 本 mod 不落地、不同步、不分发。
    /// </summary>
    public class GmUiComponent : Component, IUpdateable
    {
        private static readonly Color ToastColor = new Color(235, 225, 255);

        private ComponentPlayer m_componentPlayer;
        private Widget m_moreButton;
        private BevelledButtonWidget m_button;
        private bool m_registrationFailed;

        public UpdateOrder UpdateOrder => UpdateOrder.Views;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(throwOnError: true);
        }

        public override void Dispose()
        {
            if (m_button?.ParentWidget != null)
                m_button.ParentWidget.Children.Remove(m_button);
            m_button = null;
            m_moreButton = null;
            base.Dispose();
        }

        void IUpdateable.Update(float dt)
        {
            if (m_button == null && !m_registrationFailed)
                AttachButton();
            if (m_button != null && m_button.IsClicked)
                ShowMainMenu();
        }

        private void AttachButton()
        {
            GameWidget gameWidget = m_componentPlayer?.GameWidget;
            if (gameWidget == null)
                return;
            // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.m_moreContentsWidget
            // Source: Mod/ScMultiplayer/Func/Component/MultiplayerUiComponent.cs:AttachButtons
            // GM 入口放在**右上角三个点的菜单里面**（`MoreContents`），与联机 mod 的 CR/TA/MP
            // 按钮同一排；三个点那个按钮本身叫 `MoreButton`。
            StackPanelWidget moreContents =
                gameWidget.Children.Find<StackPanelWidget>("MoreContents", true);
            if (moreContents == null)
                return;
            m_button = new BevelledButtonWidget
            {
                Text = "GM",
                Size = new Vector2(76f, 64f),
                Margin = new Vector2(3f, 0f),
                Color = Color.White,
                CenterColor = new Color(120, 60, 140),
                BevelColor = new Color(120, 120, 120)
            };
            moreContents.Children.Add(m_button);
            Log.Information("[GmMod] GM button attached in the More menu (MoreContents), " +
                "next to the multiplayer buttons");
        }

        // ---------------------------------------------------------------- 菜单

        private sealed class MenuEntry
        {
            public string Text;
            public Action Action;
        }

        private void ShowMenu(string title, List<MenuEntry> entries, Vector2? size = null)
        {
            var dialog = new ListSelectionDialog(
                title,
                entries,
                44f,
                item => CreateRow(item as MenuEntry),
                item => (item as MenuEntry)?.Action?.Invoke());
            float availableWidth = MathUtils.Max(
                (ScreensManager.RootWidget?.ActualSize.X ?? 1000f) - 40f, 0f);
            dialog.ContentSize = size ?? new Vector2(MathUtils.Min(760f, availableWidth),
                MathUtils.Min(620f, 44f * (entries?.Count ?? 1) + 24f));
            ListPanelWidget list = dialog.Children.Find<ListPanelWidget>(
                "ListSelectionDialog.List", true);
            if (list != null)
            {
                list.ScrollPosition = 0f;
                list.ScrollSpeed = 0f;
            }
            DialogsManager.ShowDialog(m_componentPlayer?.GuiWidget, dialog);
        }

        private static Widget CreateRow(MenuEntry entry)
        {
            if (entry == null)
                return new LabelWidget { Text = string.Empty };
            return new LabelWidget
            {
                Text = entry.Text,
                Font = ContentManager.Get<Engine.Media.BitmapFont>("Fonts/Pericles18"),
                Color = Color.White,
                HorizontalAlignment = WidgetAlignment.Near,
                VerticalAlignment = WidgetAlignment.Center,
                Margin = new Vector2(8f, 0f),
                Size = new Vector2(680f, 44f)
            };
        }

        private void ShowMainMenu()
        {
            float? current = GmMod.CurrentTimeOfYear();
            var entries = new List<MenuEntry>
            {
                new MenuEntry
                {
                    Text = "★ 立刻回到复活点（自己·睡觉点）",
                    Action = SubmitReturnToRespawn
                },
                new MenuEntry
                {
                    Text = "◆ 玩家操作（所有玩家：等级 / 满血 / 送回睡觉点）…",
                    Action = ShowPlayerTargetMenu
                },
                new MenuEntry
                {
                    Text = "◆ 世界设置：季节/时段（当前：" +
                        (current.HasValue ? GmSeasons.Describe(current.Value) : "未知") + "）…",
                    Action = ShowSeasonMenu
                },
                new MenuEntry
                {
                    Text = "◆ 世界设置：昼夜（当前：" + DescribeCurrentTimeOfDay() + "）…",
                    Action = ShowTimeOfDayMenu
                },
                new MenuEntry
                {
                    Text = "◆ 世界设置：天气（当前：" + DescribeCurrentWeather() + "）…",
                    Action = ShowWeatherMenu
                },
                new MenuEntry
                {
                    Text = "◆ 地图方块：破坏 / 放置（准星处，主机执行）…",
                    Action = ShowBlockMenu
                }
            };
            ShowMenu("GM 工具（所有操作由主机授信并执行）", entries);
        }

        // ---- 玩家操作：先选目标，再选动作

        private void ShowPlayerTargetMenu()
        {
            List<Dictionary<string, object>> online = DescribeOnlinePlayers();
            if (online.Count == 0)
            {
                ShowToast("拿不到在线玩家列表（未联机？）", ToastColor);
                return;
            }
            var entries = new List<MenuEntry>();
            foreach (Dictionary<string, object> player in online)
            {
                int clientId = ReadInt(player, "clientId", -1);
                string name = ReadString(player, "name", "Player");
                bool isSelf = ReadBool(player, "isSelf");
                bool isHost = ReadBool(player, "isHost");
                float level = ReadFloat(player, "level", 1f);
                entries.Add(new MenuEntry
                {
                    Text = (isSelf ? "● " : "   ") + name +
                        (isHost ? "（主机）" : string.Empty) +
                        "   Lv " + level.ToString("0.##", CultureInfo.InvariantCulture) +
                        "   [client " + clientId + "]",
                    Action = () => ShowPlayerActionMenu(clientId, name)
                });
            }
            ShowMenu("选择目标玩家", entries);
        }

        private void ShowPlayerActionMenu(int targetClientId, string targetName)
        {
            var entries = new List<MenuEntry>
            {
                new MenuEntry { Text = "等级 +1",
                    Action = () => SubmitLevel(targetClientId, targetName, 0f, 1f) },
                new MenuEntry { Text = "等级 +5",
                    Action = () => SubmitLevel(targetClientId, targetName, 0f, 5f) },
                new MenuEntry { Text = "等级设为 25",
                    Action = () => SubmitLevel(targetClientId, targetName, 25f, 0f) },
                new MenuEntry { Text = "回满血",
                    Action = () => SubmitHeal(targetClientId, targetName) },
                new MenuEntry { Text = "送回睡觉点",
                    Action = () => SubmitRelocate(targetClientId, targetName) }
            };
            ShowMenu("对 " + targetName + " 执行（主机执行并分发）", entries);
        }

        private void SubmitLevel(int targetClientId, string targetName, float absoluteLevel,
            float amount)
        {
            var request = new PlayerDataModificationRequest { TargetClientId = targetClientId };
            string label;
            if (absoluteLevel > 0f)
            {
                request.SetAbsoluteLevel = true;
                request.Level = absoluteLevel;
                label = "等级设为 " + absoluteLevel.ToString("0.##", CultureInfo.InvariantCulture);
            }
            else
            {
                request.Amount = amount;
                label = "等级 +" + amount.ToString("0.##", CultureInfo.InvariantCulture);
            }
            SubmitPlayerOperation(DataModificationOperationNames.RestoreLevel, request,
                label + " → " + targetName);
        }

        private void SubmitHeal(int targetClientId, string targetName) =>
            SubmitPlayerOperation(DataModificationOperationNames.HealPlayer,
                new PlayerDataModificationRequest { TargetClientId = targetClientId, Amount = 1f },
                "回满血 → " + targetName);

        private void SubmitRelocate(int targetClientId, string targetName) =>
            SubmitPlayerOperation(DataModificationOperationNames.SafeRespawnRelocate,
                new PlayerDataModificationRequest { TargetClientId = targetClientId },
                "送回睡觉点 → " + targetName);

        private void SubmitReturnToRespawn() =>
            SubmitPlayerOperation(DataModificationOperationNames.SafeRespawnRelocate,
                new PlayerDataModificationRequest { TargetClientId = -1 }, "回到复活点（自己）");

        private void SubmitPlayerOperation(string operation, PlayerDataModificationRequest request,
            string label)
        {
            DataModificationSubmitResult result = DataModificationTool.RequestPlayerModification(
                GmOperations.ModId, operation, request);
            Log.Information("[GmMod] Submitted " + operation + " (target=" +
                request.TargetClientId + ") -> " + result.Code + " (" + result.Details + ")");
            ShowToast("已发送主机：" + label + "（等待主机同意）", ToastColor);
        }

        // ---- 世界设置：12 档季节

        private void ShowSeasonMenu()
        {
            float? current = GmMod.CurrentTimeOfYear();
            var entries = new List<MenuEntry>();
            foreach (GmSeasons.Slot slot in GmSeasons.BuildSlots())
            {
                bool isCurrent = current.HasValue &&
                    MathUtils.Abs(current.Value - slot.TimeOfYear) < 0.001f;
                entries.Add(new MenuEntry
                {
                    Text = (isCurrent ? "● " : "   ") + slot.Label + "   [" +
                        GmSeasons.FormatValue(slot.TimeOfYear) + "]",
                    Action = () => SubmitTimeOfYear(slot.TimeOfYear)
                });
            }
            ShowMenu("世界设置：季节/时段（当前：" +
                (current.HasValue ? GmSeasons.Describe(current.Value) : "未知") + "）", entries);
        }

        /// <summary>
        /// 季节/时段：提交联机 mod 的**通用世界设置** operation（`ScMP.Data.WorldSettings`，字段 `TimeOfYear`）。
        /// 联机 mod 不认识"季节"，只按字段名写 `WorldSettings`；落地与分发都在主机侧。
        /// </summary>
        private void SubmitTimeOfYear(float timeOfYear)
        {
            DataModificationSubmitResult result = DataModificationTool.SubmitFast(
                GmOperations.ModId, GmOperations.SetWorldSettings,
                GmPayloadCodec.EncodeTimeOfYear(timeOfYear));
            Log.Information("[GmMod] Submitted " + GmOperations.SetWorldSettings + " " +
                GmOperations.TimeOfYearField + "=" + GmSeasons.FormatValue(timeOfYear) + " -> " +
                result.Code + " (" + result.Details + ")");
            ShowToast("已发送主机：" + GmSeasons.Describe(timeOfYear) + "（等待主机同意）", ToastColor);
        }

        // ---- 世界设置：昼夜 + 天气（同样走联机 mod 的通用世界设置通道）

        private static string DescribeCurrentTimeOfDay()
        {
            TimeOfDayMode? mode = GmMod.CurrentTimeOfDayMode();
            return mode.HasValue ? GmTimeOfDay.ModeName(mode.Value) : "未知";
        }

        private static string DescribeCurrentWeather()
        {
            bool? enabled = GmMod.CurrentWeatherEffectsEnabled();
            return !enabled.HasValue ? "未知" : enabled.Value ? "开（会下雨/下雪/起雾）" : "关（无天气）";
        }

        private void ShowTimeOfDayMenu()
        {
            TimeOfDayMode? current = GmMod.CurrentTimeOfDayMode();
            var entries = new List<MenuEntry>();
            foreach (GmTimeOfDay.Slot slot in GmTimeOfDay.BuildSlots())
            {
                bool isCurrent = current.HasValue && current.Value == slot.Mode;
                entries.Add(new MenuEntry
                {
                    Text = (isCurrent ? "● " : "   ") + slot.Label,
                    Action = () => SubmitTimeOfDay(slot.Mode)
                });
            }
            ShowMenu("世界设置：昼夜（当前：" + DescribeCurrentTimeOfDay() + "）", entries);
        }

        /// <summary>
        /// 昼夜：提交 `ScMP.Data.WorldSettings` 的 `TimeOfDayMode`（枚举名）。
        /// 引擎 `SubsystemTimeOfDay.Update` 每帧读该字段，所以改成 Day/Night/… 会**立刻**定格亮度
        /// （加入后一片漆黑时设为「白天」即可看清），主机再随世界信息广播同步给所有客户端。
        /// </summary>
        private void SubmitTimeOfDay(TimeOfDayMode mode)
        {
            DataModificationSubmitResult result = DataModificationTool.SubmitFast(
                GmOperations.ModId, GmOperations.SetWorldSettings,
                GmPayloadCodec.EncodeWorldSetting(GmOperations.TimeOfDayModeField, mode.ToString()));
            Log.Information("[GmMod] Submitted " + GmOperations.SetWorldSettings + " " +
                GmOperations.TimeOfDayModeField + "=" + mode + " -> " + result.Code + " (" +
                result.Details + ")");
            ShowToast("已发送主机：昼夜 " + GmTimeOfDay.ModeName(mode) + "（等待主机同意）", ToastColor);
        }

        private void ShowWeatherMenu()
        {
            var entries = new List<MenuEntry>
            {
                new MenuEntry
                {
                    Text = "天气效果：关（立刻停雨/停雪/散雾；写进世界设置并持久保存）",
                    Action = () => SubmitWeatherEffects(false)
                },
                new MenuEntry
                {
                    Text = "天气效果：开（恢复正常降雨 / 降雪 / 雾）",
                    Action = () => SubmitWeatherEffects(true)
                }
            };
            ShowMenu("世界设置：天气（当前：" + DescribeCurrentWeather() + "）", entries);
        }

        /// <summary>
        /// 天气总开关：提交 `ScMP.Data.WorldSettings` 的 `AreWeatherEffectsEnabled`。
        /// 主机侧 `SubsystemWeather.Update` 在关闭时把降水强度**直接清零**（立即停雨，雾同理），
        /// 随后随世界信息广播同步给所有客户端；关掉后整个世界的天气效果都不再模拟。
        /// </summary>
        private void SubmitWeatherEffects(bool enabled)
        {
            DataModificationSubmitResult result = DataModificationTool.SubmitFast(
                GmOperations.ModId, GmOperations.SetWorldSettings,
                GmPayloadCodec.EncodeWorldSetting(GmOperations.WeatherEffectsField,
                    enabled ? "true" : "false"));
            Log.Information("[GmMod] Submitted " + GmOperations.SetWorldSettings + " " +
                GmOperations.WeatherEffectsField + "=" + enabled + " -> " + result.Code + " (" +
                result.Details + ")");
            ShowToast("已发送主机：天气 " + (enabled ? "开" : "关") + "（等待主机同意）", ToastColor);
        }

        // ---- 地图方块：破坏 / 放置（主机执行 + 地形广播）

        private void ShowBlockMenu()
        {
            var entries = new List<MenuEntry>
            {
                new MenuEntry
                {
                    Text = "破坏准星指向的方块（无视距离）",
                    Action = () => SubmitBlockEdit(place: false)
                },
                new MenuEntry
                {
                    Text = "贴着准星面放置手持方块",
                    Action = () => SubmitBlockEdit(place: true)
                }
            };
            ShowMenu("地图方块（主机执行并广播）", entries);
        }

        private void SubmitBlockEdit(bool place)
        {
            ComponentPlayer player = m_componentPlayer;
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(false);
            if (player?.GameWidget?.ActiveCamera == null || terrain == null)
                return;
            Vector3 eye = player.ComponentCreatureModel?.EyePosition ??
                player.ComponentBody.Position;
            Vector3 direction = player.GameWidget.ActiveCamera.ViewDirection;
            TerrainRaycastResult? hit = terrain.Raycast(eye, eye + direction * 64f, false, false,
                null);
            if (!hit.HasValue)
            {
                ShowToast("准星没有指向方块", ToastColor);
                return;
            }
            CellFace face = hit.Value.CellFace;
            int x = face.X;
            int y = face.Y;
            int z = face.Z;
            int contents = 0;
            int data = 0;
            if (place)
            {
                // 贴着命中面放置：沿法线方向偏移一格
                switch (face.Face)
                {
                    case 0: z++; break;
                    case 1: x++; break;
                    case 2: z--; break;
                    case 3: x--; break;
                    case 4: y++; break;
                    case 5: y--; break;
                }
                int activeValue = player.ComponentMiner?.ActiveBlockValue ?? 0;
                contents = Terrain.ExtractContents(activeValue);
                data = Terrain.ExtractData(activeValue);
                if (contents == 0)
                {
                    ShowToast("手上没有可放置的方块", ToastColor);
                    return;
                }
            }
            byte[] payload = GmPayloadCodec.EncodeCell(x, y, z, contents, data);
            DataModificationSubmitResult result = DataModificationTool.SubmitFast(
                GmOperations.ModId, GmOperations.SetCells, payload);
            Log.Information("[GmMod] Submitted " + GmOperations.SetCells + " cell=" + x + "," + y +
                "," + z + " contents=" + contents + " -> " + result.Code + " (" +
                result.Details + ")");
            ShowToast("已发送主机：" + (place ? "放置方块" : "破坏方块") + " " + x + "," + y + "," + z +
                "（等待主机同意）", ToastColor);
        }

        // ---------------------------------------------------------------- 底部冒泡反馈

        /// <summary>
        /// 在**本地玩家**的 HUD 上弹一条底部小提示（冒泡）。
        ///
        /// 本地玩家怎么找：`SubsystemGameWidgets.GameWidgets` 里只有**真正渲染**的 GameWidget，
        /// 联机 mod 建的网络化身会把它的 GameWidget 从这里面移除（`RemoveGameWidget`），
        /// 所以这里剩下的就是本地玩家。
        /// ⚠ 不要用 `PlayerData.InputDevice != None` 判断：实测本 mod 里主/客双方的
        /// `InputDevice` 都是 `None`，那样会一个都找不到（冒泡静默不出现）。
        /// </summary>
        internal static void ShowToast(string text, Color color)
        {
            try
            {
                ComponentPlayer local = ResolveLocalPlayer();
                // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
                local?.ComponentGui?.DisplaySmallMessage(text, color, false, false);
            }
            catch (Exception ex)
            {
                Log.Warning("[GmMod] Toast failed: " + ex.Message);
            }
        }

        private static ComponentPlayer ResolveLocalPlayer()
        {
            SubsystemGameWidgets widgets =
                GameManager.Project?.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null)
                return null;
            foreach (GameWidget widget in widgets.GameWidgets)
            {
                ComponentPlayer player = widget?.PlayerData?.ComponentPlayer;
                if (player?.ComponentGui != null)
                    return player;
            }
            return null;
        }

        // ---------------------------------------------------------------- 在线玩家列表

        // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:
        // DataModificationTool.DescribeOnlinePlayers
        private static List<Dictionary<string, object>> DescribeOnlinePlayers()
        {
            try
            {
                return DataModificationTool.DescribeOnlinePlayers() ??
                    new List<Dictionary<string, object>>();
            }
            catch (Exception ex)
            {
                Log.Warning("[GmMod] Online player list failed: " + ex.Message);
                return new List<Dictionary<string, object>>();
            }
        }

        private static int ReadInt(Dictionary<string, object> values, string key, int fallback) =>
            values != null && values.TryGetValue(key, out object value) && value != null
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : fallback;

        private static float ReadFloat(Dictionary<string, object> values, string key,
            float fallback) => values != null && values.TryGetValue(key, out object value) &&
                value != null
                    ? Convert.ToSingle(value, CultureInfo.InvariantCulture)
                    : fallback;

        private static string ReadString(Dictionary<string, object> values, string key,
            string fallback) => values != null && values.TryGetValue(key, out object value)
                ? value as string ?? fallback
                : fallback;

        private static bool ReadBool(Dictionary<string, object> values, string key) =>
            values != null && values.TryGetValue(key, out object value) && value != null &&
            Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }
}
