using Engine;
using Engine.Content;
using Engine.Graphics;
using Engine.Input;
using Game;
using GameEntitySystem;
using System.Reflection;
using System;
using System.Collections.Generic;
using System.Text;
using SuAPI;

namespace ConsoleMod
{
    // Source: SubsystemGameWidgets.cs:SubsystemGameWidgets.Update — one real Views host, separate early input.
    public sealed class ConsoleSubsystemGameWidgets : UpdateExtension<SubsystemGameWidgets>, IDisposable
    {
        // Source: Runtime platform detection — replaces #if WINDOWS/#if ANDROID for single-TFM compatibility
        private static readonly bool IsWindows = System.OperatingSystem.IsWindows();
        private static readonly bool IsAndroid = System.OperatingSystem.IsAndroid();

        private ReadOnlyList<GameWidget> GameWidgets => Host.GameWidgets;
        private Project Project => Host.Project;
        private bool m_initialized;
        private bool m_disposed;

        public ConsoleSubsystemGameWidgets(SubsystemGameWidgets host) : base(host) { }

        // Source: Static flag for external code to check console state
        private static readonly HashSet<ConsoleSubsystemGameWidgets> s_openControllers = new HashSet<ConsoleSubsystemGameWidgets>();
        public static bool IsConsoleOpen => s_openControllers.Count != 0;
        private int m_keyboardSession;

        private bool m_consoleOpen = false;
        private bool m_justToggled = false;
        private bool m_justClosed = false; // Source: prevent key leaks on close frame
        private StringBuilder m_inputText = new StringBuilder();
        private int m_cursorPos = 0; // Source: cursor position within input text
        private List<string> m_history = new List<string>();
        private const int MaxHistoryLines = 200; // More lines for scrollback

        // Source: Android — keyboard shown state
        private bool m_androidKeyboardShown = false;

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

        // Source: Modal dialog for mouse cursor unlock (analogous to iron sign) — Windows only
        private Dialog m_modalDialog;

        // Source: Console toggle button — visible on both Windows and Android
        // Source: SurvivalcraftMiniMap.SuComponentMap.Load — same pattern: MoreContents StackPanel + BitmapButtonWidget
        private BitmapButtonWidget m_consoleButton;
        private Texture2D m_buttonNormalTex;
        private Texture2D m_buttonPressedTex;
        private bool m_buttonAttached = false;

        // Source: ConsoleInputSubsystem.cs:ConsoleEarlyUpdate.Update — initialize after Project loading, before Input(-10).
        private void Initialize()
        {
            if (m_initialized) return;
            if (m_disposed) throw new ObjectDisposedException(nameof(ConsoleSubsystemGameWidgets));

            // Source: Normal button texture from scmod Content/ (ContentCache key: Mod/SuConsoleButton)
            m_buttonNormalTex = ContentCache.Get<Texture2D>("Mod/SuConsoleButton");
            // Source: Pressed button texture embedded in DLL
            m_buttonPressedTex = LoadEmbeddedTexture("ConsoleMod.Content.SuConsoleButton_Pressed.png");

            m_initialized = true;
            // Source: SubsystemGameWidgets.cs:Load — players may still be added on a later simulation step.
            AttachConsoleButton();
        }

        // Source: SurvivalcraftMiniMap.SuComponentMap.Load — same pattern: find MoreContents StackPanel, add BitmapButtonWidget
        private void AttachConsoleButton()
        {
            if (m_buttonAttached) return;

            if (GameWidgets.Count == 0) return;

            GameWidget gameWidget = GameWidgets[0];

            // Source: GameWidget.xml — MoreContents is the horizontal StackPanel behind MoreButton
            StackPanelWidget moreContents = gameWidget.Children.Find<StackPanelWidget>("MoreContents", true);

            m_consoleButton = new BitmapButtonWidget
            {
                Name = "ConsoleMod.Toggle",
                Text = "",
                Size = new Vector2(68f, 64f),
                Margin = new Vector2(4f, 0f),
                NormalSubtexture = new Subtexture(m_buttonNormalTex, Vector2.Zero, Vector2.One),
                ClickedSubtexture = new Subtexture(m_buttonPressedTex, Vector2.Zero, Vector2.One)
            };

            moreContents.Children.Add(m_consoleButton);

            m_buttonAttached = true;
        }

        private static Texture2D LoadEmbeddedTexture(string resourceName)
        {
            var asm = Assembly.GetExecutingAssembly();
            var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) throw new InvalidOperationException("[ConsoleMod] Missing embedded texture: " + resourceName);
            try { return Texture2D.Load(stream); }
            finally { stream.Dispose(); }
        }

        // Source: ConsoleSubsystemGameWidgets.cs:original Update — keyboard interception remains at -100.
        internal void UpdateInput(float dt)
        {
            if (m_disposed) return;
            Initialize();
            // Source: Deferred button attach — Load() may run before GameWidgets are populated
            if (!m_buttonAttached) AttachConsoleButton();

            // Source: Console button click triggers toggle on both platforms
            // Source: Keyboard.Tilde — grave accent / tilde key (Windows shortcut)
            bool togglePressed = Keyboard.IsKeyDownOnce(Key.Tilde) || (m_consoleButton != null && m_consoleButton.IsClicked);

            if (togglePressed)
            {
                m_consoleOpen = !m_consoleOpen;
                m_justToggled = true;
                // Source: ConsoleInputSubsystem: each real Views host owns its own controller.
                if (m_consoleOpen) s_openControllers.Add(this);
                else s_openControllers.Remove(this);
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

        }

        // Source: SubsystemGameWidgets.cs:Update — the shared runner invokes the original host once at Views(200).
        public override void AfterUpdate(float dt, InvocationResult result)
        {
            if (!m_disposed && m_consoleOpen) UpdateDisplay();
        }

        public override void Detached() => Dispose();

        // Source: WatchMod/Subsystem/SuWatchComponent.cs:DetachUI — release only this controller's widgets.
        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            m_consoleOpen = false;
            s_openControllers.Remove(this);
            var errors = new List<Exception>();
            try { DetachUI(); }
            catch (Exception ex) { errors.Add(ex); }
            if (m_consoleButton != null)
            {
                try
                {
                    m_consoleButton.ParentWidget?.Children.Remove(m_consoleButton);
                    m_consoleButton.Dispose();
                }
                catch (Exception ex) { errors.Add(ex); }
                m_consoleButton = null;
            }
            m_buttonAttached = false;
            try { m_buttonPressedTex?.Dispose(); }
            catch (Exception ex) { errors.Add(ex); }
            m_buttonPressedTex = null;
            // ContentCache owns the normal texture; this extension must not dispose the shared asset.
            m_buttonNormalTex = null;
            if (errors.Count != 0) throw new AggregateException("[ConsoleMod] Controller cleanup failed.", errors);
        }

        private void AttachUI()
        {
            if (m_uiAttached) return;
            if (GameWidgets.Count == 0) return;

            // Source: GameWidget.GuiWidget — HUD container
            ContainerWidget guiWidget = GameWidgets[0].GuiWidget;

            // Console panel — bottom of screen
            m_consolePanel = new CanvasWidget();
            m_consolePanel.Name = "ConsoleMod.Panel";
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

            if (IsWindows)
            {
                // Source: Show modal dialog to unlock mouse cursor (analogous to iron sign)
                m_modalDialog = new Dialog();
                m_modalDialog.Size = Vector2.Zero;
                DialogsManager.ShowDialog(guiWidget, m_modalDialog);
                MakeCoverTransparent();
            }

            if (IsAndroid)
            {
                // Source: Android — show virtual keyboard for text input
                ShowAndroidKeyboard();
            }
        }

        private void DetachUI()
        {
            CanvasWidget panel = m_consolePanel;
            Dialog modalDialog = m_modalDialog;
            m_consolePanel = null;
            m_modalDialog = null;
            m_outputArea = null;
            m_background = null;
            m_outputLabel = null;
            m_inputLabel = null;
            m_separator = null;
            m_uiAttached = false;
            var errors = new List<Exception>();
            if (panel != null)
            {
                try
                {
                    panel.ParentWidget?.Children.Remove(panel);
                    panel.Dispose();
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (IsWindows && modalDialog != null)
            {
                // Source: DialogsManager.Update: the hide animation still owns this dialog until detached.
                try { DialogsManager.HideDialog(modalDialog); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (IsAndroid)
            {
                HideAndroidKeyboard();
            }
            if (errors.Count != 0) throw new AggregateException("[ConsoleMod] Console UI cleanup failed.", errors);
        }

        private void ShowAndroidKeyboard()
        {
            if (!IsAndroid) return;
            if (m_androidKeyboardShown) return;
            m_androidKeyboardShown = true;
            int session = ++m_keyboardSession;
            Keyboard.ShowKeyboard("Console", "Enter command", "", false,
                enter: (string text) =>
                {
                    // Source: Keyboard.ShowKeyboard: a delayed callback must belong to this opening.
                    if (session != m_keyboardSession) return;
                    m_androidKeyboardShown = false;
                    // Source: GameManager.cs:DisposeProject — discard keyboard completions from a closed console or previous world.
                    if (m_disposed || !m_consoleOpen || !ReferenceEquals(GameManager.Project, Project)) return;
                    if (!string.IsNullOrEmpty(text))
                    {
                        ExecuteCommand(text);
                        m_inputText.Clear();
                        m_cursorPos = 0;
                    }
                },
                cancel: () => { if (session == m_keyboardSession) m_androidKeyboardShown = false; });
        }

        private void HideAndroidKeyboard()
        {
            // Source: Android — keyboard dismisses via cancel callback or Back button
            // No explicit HideKeyboard API available; invalidate callbacks from the closed session.
            m_keyboardSession++;
            m_androidKeyboardShown = false;
        }

        private void CaptureInput()
        {
            if (m_justToggled)
            {
                m_justToggled = false;
                if (IsWindows)
                {
                    Keyboard.AcquireEnteredText(); // Drain stale chars accumulated before console opened
                }
                return;
            }

            // Escape closes console
            if (Keyboard.IsKeyDownOnce(Key.Escape))
            {
                m_consoleOpen = false;
                s_openControllers.Remove(this);
                m_justClosed = true;
                DetachUI();
                return;
            }

            // Scroll output area with mouse wheel or PageUp/PageDown (Windows only)
            if (IsWindows)
            {
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
            }

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
                if (IsAndroid)
                {
                    // Source: Android — re-show keyboard after command execution
                    ShowAndroidKeyboard();
                }
                return;
            }

            // Delete key — delete char at cursor
            if (Keyboard.IsKeyDownOnce(Key.Delete))
            {
                if (m_cursorPos < m_inputText.Length)
                    m_inputText.Remove(m_cursorPos, 1);
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
                return;
            }

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

            // Character input (Windows: Engine ACQUIRE queue; Android: via ShowKeyboard dialog)
            if (IsWindows)
            {
                string inputChars = Keyboard.AcquireEnteredText();
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
            }
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

        // Source: DialogsManager.m_animationData — make the modal cover transparent
        // so the dialog doesn't dim the screen, only unlocks the mouse cursor (Windows only)
        private void MakeCoverTransparent()
        {
            if (!IsWindows) return;
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
    }
}
