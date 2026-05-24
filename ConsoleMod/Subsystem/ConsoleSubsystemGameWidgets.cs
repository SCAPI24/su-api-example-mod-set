using Engine;
using Engine.Input;
using Game;
using GameEntitySystem;
using System.Reflection;
using System;
using System.Collections.Generic;
using System.Text;
using TemplatesDatabase;

namespace ConsoleMod
{
    public class ConsoleSubsystemGameWidgets : SubsystemGameWidgets, IUpdateable
    {
        // Source: GameEntitySystem.UpdateOrder enum — runs BEFORE Input(-10) to intercept keyboard before ComponentInput
        public new UpdateOrder UpdateOrder => (UpdateOrder)(-100);

        // Source: Static flag for external code to check console state
        public static bool IsConsoleOpen { get; private set; }

        private bool m_consoleOpen = false;
        private bool m_justToggled = false;
        private bool m_justClosed = false; // Source: prevent key leaks on close frame
        private StringBuilder m_inputText = new StringBuilder();
        private int m_cursorPos = 0; // Source: cursor position within input text
        private List<string> m_history = new List<string>();
        private const int MaxHistoryLines = 200; // More lines for scrollback

#if ANDROID
        // Source: Android — keyboard shown state
        private bool m_androidKeyboardShown = false;
#endif

        // UI elements
        private CanvasWidget m_consolePanel;
        private CanvasWidget m_outputArea; // Nested canvas to clip output
        private RectangleWidget m_background;
        private LabelWidget m_outputLabel;
        private LabelWidget m_inputLabel;
        private RectangleWidget m_separator;
        private bool m_uiAttached = false;
        private const float ConsoleHeight = 280f;
        private const float InputAreaHeight = 28f;
        private const int MaxVisibleLines = 13;

        // Source: Scroll state for browsing history
        private int m_scrollOffset = 0; // 0 = auto (latest), >0 = scrolled up
        private bool m_autoScroll = true; // Reset on new output

        // Source: History navigation state
        private int m_historyIndex = -1;
        private string m_savedInput = "";
#if WINDOWS
        // Source: Modal dialog for mouse cursor unlock (analogous to iron sign)
        private Dialog m_modalDialog;
#endif

        public ConsoleSubsystemGameWidgets()
        {
#if ANDROID
            // Source: Android — subscribe to character input from virtual keyboard
            Keyboard.CharacterEntered += OnCharEntered;
#endif
        }

#if ANDROID
        private void OnCharEntered(char c)
        {
            // Source: Android — not used, input goes through ShowKeyboard dialog
        }
#endif

        public override void Update(float dt)
        {
            // Source: Engine.Input.Key.Tilde — grave accent / tilde key
            bool togglePressed = Keyboard.IsKeyDownOnce(Key.Tilde);

            if (togglePressed)
            {
                m_consoleOpen = !m_consoleOpen;
                m_justToggled = true;
                IsConsoleOpen = m_consoleOpen;
                if (m_consoleOpen)
                    AttachUI();
                else
                {
                    m_justClosed = true;
                    DetachUI();
                }
            }

            // Source: Capture console input BEFORE clearing keyboard state
            if (m_consoleOpen)
                CaptureInput();

            // Source: Block game input when console is open
            if (m_consoleOpen || m_justClosed)
            {
                m_justClosed = false;
                Keyboard.Clear();
                Mouse.Clear();
            }

            base.Update(dt);

            if (m_consoleOpen)
                UpdateDisplay();
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
            m_consolePanel.Size = new Vector2(-1f, ConsoleHeight);

            // Semi-transparent black background
            m_background = new RectangleWidget();
            m_background.FillColor = new Color(0, 0, 0, 180);
            m_background.OutlineColor = new Color(128, 128, 128, 200);
            m_background.OutlineThickness = 1f;
            m_background.HorizontalAlignment = WidgetAlignment.Stretch;
            m_background.VerticalAlignment = WidgetAlignment.Stretch;

            // Output area container — clips text so it won't overlap input
            m_outputArea = new CanvasWidget();
            m_outputArea.Size = new Vector2(-1f, ConsoleHeight - InputAreaHeight);
            m_outputArea.ClampToBounds = true;

            // Output label inside the output area
            m_outputLabel = new LabelWidget();
            m_outputLabel.Color = Color.White;
            m_outputLabel.FontScale = 0.6f;
            m_outputLabel.HorizontalAlignment = WidgetAlignment.Near;
            m_outputLabel.VerticalAlignment = WidgetAlignment.Far;
            m_outputArea.Children.Add(m_outputLabel);

            // Separator line between output and input
            m_separator = new RectangleWidget();
            m_separator.FillColor = new Color(128, 128, 128, 200);
            m_separator.OutlineColor = Color.Transparent;
            m_separator.Size = new Vector2(-1f, 1f);

            // Input line — fixed at bottom
            m_inputLabel = new LabelWidget();
            m_inputLabel.Color = new Color(0, 255, 0); // Green input text
            m_inputLabel.FontScale = 0.6f;
            m_inputLabel.HorizontalAlignment = WidgetAlignment.Near;
            m_inputLabel.VerticalAlignment = WidgetAlignment.Near;

            m_consolePanel.Children.Add(m_background);
            m_consolePanel.Children.Add(m_outputArea);
            m_consolePanel.Children.Add(m_separator);
            m_consolePanel.Children.Add(m_inputLabel);

            CanvasWidget.SetPosition(m_outputArea, new Vector2(0f, 0f));
            CanvasWidget.SetPosition(m_outputLabel, new Vector2(10f, 5f));
            CanvasWidget.SetPosition(m_separator, new Vector2(0f, ConsoleHeight - InputAreaHeight));
            CanvasWidget.SetPosition(m_inputLabel, new Vector2(10f, ConsoleHeight - InputAreaHeight + 4f));

            guiWidget.Children.Add(m_consolePanel);

            m_uiAttached = true;
            m_inputText.Clear();
            m_cursorPos = 0;

#if WINDOWS
            // Source: Show modal dialog to unlock mouse cursor (analogous to iron sign)
            m_modalDialog = new Dialog();
            m_modalDialog.Size = Vector2.Zero;
            DialogsManager.ShowDialog(guiWidget, m_modalDialog);
            MakeCoverTransparent();
#endif

#if ANDROID
            // Source: Android — show virtual keyboard for text input
            ShowAndroidKeyboard();
#endif
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
            m_outputArea = null;
            m_background = null;
            m_outputLabel = null;
            m_inputLabel = null;
            m_separator = null;
            m_uiAttached = false;

#if WINDOWS
            if (m_modalDialog != null)
            {
                DialogsManager.HideDialog(m_modalDialog);
                m_modalDialog = null;
            }
#endif

#if ANDROID
            HideAndroidKeyboard();
#endif
        }

#if ANDROID
        private void ShowAndroidKeyboard()
        {
            if (m_androidKeyboardShown) return;
            m_androidKeyboardShown = true;
            Keyboard.ShowKeyboard("Console", "Enter command", "", false,
                enter: (string text) =>
                {
                    m_androidKeyboardShown = false;
                    if (!string.IsNullOrEmpty(text))
                    {
                        ExecuteCommand(text);
                        m_inputText.Clear();
                        m_cursorPos = 0;
                    }
                },
                cancel: () => { m_androidKeyboardShown = false; });
        }

        private void HideAndroidKeyboard()
        {
            // Source: Android — keyboard dismisses via cancel callback or Back button
            // No explicit HideKeyboard API available
            m_androidKeyboardShown = false;
        }
#endif

        private void CaptureInput()
        {
#if WINDOWS
            if (m_justToggled)
            {
                m_justToggled = false;
                KeyboardInput.DeletePressed = false;
                KeyboardInput.GetInput(); // Drain stale chars accumulated before console opened
                return;
            }
#else
            if (m_justToggled)
            {
                m_justToggled = false;
                return;
            }
#endif

            // Escape closes console
            if (Keyboard.IsKeyDownOnce(Key.Escape))
            {
                m_consoleOpen = false;
                IsConsoleOpen = false;
                m_justClosed = true;
                DetachUI();
                return;
            }

#if WINDOWS
            // Scroll output area with mouse wheel or PageUp/PageDown
            int wheelDelta = Mouse.MouseWheelMovement;
            if (Keyboard.IsKeyDownOnce(Key.PageUp)) wheelDelta += 120;
            if (Keyboard.IsKeyDownOnce(Key.PageDown)) wheelDelta -= 120;
            if (wheelDelta != 0)
            {
                int scrollLines = wheelDelta / 120;
                int maxScroll = MathUtils.Max(0, m_history.Count - MaxVisibleLines);
                m_scrollOffset = MathUtils.Clamp(m_scrollOffset + scrollLines, 0, maxScroll);
                m_autoScroll = (m_scrollOffset == 0);
                return;
            }

            // Home/End — jump to top/bottom of history
            if (Keyboard.IsKeyDownOnce(Key.Home))
            {
                m_scrollOffset = MathUtils.Max(0, m_history.Count - MaxVisibleLines);
                m_autoScroll = false;
                return;
            }
            if (Keyboard.IsKeyDownOnce(Key.End))
            {
                m_scrollOffset = 0;
                m_autoScroll = true;
                return;
            }
#endif

            // Left arrow — move cursor left
            if (Keyboard.IsKeyDownOnce(Key.LeftArrow))
            {
                m_cursorPos = MathUtils.Max(0, m_cursorPos - 1);
                return;
            }

            // Right arrow — move cursor right
            if (Keyboard.IsKeyDownOnce(Key.RightArrow))
            {
                m_cursorPos = MathUtils.Min(m_inputText.Length, m_cursorPos + 1);
                return;
            }

            // Enter executes command
            if (Keyboard.IsKeyDownOnce(Key.Enter))
            {
                m_historyIndex = -1;
                string cmd = m_inputText.ToString();
                ExecuteCommand(cmd);
                m_inputText.Clear();
                m_cursorPos = 0;
#if ANDROID
                // Source: Android — re-show keyboard after command execution
                ShowAndroidKeyboard();
#endif
                return;
            }

#if WINDOWS
            // Delete key — delete char at cursor
            if (Keyboard.IsKeyDownOnce(Key.Delete))
            {
                if (m_cursorPos < m_inputText.Length)
                    m_inputText.Remove(m_cursorPos, 1);
                KeyboardInput.DeletePressed = false;
                return;
            }

            // Backspace — delete char before cursor
            if (Keyboard.IsKeyDownOnce(Key.BackSpace))
            {
                if (m_cursorPos > 0)
                {
                    m_inputText.Remove(m_cursorPos - 1, 1);
                    m_cursorPos--;
                }
                KeyboardInput.DeletePressed = false;
                return;
            }
#else
            // Backspace (Android) — no KeyboardInput.DeletePressed needed
            if (Keyboard.IsKeyDownOnce(Key.BackSpace))
            {
                if (m_cursorPos > 0)
                {
                    m_inputText.Remove(m_cursorPos - 1, 1);
                    m_cursorPos--;
                }
                return;
            }

            // Delete (Android)
            if (Keyboard.IsKeyDownOnce(Key.Delete))
            {
                if (m_cursorPos < m_inputText.Length)
                    m_inputText.Remove(m_cursorPos, 1);
                return;
            }
#endif

            // Up arrow — navigate to previous command in history
            if (Keyboard.IsKeyDownOnce(Key.UpArrow))
            {
                if (m_historyIndex == -1)
                {
                    m_savedInput = m_inputText.ToString();
                    m_historyIndex = m_history.Count;
                }
                int prev = m_historyIndex - 1;
                while (prev >= 0 && !m_history[prev].StartsWith("> "))
                    prev--;
                if (prev >= 0)
                {
                    m_historyIndex = prev;
                    m_inputText.Clear();
                    m_inputText.Append(m_history[prev].Substring(2));
                    m_cursorPos = m_inputText.Length;
                }
                return;
            }

            // Down arrow — navigate to next command in history
            if (Keyboard.IsKeyDownOnce(Key.DownArrow))
            {
                if (m_historyIndex < 0) return;
                int next = m_historyIndex + 1;
                while (next < m_history.Count && !m_history[next].StartsWith("> "))
                    next++;
                if (next < m_history.Count)
                {
                    m_historyIndex = next;
                    m_inputText.Clear();
                    m_inputText.Append(m_history[next].Substring(2));
                    m_cursorPos = m_inputText.Length;
                }
                else
                {
                    m_historyIndex = -1;
                    m_inputText.Clear();
                    if (!string.IsNullOrEmpty(m_savedInput))
                        m_inputText.Append(m_savedInput);
                    m_cursorPos = m_inputText.Length;
                }
                return;
            }

#if WINDOWS
            // Character input via KeyboardInput.GetInput — captures all chars per frame
            string inputChars = KeyboardInput.GetInput();
            if (!string.IsNullOrEmpty(inputChars))
            {
                m_historyIndex = -1;
                foreach (char c in inputChars)
                {
                    if (!char.IsControl(c) && c != '`' && c != '~')
                    {
                        m_inputText.Insert(m_cursorPos, c);
                        m_cursorPos++;
                    }
                }
            }
#else
            // Android: input handled by ShowKeyboard dialog, no inline char processing
#endif
        }

        private void UpdateDisplay()
        {
            if (m_outputLabel == null || m_inputLabel == null) return;

            // Auto-scroll: if new output was added and user hasn't manually scrolled
            if (m_autoScroll)
                m_scrollOffset = 0;

            // Output area: show visible window of history
            int totalLines = m_history.Count;
            int maxScroll = MathUtils.Max(0, totalLines - MaxVisibleLines);
            m_scrollOffset = MathUtils.Clamp(m_scrollOffset, 0, maxScroll);

            var sb = new StringBuilder();
            int startIdx = MathUtils.Max(0, totalLines - MaxVisibleLines - m_scrollOffset);
            int endIdx = MathUtils.Min(totalLines, startIdx + MaxVisibleLines);
            for (int i = startIdx; i < endIdx; i++)
            {
                sb.AppendLine(m_history[i]);
            }

            // Show scroll indicator if not at bottom
            if (m_scrollOffset > 0)
                sb.AppendLine($"[-- {m_scrollOffset} lines above --]");

            m_outputLabel.Text = sb.ToString();

            // Input area: prompt + text with cursor at m_cursorPos
            string text = m_inputText.ToString();
            m_inputLabel.Text = "> " + text.Substring(0, m_cursorPos) + "_" + text.Substring(m_cursorPos);
        }

        private void ExecuteCommand(string command)
        {
            command = command.Trim();
            if (string.IsNullOrEmpty(command)) return;

            m_historyIndex = -1;
            m_history.Add("> " + command);

            // New output -> auto-scroll to bottom
            m_autoScroll = true;
            m_scrollOffset = 0;

            var parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            string cmd = parts[0].ToLower();

            switch (cmd)
            {
                case "move":
                    HandleMoveCommand(parts);
                    break;
                case "tp":
                    HandleTpCommand(parts);
                    break;
                case "help":
                    m_history.Add("Available commands:");
                    m_history.Add("  move [+-][xyz]<distance> — relative move, e.g. move +x300");
                    m_history.Add("  tp [xyz]<value> — absolute teleport, e.g. tp x100 y50 z-30");
                    break;
                default:
                    m_history.Add("Unknown command: " + cmd + ". Type 'help' for commands.");
                    break;
            }

            // Trim history
            while (m_history.Count > MaxHistoryLines)
                m_history.RemoveAt(0);
        }

        private void HandleTpCommand(string[] parts)
        {
            // Format: tp [xyz]<value> [...] — set absolute position per axis
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
                char axis = char.ToLower(arg[0]);
                if (axis != 'x' && axis != 'y' && axis != 'z')
                {
                    m_history.Add($"Invalid axis '{axis}' in: {arg}");
                    continue;
                }
                if (!float.TryParse(arg.Substring(1), out float value))
                {
                    m_history.Add($"Invalid number in: {arg}");
                    continue;
                }
                switch (axis)
                {
                    case 'x': pos.X = value; break;
                    case 'y': pos.Y = value; break;
                    case 'z': pos.Z = value; break;
                }
                moved = true;
            }
            if (moved)
            {
                pos = FindSafePosition(pos);
                body.Position = pos;
                m_history.Add($"Teleported to ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
            }
            else
            {
                m_history.Add("Usage: tp [xyz]<value> [...]");
                m_history.Add("Example: tp x100  |  tp x100 y50 z-30");
            }
        }

        private void HandleMoveCommand(string[] parts)
        {
            // Source: ComponentFrame.Position (public set)
            // Format: move [+-][xyz]<number> [...]
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
                pos = FindSafePosition(pos);
                body.Position = pos;
                m_history.Add($"Moved to ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
            }
            else
            {
                m_history.Add("Usage: move [+-][xyz]<distance> [...]");
                m_history.Add("Example: move +x300  |  move -y10 +z50");
            }
        }

        // Source: Terrain.GetCellContentsFast / GetTopHeight — find safe Y to avoid crushing
        private Vector3 FindSafePosition(Vector3 pos)
        {
            var subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(throwOnError: false);
            if (subsystemTerrain == null) return pos;
            Terrain terrain = subsystemTerrain.Terrain;
            int cellX = (int)Math.Floor(pos.X);
            int cellZ = (int)Math.Floor(pos.Z);
            int cellY = Math.Max(0, (int)Math.Floor(pos.Y));

            // Check if feet + head are air (contents == 0)
            if (terrain.GetCellContentsFast(cellX, cellY, cellZ) == 0
                && terrain.GetCellContentsFast(cellX, cellY + 1, cellZ) == 0)
                return pos; // Already safe

            // Scan upward for 2 consecutive air blocks
            for (int y = cellY; y < 254; y++)
            {
                if (terrain.GetCellContentsFast(cellX, y, cellZ) == 0
                    && terrain.GetCellContentsFast(cellX, y + 1, cellZ) == 0)
                    return new Vector3(pos.X, y, pos.Z);
            }

            // Fallback: top height + 1
            int topHeight = terrain.GetTopHeight(cellX, cellZ);
            return new Vector3(pos.X, topHeight + 1f, pos.Z);
        }

#if WINDOWS
        // Source: DialogsManager.m_animationData — make the modal cover transparent
        // so the dialog doesn't dim the screen, only unlocks the mouse cursor
        private void MakeCoverTransparent()
        {
            try
            {
                var animDataField = typeof(DialogsManager).GetField("m_animationData",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (animDataField == null) return;
                var animData = animDataField.GetValue(null) as System.Collections.IDictionary;
                if (animData == null || !animData.Contains(m_modalDialog)) return;
                var data = animData[m_modalDialog];
                var coverField = data.GetType().GetField("CoverWidget",
                    BindingFlags.Public | BindingFlags.Instance);
                if (coverField == null) return;
                var cover = coverField.GetValue(data) as RectangleWidget;
                if (cover != null)
                {
                    cover.FillColor = Color.Transparent;
                    cover.OutlineColor = Color.Transparent;
                    cover.IsHitTestVisible = false;
                }
            }
            catch { }
        }
#endif
    }
}
