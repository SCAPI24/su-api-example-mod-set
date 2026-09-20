using Engine;
using Game;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 游戏自身的反馈消息读取（只读）。
    ///
    /// 这是 AI 最直接的教学信号：游戏会通过"小提示"和"屏幕叠加消息"告诉玩家发生了什么——
    /// 例如 "You will faint, go to sleep!"、"Too uncomfortable to sleep"、"Can't go no more"、
    /// "You fell unconscious from lack of sleep"、"Brrr, wet!"、"You need a better shelter"。
    ///
    /// 两条来源：
    ///   1. `ComponentGui.DisplaySmallMessage` → `MessageWidget`，文本作为子 LabelWidget 挂在控件树上。
    ///      Source: Survivalcraft/Game/MessageWidget.cs:70-92、ComponentGui.cs:189-191, 268
    ///   2. `ComponentScreenOverlays.Message` / `FloatingMessage`（睡觉、溺水等状态提示）。
    ///      Source: Survivalcraft/Game/ComponentScreenOverlays.cs:46-52
    /// </summary>
    internal static class MessageObserver
    {
        public static Dictionary<string, object> Describe()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["messages"] = CollectSmallMessages(),
                ["overlay"] = CollectOverlay()
            };
            return result;
        }

        /// <summary>当前屏幕上的"小提示"文本（4~6 秒后自动消失）。</summary>
        public static List<string> CollectSmallMessages()
        {
            var texts = new List<string>();
            try
            {
                ContainerWidget root = ScreensManager.RootWidget;
                if (root == null)
                    return texts;

                foreach (Widget widget in root.AllChildren)
                {
                    if (!(widget is MessageWidget messageWidget))
                        continue;
                    foreach (Widget child in messageWidget.Children)
                    {
                        if (child is LabelWidget label &&
                            !string.IsNullOrEmpty(label.Text) &&
                            !texts.Contains(label.Text))
                        {
                            texts.Add(label.Text);
                        }
                    }
                }
            }
            catch
            {
            }
            return texts;
        }

        /// <summary>屏幕叠加层消息（睡觉提示 / Zzz / 黑屏红屏等状态表现）。</summary>
        public static Dictionary<string, object> CollectOverlay()
        {
            try
            {
                ComponentPlayer player = FindPlayer();
                if (player == null)
                    return null;
                ComponentScreenOverlays overlays =
                    player.Entity.FindComponent<ComponentScreenOverlays>(false);
                if (overlays == null)
                    return null;

                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["message"] = string.IsNullOrEmpty(overlays.Message) ? null : overlays.Message,
                    ["messageFactor"] = overlays.MessageFactor,
                    ["floatingMessage"] = string.IsNullOrEmpty(overlays.FloatingMessage)
                        ? null
                        : overlays.FloatingMessage,
                    ["floatingMessageFactor"] = overlays.FloatingMessageFactor,
                    ["blackoutFactor"] = overlays.BlackoutFactor,
                    ["redoutFactor"] = overlays.RedoutFactor,
                    ["greenoutFactor"] = overlays.GreenoutFactor,
                    ["iceFactor"] = overlays.IceFactor
                };
            }
            catch
            {
                return null;
            }
        }

        internal static ComponentPlayer FindPlayer()
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
