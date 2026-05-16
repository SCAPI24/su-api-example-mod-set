using Engine;
using Engine.Input;
using Game;
using GameEntitySystem;
using System;
using System.Collections.Generic;
using System.Text;
using TemplatesDatabase;

namespace ConsoleMod
{
    public class ConsoleSubsystemGameWidgets : SubsystemGameWidgets, IUpdateable
    {
        private bool m_consoleOpen = false;
        private bool m_justToggled = false;
        private StringBuilder m_inputText = new StringBuilder();
        private List<string> m_history = new List<string>();
        private const int MaxHistoryLines = 50;

        // UI elements
        private CanvasWidget m_consolePanel;
        private RectangleWidget m_background;
        private LabelWidget m_contentLabel;
        private bool m_uiAttached = false;

        public new UpdateOrder UpdateOrder => UpdateOrder.Views;

        public override void Update(float dt)
        {
            base.Update(dt);

            // Source: Engine.Input.Key.Tilde — grave accent / tilde key
            if (Keyboard.IsKeyDownOnce(Key.Tilde))
            {
                m_consoleOpen = !m_consoleOpen;
                m_justToggled = true;
                if (m_consoleOpen)
                    AttachUI();
                else
                    DetachUI();
            }

            if (m_consoleOpen)
            {
                CaptureInput();
                UpdateDisplay();
            }
        }

        private void AttachUI()
        {
            if (m_uiAttached) return;
            if (GameWidgets.Count == 0) return;

            // Source: GameWidget.GuiWidget — HUD container
            ContainerWidget guiWidget = GameWidgets[0].GuiWidget;

            // Console panel — bottom of screen
            m_consolePanel = new CanvasWidget();
            m_consolePanel.VerticalAlignment = WidgetAlignment.Far;
            m_consolePanel.Size = new Vector2(-1f, 280f);

            // Semi-transparent black background
            m_background = new RectangleWidget();
            m_background.FillColor = new Color(0, 0, 0, 180);
            m_background.OutlineColor = new Color(128, 128, 128, 200);
            m_background.OutlineThickness = 1f;
            m_background.HorizontalAlignment = WidgetAlignment.Stretch;
            m_background.VerticalAlignment = WidgetAlignment.Stretch;

            // Content label — history + input
            m_contentLabel = new LabelWidget();
            m_contentLabel.Color = Color.White;
            m_contentLabel.FontScale = 0.6f;
            m_contentLabel.HorizontalAlignment = WidgetAlignment.Near;
            m_contentLabel.VerticalAlignment = WidgetAlignment.Far;

            m_consolePanel.Children.Add(m_background);
            m_consolePanel.Children.Add(m_contentLabel);
            CanvasWidget.SetPosition(m_contentLabel, new Vector2(10f, 10f));
            guiWidget.Children.Add(m_consolePanel);

            m_uiAttached = true;
            m_inputText.Clear();
        }

        private void DetachUI()
        {
            if (!m_uiAttached || m_consolePanel == null) return;
            if (GameWidgets.Count > 0)
            {
                ContainerWidget guiWidget = GameWidgets[0].GuiWidget;
                guiWidget.Children.Remove(m_consolePanel);
            }
            m_consolePanel = null;
            m_background = null;
            m_contentLabel = null;
            m_uiAttached = false;
        }

        private void CaptureInput()
        {
            // Skip input on toggle frame to avoid capturing tilde character
            if (m_justToggled)
            {
                m_justToggled = false;
                return;
            }

            // Escape closes console
            if (Keyboard.IsKeyDownOnce(Key.Escape))
            {
                m_consoleOpen = false;
                DetachUI();
                return;
            }

            // Enter executes command
            if (Keyboard.IsKeyDownOnce(Key.Enter))
            {
                string cmd = m_inputText.ToString();
                ExecuteCommand(cmd);
                m_inputText.Clear();
                return;
            }

            // Backspace
            if (Keyboard.IsKeyDownOnce(Key.Back))
            {
                if (m_inputText.Length > 0)
                    m_inputText.Remove(m_inputText.Length - 1, 1);
                return;
            }

            // Character input — printable chars only, exclude tilde/backtick
            // Source: Engine.Input.Keyboard.LastChar
            char? lastChar = Keyboard.LastChar;
            if (lastChar.HasValue
                && !char.IsControl(lastChar.Value)
                && lastChar.Value != '`'
                && lastChar.Value != '~')
            {
                m_inputText.Append(lastChar.Value);
            }
        }

        private void UpdateDisplay()
        {
            if (m_contentLabel == null) return;

            var sb = new StringBuilder();
            // Show last N history lines that fit
            int startIdx = MathUtils.Max(0, m_history.Count - 15);
            for (int i = startIdx; i < m_history.Count; i++)
            {
                sb.AppendLine(m_history[i]);
            }
            // Current input line with cursor
            sb.Append("> " + m_inputText.ToString() + "_");
            m_contentLabel.Text = sb.ToString();
        }

        private void ExecuteCommand(string command)
        {
            command = command.Trim();
            if (string.IsNullOrEmpty(command)) return;

            m_history.Add("> " + command);

            var parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            string cmd = parts[0].ToLower();

            switch (cmd)
            {
                case "move":
                    HandleMoveCommand(parts);
                    break;
                case "help":
                    m_history.Add("Available commands:");
                    m_history.Add("  move <axis><distance> — e.g. move +x300, move -y10 +z50");
                    break;
                default:
                    m_history.Add("Unknown command: " + cmd + ". Type 'help' for commands.");
                    break;
            }

            // Trim history
            while (m_history.Count > MaxHistoryLines)
                m_history.RemoveAt(0);
        }

        private void HandleMoveCommand(string[] parts)
        {
            // Source: ComponentFrame.Position (public set)
            // Format: move [+-][xyz]<number> [...]
            // Example: move +x300 — move +300 in X
            //          move -y10 +z50 — move -10 Y, +50 Z
            var subsystemPlayers = Project.FindSubsystem<SubsystemPlayers>(throwOnError: false);
            if (subsystemPlayers == null || subsystemPlayers.ComponentPlayers.Count == 0)
            {
                m_history.Add("Error: No player found");
                return;
            }

            var player = subsystemPlayers.ComponentPlayers[0];
            var body = player.ComponentBody;
            Vector3 pos = body.Position;

            bool moved = false;
            for (int i = 1; i < parts.Length; i++)
            {
                string arg = parts[i];
                if (arg.Length < 2) continue;

                // Parse optional sign prefix: +x300 or -x300
                float sign = 1f;
                int idx = 0;
                if (arg[0] == '+')
                {
                    sign = 1f;
                    idx = 1;
                }
                else if (arg[0] == '-')
                {
                    sign = -1f;
                    idx = 1;
                }

                if (idx >= arg.Length)
                {
                    m_history.Add($"Missing axis in: {arg}");
                    continue;
                }

                // Parse axis after sign
                char axis = char.ToLower(arg[idx]);
                if (axis != 'x' && axis != 'y' && axis != 'z')
                {
                    m_history.Add($"Invalid axis '{axis}' in: {arg}");
                    continue;
                }
                idx++;

                if (idx >= arg.Length)
                {
                    m_history.Add($"Missing distance in: {arg}");
                    continue;
                }

                if (!float.TryParse(arg.Substring(idx), out float distance))
                {
                    m_history.Add($"Invalid number in: {arg}");
                    continue;
                }

                float delta = sign * distance;
                switch (axis)
                {
                    case 'x': pos.X += delta; break;
                    case 'y': pos.Y += delta; break;
                    case 'z': pos.Z += delta; break;
                }
                moved = true;
            }

            if (moved)
            {
                // Source: ComponentFrame.Position — public set, triggers PositionChanged event
                body.Position = pos;
                m_history.Add($"Moved to ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
            }
            else
            {
                m_history.Add("Usage: move [+-][xyz]<distance> [...]");
                m_history.Add("Example: move +x300  |  move -y10 +z50");
            }
        }
    }
}
