using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 事件环：把"发生了什么"按帧记入定长缓冲，客户端用 sinceSeq 增量拉取。
    ///
    /// 为什么需要：AI 需要确认自己的动作产生了什么结果（点了哪个按钮、屏幕是否切换、
    /// 面板是否打开、是否受伤），而不是靠轮询比对整棵控件树。
    ///
    /// 记录的事件：
    ///   ui.click       本帧被点击的 UI 元素（玩家点或桥注入点都算）
    ///   world.dig / world.hit / world.interact / world.aim   世界交互射线的上升沿
    ///   screen.changed / modal.opened / modal.closed / dialog.shown / dialog.hidden
    ///   world.loaded / world.unloaded / player.damaged / player.died
    ///
    /// Tick 由 Frame.Update 订阅在游戏线程调用（doc/cmd-bridge-plan.md §2.4）。
    /// </summary>
    internal sealed class EventRecorder
    {
        private readonly int m_capacity;
        private readonly List<Dictionary<string, object>> m_events;
        private long m_seq;
        private string m_lastScreen;
        private string m_lastModal;
        private int m_lastDialogCount = -1;
        private bool m_lastWorldLoaded;
        private float m_lastHealth = float.NaN;
        private int m_lastActiveSlot = -1;
        private bool m_lastSleeping;
        private readonly List<string> m_lastMessages = new List<string>();
        private readonly List<Widget> m_lastClicked = new List<Widget>();
        private readonly List<Widget> m_lastTapped = new List<Widget>();
        private bool m_lastDig;
        private bool m_lastHit;
        private bool m_lastInteract;
        private bool m_lastAim;
        private string m_lastError;

        public EventRecorder(int capacity)
        {
            m_capacity = MathUtils.Clamp(capacity, 16, 8192);
            m_events = new List<Dictionary<string, object>>(m_capacity);
        }

        public long LastSeq
        {
            get
            {
                lock (m_events)
                    return m_seq;
            }
        }

        public string LastError => m_lastError;

        // ---------------------------------------------------------------- 记录

        public void Tick()
        {
            try
            {
                TickCore();
                m_lastError = null;
            }
            catch (Exception exception)
            {
                string message = exception.GetType().Name + ": " + exception.Message;
                if (!string.Equals(message, m_lastError, StringComparison.Ordinal))
                {
                    m_lastError = message;
                    Log.Warning("[CmdBridge] event recorder failed: " + message);
                }
            }
        }

        private void TickCore()
        {
            int frame = Time.FrameIndex;

            bool worldLoaded = GameManager.Project != null;
            if (worldLoaded != m_lastWorldLoaded)
            {
                Add(frame, worldLoaded ? "world.loaded" : "world.unloaded", null);
                m_lastWorldLoaded = worldLoaded;
            }

            string screen = UiInspector.ScreenName();
            if (!string.Equals(screen, m_lastScreen, StringComparison.Ordinal))
            {
                var data = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["from"] = m_lastScreen,
                    ["to"] = screen
                };
                Add(frame, "screen.changed", data);
                m_lastScreen = screen;
            }

            ComponentPlayer player = FindPlayer();

            string modal = null;
            if (player != null)
            {
                try
                {
                    ComponentGui gui = player.ComponentGui;
                    if (gui != null && gui.ModalPanelWidget != null)
                        modal = gui.ModalPanelWidget.GetType().Name;
                }
                catch
                {
                }
            }
            if (!string.Equals(modal, m_lastModal, StringComparison.Ordinal))
            {
                var data = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["panel"] = modal,
                    ["previous"] = m_lastModal
                };
                Add(frame, modal == null ? "modal.closed" : "modal.opened", data);
                m_lastModal = modal;
            }

            try
            {
                int dialogs = DialogsManager.Dialogs.Count;
                if (dialogs != m_lastDialogCount)
                {
                    if (m_lastDialogCount >= 0)
                    {
                        var data = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["count"] = dialogs
                        };
                        Add(frame, dialogs > m_lastDialogCount ? "dialog.shown" : "dialog.hidden", data);
                    }
                    m_lastDialogCount = dialogs;
                }
            }
            catch
            {
            }

            RecordUiClicks(frame);
            RecordRawTaps(frame, player);
            RecordPlayerEvents(frame, player);
            RecordMessages(frame);
        }

        private void RecordUiClicks(int frame)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                return;

            // 边沿去重：只上报"本帧新出现"的点击。
            //
            // 为什么必须去重：ScreensManager.SwitchScreen 会把 RootWidget.IsUpdateEnabled 置 false
            // （ScreensManager.cs:81），转屏动画期间整棵控件树不再 Update，WidgetInput 因此不会清空，
            // 旧屏幕控件的 ClickableWidget.IsClicked 会冻结在 true；若不去重，一次点击会在动画的
            // 每一帧被重复上报（实测一次点击报出 22 条）。
            var clickedNow = new List<Widget>();
            foreach (Widget widget in root.AllChildren)
            {
                if (!(widget is ClickableWidget clickable) || !clickable.IsClicked)
                    continue;
                clickedNow.Add(clickable);
                if (m_lastClicked.Contains(clickable))
                    continue;

                // 报告语义上的拥有者（例如 BitmapButtonWidget），而不是内部模板子控件。
                Widget owner = clickable;
                for (Widget parent = clickable.ParentWidget; parent != null; parent = parent.ParentWidget)
                {
                    if (parent is ButtonWidget)
                    {
                        owner = parent;
                        break;
                    }
                }

                var data = new Dictionary<string, object>(StringComparer.Ordinal);
                try
                {
                    Dictionary<string, object> described = UiInspector.DescribeElement(owner, 0);
                    data["path"] = described["path"];
                    data["name"] = described["name"];
                    data["type"] = described["type"];
                    data["text"] = described["text"];
                    data["clientPoint"] = described["clientPoint"];
                    data["screenPoint"] = described["screenPoint"];
                }
                catch
                {
                    data["path"] = UiInspector.BuildPath(owner);
                    data["type"] = owner.GetType().Name;
                }
                Add(frame, "ui.click", data);
            }

            // 记录本帧的点击集合，供下一帧做边沿判断。
            m_lastClicked.Clear();
            m_lastClicked.AddRange(clickedNow);
        }

        /// <summary>
        /// 游戏自己的提示消息当作事件上报。小提示只存在 4~6 秒，靠轮询会漏；
        /// 事件环保证 AI 不会错过 "You will faint, go to sleep!" 这类关键警告。
        /// </summary>
        private void RecordMessages(int frame)
        {
            var current = new List<string>();
            try
            {
                current.AddRange(MessageObserver.CollectSmallMessages());
                Dictionary<string, object> overlay = MessageObserver.CollectOverlay();
                if (overlay != null)
                {
                    if (overlay["message"] is string overlayMessage)
                        current.Add(overlayMessage);
                    if (overlay["floatingMessage"] is string floatingMessage)
                        current.Add(floatingMessage);
                }
            }
            catch
            {
            }

            for (int i = 0; i < current.Count; i++)
            {
                string text = current[i];
                if (m_lastMessages.Contains(text))
                    continue;
                var data = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["text"] = text
                };
                Add(frame, "ui.message", data);
            }

            m_lastMessages.Clear();
            m_lastMessages.AddRange(current);
        }

        /// <summary>
        /// 原始点击事件：只要游戏的输入层级在本帧派生了 Click，就把它落到具体控件上上报。
        ///
        /// 为什么需要它：背包槽位、滑条、列表项、文本框这类控件**不是** ClickableWidget，
        /// 它们自己读 Press/Tap/Click，因此 ui.click 覆盖不到；而 AI 需要知道"我点了哪个槽位"。
        /// 检查的层级输入：根控件树（菜单/对话框）+ 每个玩家的 GameWidget（HUD 与模态面板）。
        /// </summary>
        private void RecordRawTaps(int frame, ComponentPlayer player)
        {
            var inputs = new List<WidgetInput>();
            try
            {
                ContainerWidget root = ScreensManager.RootWidget;
                if (root != null && root.WidgetsHierarchyInput != null)
                    inputs.Add(root.WidgetsHierarchyInput);
            }
            catch
            {
            }
            try
            {
                if (player != null && player.GameWidget != null &&
                    player.GameWidget.WidgetsHierarchyInput != null)
                {
                    inputs.Add(player.GameWidget.WidgetsHierarchyInput);
                }
            }
            catch
            {
            }

            var tappedNow = new List<Widget>();
            IModParentField fields = null;
            for (int i = 0; i < inputs.Count; i++)
            {
                WidgetInput input = inputs[i];
                object clickValue = null;
                try
                {
                    if (fields == null)
                        fields = ModManager.Instance.ModParentField;
                    clickValue = fields.GetParentField(input, "Click");
                }
                catch
                {
                    continue;
                }
                if (!(clickValue is Segment2 segment))
                    continue;

                Widget target = null;
                try
                {
                    ContainerWidget root = ScreensManager.RootWidget;
                    if (root != null)
                        target = root.HitTestGlobal(segment.End);
                }
                catch
                {
                }
                if (target == null)
                    continue;

                tappedNow.Add(target);
                if (m_lastTapped.Contains(target))
                    continue;

                Widget owner = target;
                for (Widget parent = target.ParentWidget; parent != null;
                    parent = parent.ParentWidget)
                {
                    if (parent is ButtonWidget || parent is InventorySlotWidget)
                    {
                        owner = parent;
                        break;
                    }
                }

                var data = new Dictionary<string, object>(StringComparer.Ordinal);
                try
                {
                    Dictionary<string, object> described = UiInspector.DescribeElement(owner, 0);
                    data["path"] = described["path"];
                    data["name"] = described["name"];
                    data["type"] = described["type"];
                    data["text"] = described["text"];
                    data["hitType"] = target.GetType().Name;
                    data["clientPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = segment.End.X,
                        ["y"] = segment.End.Y
                    };
                }
                catch
                {
                    data["path"] = UiInspector.BuildPath(owner);
                    data["type"] = owner.GetType().Name;
                }
                Add(frame, "ui.tap", data);
            }

            m_lastTapped.Clear();
            m_lastTapped.AddRange(tappedNow);
        }

        private void RecordPlayerEvents(int frame, ComponentPlayer player)
        {
            if (player == null)
                return;

            try
            {
                PlayerInput input = player.ComponentInput.PlayerInput;
                RecordEdge(frame, "world.dig", input.Dig.HasValue, ref m_lastDig);
                RecordEdge(frame, "world.hit", input.Hit.HasValue, ref m_lastHit);
                RecordEdge(frame, "world.interact", input.Interact.HasValue, ref m_lastInteract);
                RecordEdge(frame, "world.aim", input.Aim.HasValue, ref m_lastAim);
            }
            catch
            {
            }

            try
            {
                IInventory inventory = player.ComponentMiner != null
                    ? player.ComponentMiner.Inventory
                    : null;
                if (inventory != null)
                {
                    int slot = inventory.ActiveSlotIndex;
                    if (m_lastActiveSlot >= 0 && slot != m_lastActiveSlot)
                    {
                        var data = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["from"] = m_lastActiveSlot,
                            ["to"] = slot
                        };
                        Add(frame, "player.slotChanged", data);
                    }
                    m_lastActiveSlot = slot;
                }
            }
            catch
            {
            }

            try
            {
                // 入睡/醒来（含"困到昏睡"——此时 allowManualWakeUp=false，AI 无法用输入叫醒）。
                ComponentSleep sleep = player.Entity.FindComponent<ComponentSleep>(false);
                if (sleep != null)
                {
                    bool sleeping = sleep.IsSleeping;
                    if (sleeping != m_lastSleeping)
                    {
                        var data = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["sleepFactor"] = sleep.SleepFactor
                        };
                        try
                        {
                            data["allowManualWakeUp"] = ModManager.Instance.ModParentField
                                .GetParentField<bool>(sleep, "m_allowManualWakeUp", typeof(ComponentSleep));
                        }
                        catch
                        {
                        }
                        Add(frame, sleeping ? "player.sleepStarted" : "player.sleepEnded", data);
                        m_lastSleeping = sleeping;
                    }
                }
            }
            catch
            {
            }

            try
            {
                ComponentHealth health = player.ComponentHealth;
                if (health != null)
                {
                    float current = health.Health;
                    if (!float.IsNaN(m_lastHealth))
                    {
                        if (current < m_lastHealth - 0.0001f)
                        {
                            var data = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["from"] = m_lastHealth,
                                ["to"] = current,
                                ["delta"] = current - m_lastHealth
                            };
                            Add(frame, "player.damaged", data);
                        }
                        else if (current > m_lastHealth + 0.0001f)
                        {
                            // 自然回血/进食回血都要让 AI 看到（它自己不能直接加血，只能等或吃）。
                            var data = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["from"] = m_lastHealth,
                                ["to"] = current,
                                ["delta"] = current - m_lastHealth
                            };
                            Add(frame, "player.healed", data);
                        }
                        if (current <= 0f && m_lastHealth > 0f)
                            Add(frame, "player.died", null);
                    }
                    m_lastHealth = current;
                }
            }
            catch
            {
            }
        }

        private void RecordEdge(int frame, string kind, bool active, ref bool previous)
        {
            if (active && !previous)
                Add(frame, kind, null);
            previous = active;
        }

        private void Add(int frame, string kind, Dictionary<string, object> data)
        {
            var entry = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["kind"] = kind,
                ["frame"] = frame
            };
            if (data != null)
            {
                foreach (KeyValuePair<string, object> pair in data)
                    entry[pair.Key] = pair.Value;
            }

            lock (m_events)
            {
                entry["seq"] = ++m_seq;
                m_events.Add(entry);
                while (m_events.Count > m_capacity)
                    m_events.RemoveAt(0);
            }
        }

        // ---------------------------------------------------------------- 读取

        public Dictionary<string, object> Read(long sinceSeq, int max)
        {
            int limit = MathUtils.Clamp(max, 1, 512);
            var selected = new List<Dictionary<string, object>>();
            long lastSeq;
            long firstSeq = 0;

            lock (m_events)
            {
                lastSeq = m_seq;
                for (int i = 0; i < m_events.Count; i++)
                {
                    Dictionary<string, object> entry = m_events[i];
                    if (i == 0)
                        firstSeq = (long)entry["seq"];
                    if ((long)entry["seq"] <= sinceSeq)
                        continue;
                    if (selected.Count >= limit)
                        break;
                    selected.Add(entry);
                }
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sinceSeq"] = sinceSeq,
                ["lastSeq"] = lastSeq,
                ["firstAvailableSeq"] = firstSeq,
                ["lostEvents"] = sinceSeq > 0 && sinceSeq < firstSeq - 1,
                ["events"] = selected,
                ["capacity"] = m_capacity
            };
        }

        private static ComponentPlayer FindPlayer()
        {
            try
            {
                if (GameManager.Project == null)
                    return null;
                SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
                if (players == null || players.ComponentPlayers.Count == 0)
                    return null;
                return players.ComponentPlayers[0];
            }
            catch
            {
                return null;
            }
        }
    }
}
