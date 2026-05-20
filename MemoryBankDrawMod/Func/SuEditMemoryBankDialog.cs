using Engine;
using Engine.Media;
using Game;
using System;
using System.Reflection;
using System.Xml.Linq;

namespace MemoryBankDrawMod
{
    // Source: EditMemoryBankDialog.cs — full replacement with 3-mode toggle (Linear/Grid/Draw)
    public class SuEditMemoryBankDialog : Dialog
    {
        private Action m_handler;
        private MemoryBankData m_memoryBankData;
        private MemoryBankData m_tmpMemoryBankData;

        // Original widgets (loaded from XML)
        private Widget m_linearPanel;
        private Widget m_gridPanel;
        private ButtonWidget m_okButton;
        private ButtonWidget m_cancelButton;
        private ButtonWidget m_switchViewButton;
        private Widget[] m_lineTextBoxes = new Widget[16];
        private Widget m_linearTextBox;

        // Widgets to toggle in Draw mode
        private Widget m_titleLabel;
        private Widget m_descLabelContainer;

        // Draw mode widgets
        private CanvasWidget m_drawPanel;
        private Widget m_drawRow; // Horizontal: [title | drawPanel]
        private ClickableWidget[] m_colorButtons;
        private LabelWidget[] m_colorLabels;
        private int m_selectedColor = -1;
        private ClickableWidget[,] m_gridCells;
        private LabelWidget[,] m_cellLabels = new LabelWidget[16, 16];
        private bool m_drawDirty = true;
        private Point2 m_lastPaintedCell = new Point2(-1, -1);

        // View mode: 0=Linear, 1=Grid, 2=Draw
        private int m_viewMode = 1;

        private const int CELL_SIZE = 15;
        private const int CELL_GAP = 1;
        private const int COLOR_BUTTON_SIZE = 22;

        // Source: TextBoxWidget is internal — use reflection for Text property
        private static readonly PropertyInfo s_textProp =
            typeof(Widget).Assembly.GetType("Game.TextBoxWidget")?.GetProperty("Text");



        // Source: player color palette — hex values 8-f mapped to colors
        private static readonly Color[] s_drawColors = new Color[16]
        {
            new Color(20, 20, 20), new Color(40, 40, 40),
            new Color(60, 60, 60), new Color(80, 80, 80),
            new Color(100, 100, 100), new Color(120, 120, 120),
            new Color(140, 140, 140), new Color(160, 160, 160),
            new Color(255, 255, 255), // 8 - white
            new Color(0, 255, 255),   // 9 - cyan
            new Color(255, 0, 0),     // A - red
            new Color(0, 0, 255),     // B - blue
            new Color(255, 255, 0),   // C - yellow
            new Color(0, 255, 0),     // D - green
            new Color(255, 165, 0),   // E - orange
            new Color(160, 32, 240),  // F - purple
        };

        private static readonly string[] s_hexLabels = new string[16]
        {
            "0","1","2","3","4","5","6","7","8","9","A","B","C","D","E","F"
        };

        private static string GetTextBoxText(Widget tb)
        {
            return (string)s_textProp.GetValue(tb);
        }

        private static void SetTextBoxText(Widget tb, string value)
        {
            s_textProp.SetValue(tb, value);
        }

        public SuEditMemoryBankDialog(MemoryBankData memoryBankData, Action handler)
        {
            // Source: EditMemoryBankDialog constructor — load layout from XML
            XElement node = ContentManager.Get<XElement>("Dialogs/EditMemoryBankDialog");
            LoadContents(this, node);

            m_linearPanel = Children.Find<Widget>("EditMemoryBankDialog.LinearPanel");
            m_gridPanel = Children.Find<Widget>("EditMemoryBankDialog.GridPanel");
            m_okButton = Children.Find<ButtonWidget>("EditMemoryBankDialog.OK");
            m_cancelButton = Children.Find<ButtonWidget>("EditMemoryBankDialog.Cancel");
            m_switchViewButton = Children.Find<ButtonWidget>("EditMemoryBankDialog.SwitchViewButton");
            m_linearTextBox = Children.Find<Widget>("EditMemoryBankDialog.LinearText");
            for (int i = 0; i < 16; i++)
            {
                m_lineTextBoxes[i] = Children.Find<Widget>("EditMemoryBankDialog.Line" + i);
            }

            m_handler = handler;
            m_memoryBankData = memoryBankData;
            m_tmpMemoryBankData = (MemoryBankData)memoryBankData.Copy();

            // Initialize TextBoxes with data
            // Initialize TextBoxes with data (no TextChanged event since we inherit Dialog, not EditMemoryBankDialog)
            string text = m_tmpMemoryBankData.SaveString(saveLastOutput: false);
            if (text.Length < 256)
            {
                text += new string('0', 256 - text.Length);
            }
            for (int i = 0; i < 16; i++)
            {
                SetTextBoxText(m_lineTextBoxes[i], text.Substring(i * 16, 16));
            }
            SetTextBoxText(m_linearTextBox, m_tmpMemoryBankData.SaveString(saveLastOutput: false));

            // Source: EditMemoryBankDialog constructor — default view is Grid
            m_linearPanel.IsVisible = false;
            m_gridPanel.IsVisible = true;

            // Cache widgets to toggle in Draw mode
            m_descLabelContainer = FindDescLabelContainer();
            m_titleLabel = FindTitleLabel();

            // Cache widgets to toggle in Draw mode
            m_descLabelContainer = FindDescLabelContainer();
            m_titleLabel = FindTitleLabel();

            // Build horizontal row: [3-line title | DrawPanel] (BuildDrawPanel called inside)
            BuildDrawRow();
            m_drawPanel.IsVisible = false;
            m_drawRow.IsVisible = false;

            // Insert DrawRow after LinearPanel in parent StackPanelWidget
            ContainerWidget parent = m_gridPanel.ParentWidget;
            if (parent != null)
            {
                int gridIndex = parent.Children.IndexOf(m_gridPanel);
                int insertAt = gridIndex + 2;
                if (insertAt > parent.Children.Count) insertAt = parent.Children.Count;
                parent.Children.Insert(insertAt, m_drawRow);
            }
        }

        private Widget FindDescLabelContainer()
        {
            var parentStack = m_gridPanel.ParentWidget as ContainerWidget;
            if (parentStack == null) return null;
            foreach (var child in parentStack.Children)
            {
                var canvas = child as CanvasWidget;
                if (canvas != null && canvas.Size.Y == 50f)
                {
                    var container = canvas as ContainerWidget;
                    if (container != null)
                    {
                        foreach (var inner in container.Children)
                        {
                            var lbl = inner as LabelWidget;
                            if (lbl != null && lbl.Text != null && lbl.Text.Contains("256"))
                            {
                                return canvas;
                            }
                        }
                    }
                }
            }
            return null;
        }

        private Widget FindTitleLabel()
        {
            // Find the "Edit Memory Bank" label in the parent stack
            var parentStack = m_gridPanel.ParentWidget as ContainerWidget;
            if (parentStack == null) return null;
            foreach (var child in parentStack.Children)
            {
                var lbl = child as LabelWidget;
                if (lbl != null && lbl.Text != null && lbl.Text.Contains("Edit Memory Bank"))
                {
                    return lbl;
                }
            }
            return null;
        }

        private void BuildDrawRow()
        {
            // Horizontal row: [3-line title | DrawPanel]
            var row = new StackPanelWidget();
            row.Direction = LayoutDirection.Horizontal;
            row.HorizontalAlignment = WidgetAlignment.Center;
            row.VerticalAlignment = WidgetAlignment.Center;

            // Left title: "Edit" / "Memory" / "Bank" — vertically centered
            var titleStack = new StackPanelWidget();
            titleStack.Direction = LayoutDirection.Vertical;
            titleStack.HorizontalAlignment = WidgetAlignment.Center;
            titleStack.VerticalAlignment = WidgetAlignment.Center;
            titleStack.IsHitTestVisible = false;
            string[] titleLines = { "Edit", "Memory", "Bank" };
            foreach (var line in titleLines)
            {
                var lbl = new LabelWidget();
                lbl.Text = line;
                lbl.Color = new Color(180, 180, 180);
                lbl.FontScale = 0.7f;
                lbl.HorizontalAlignment = WidgetAlignment.Center;
                lbl.IsHitTestVisible = false;
                titleStack.Children.Add(lbl);
            }
            row.Children.Add(titleStack);

            // Build DrawPanel
            BuildDrawPanel();
            row.Children.Add(m_drawPanel);

            m_drawRow = row;
        }

        private void BuildDrawPanel()
        {
            int gridPixelSize = 16 * (CELL_SIZE + CELL_GAP) - CELL_GAP; // 16*16-1=255

            // Layout: [color buttons | grid]
            int colorColWidth = 42;
            int gridAreaWidth = gridPixelSize + 4;
            int totalWidth = colorColWidth + 4 + gridAreaWidth + 4;
            int panelHeight = gridPixelSize + 8;

            m_drawPanel = new CanvasWidget();
            m_drawPanel.Name = "EditMemoryBankDialog.DrawPanel";
            m_drawPanel.HorizontalAlignment = WidgetAlignment.Center;
            m_drawPanel.Size = new Vector2(totalWidth, panelHeight);

            // Panel background
            var panelBg = new RectangleWidget();
            panelBg.FillColor = new Color(20, 20, 20, 200);
            panelBg.OutlineColor = new Color(128, 128, 128, 200);
            panelBg.OutlineThickness = 1f;
            panelBg.IsHitTestVisible = false;
            panelBg.Size = new Vector2(totalWidth, panelHeight);
            m_drawPanel.Children.Add(panelBg);

            // Color button column
            int colorBtnHeight = COLOR_BUTTON_SIZE + 2;
            // 9 buttons: 0(eraser) + 8-F(colors)
            int numButtons = 9;
            int colorColHeight = numButtons * (colorBtnHeight + 2);
            int colorColLeft = 4;
            int colorColTop = (panelHeight - colorColHeight) / 2;

            m_colorButtons = new ClickableWidget[numButtons];
            m_colorLabels = new LabelWidget[numButtons];
            var signFont = ContentManager.Get<BitmapFont>("Fonts/SignFont");

            for (int i = 0; i < numButtons; i++)
            {
                int hexValue = (i == 0) ? 0 : (8 + i - 1); // 0, 8,9,A,B,C,D,E,F
                int y = colorColTop + i * (colorBtnHeight + 2);

                var container = new CanvasWidget();
                container.Size = new Vector2(colorColWidth, colorBtnHeight);

                var swatch = new RectangleWidget();
                swatch.FillColor = s_drawColors[hexValue];
                swatch.OutlineColor = Color.Gray;
                swatch.OutlineThickness = 1f;
                swatch.IsHitTestVisible = false;
                swatch.Size = new Vector2(COLOR_BUTTON_SIZE, COLOR_BUTTON_SIZE);
                swatch.HorizontalAlignment = WidgetAlignment.Near;
                swatch.VerticalAlignment = WidgetAlignment.Center;

                var label = new LabelWidget();
                label.Text = s_hexLabels[hexValue];
                label.Color = Color.White;
                label.Font = signFont;
                label.FontScale = 1.5f;
                label.IsHitTestVisible = false;
                label.HorizontalAlignment = WidgetAlignment.Far;
                label.VerticalAlignment = WidgetAlignment.Center;

                var clickable = new ClickableWidget();
                clickable.Tag = hexValue;

                container.Children.Add(swatch);
                container.Children.Add(label);
                container.Children.Add(clickable);

                m_colorButtons[i] = clickable;
                m_colorLabels[i] = label;
                m_drawPanel.Children.Add(container);
                m_drawPanel.SetWidgetPosition(container, new Vector2(colorColLeft, y));
            }

            // Grid area — right side
            int gridLeft = colorColLeft + colorColWidth + 4;
            int gridTop = (panelHeight - gridPixelSize - 4) / 2;

            var gridContainer = new CanvasWidget();
            gridContainer.Size = new Vector2(gridAreaWidth, gridPixelSize + 4);
            gridContainer.ClampToBounds = true;

            var gridBg = new RectangleWidget();
            gridBg.FillColor = new Color(10, 10, 10, 200);
            gridBg.OutlineColor = new Color(80, 80, 80, 200);
            gridBg.OutlineThickness = 1f;
            gridBg.IsHitTestVisible = false;
            gridBg.Size = new Vector2(gridAreaWidth, gridPixelSize + 4);
            gridContainer.Children.Add(gridBg);

            m_gridCells = new ClickableWidget[16, 16];
            for (int row = 0; row < 16; row++)
            {
                for (int col = 0; col < 16; col++)
                {
                    var cellContainer = new CanvasWidget();
                    cellContainer.Size = new Vector2(CELL_SIZE, CELL_SIZE);

                    var rect = new RectangleWidget();
                    rect.FillColor = s_drawColors[0];
                    rect.OutlineColor = new Color(60, 60, 60);
                    rect.OutlineThickness = 0.5f;
                    rect.IsHitTestVisible = false;
                    rect.Size = new Vector2(CELL_SIZE, CELL_SIZE);

                    var cellLabel = new LabelWidget();
                    cellLabel.Text = "";
                    cellLabel.Font = signFont;
                    cellLabel.FontScale = 1.5f;
                    cellLabel.Color = Color.White;
                    cellLabel.HorizontalAlignment = WidgetAlignment.Center;
                    cellLabel.VerticalAlignment = WidgetAlignment.Center;
                    cellLabel.IsHitTestVisible = false;

                    var clickable = new ClickableWidget();
                    clickable.Tag = new Point2(row, col);

                    cellContainer.Children.Add(rect);
                    cellContainer.Children.Add(cellLabel);
                    cellContainer.Children.Add(clickable);

                    gridContainer.Children.Add(cellContainer);
                    gridContainer.SetWidgetPosition(cellContainer,
                        new Vector2(2 + col * (CELL_SIZE + CELL_GAP),
                                     2 + row * (CELL_SIZE + CELL_GAP)));

                    m_gridCells[row, col] = clickable;
                    m_cellLabels[row, col] = cellLabel;
                }
            }

            m_drawPanel.Children.Add(gridContainer);
            m_drawPanel.SetWidgetPosition(gridContainer, new Vector2(gridLeft, gridTop));
        }

        public override void Update()
        {
            // Sync data between TextBoxes and m_tmpMemoryBankData
            if (m_viewMode == 2)
            {
                // Draw mode: data flows FROM m_tmpMemoryBankData TO TextBoxes
                SyncDrawDataToTextBoxes();
            }
            else
            {
                // Linear/Grid mode: data flows FROM TextBoxes TO m_tmpMemoryBankData
                SyncTextBoxesToData();
            }

            // Three-way mode switching
            if (m_switchViewButton.IsClicked)
            {
                m_viewMode = (m_viewMode + 1) % 3;
                ApplyViewMode();
            }

            // Update switch button text — show NEXT mode name
            switch (m_viewMode)
            {
                case 0: m_switchViewButton.Text = "Grid"; break;
                case 1: m_switchViewButton.Text = "Draw"; break;
                case 2: m_switchViewButton.Text = "Linear"; break;
            }

            // Draw mode: sync grid cells and handle clicks
            if (m_viewMode == 2)
            {
                if (m_drawDirty)
                {
                    SyncAllGridCells();
                    m_drawDirty = false;
                }

                HandleColorButtonClicks();
                HandleGridCellClicks();
            }

            // OK/Cancel
            if (m_okButton.IsClicked)
            {
                m_memoryBankData.Data = m_tmpMemoryBankData.Data;
                Engine.Log.Information($"[MemoryBankDraw] OK: SaveString={m_memoryBankData.SaveString()}, Data.Count={m_memoryBankData.Data.Count}");
                Dismiss(result: true);
            }
            if (base.Input.Cancel || m_cancelButton.IsClicked)
            {
                Dismiss(result: false);
            }
        }

        private void ApplyViewMode()
        {
            m_linearPanel.IsVisible = (m_viewMode == 0);
            m_gridPanel.IsVisible = (m_viewMode == 1);
            m_drawPanel.IsVisible = (m_viewMode == 2);
            m_drawRow.IsVisible = (m_viewMode == 2);

            bool isDraw = (m_viewMode == 2);

            // Hide original title and description in Draw mode
            if (m_titleLabel != null)
            {
                m_titleLabel.IsVisible = !isDraw;
            }
            if (m_descLabelContainer != null)
            {
                m_descLabelContainer.IsVisible = !isDraw;
            }

            if (isDraw)
            {
                m_drawDirty = true;
            }
        }

        private void SyncTextBoxesToData()
        {
            string gridText = string.Empty;
            for (int i = 0; i < 16; i++)
            {
                gridText += GetTextBoxText(m_lineTextBoxes[i]);
            }
            var newData = new MemoryBankData();
            newData.LoadString(gridText);
            m_tmpMemoryBankData = newData;

            string normalized = m_tmpMemoryBankData.SaveString(saveLastOutput: false);
            if (normalized.Length < 256)
            {
                normalized += new string('0', 256 - normalized.Length);
            }
            for (int i = 0; i < 16; i++)
            {
                SetTextBoxText(m_lineTextBoxes[i], normalized.Substring(i * 16, 16));
            }
            SetTextBoxText(m_linearTextBox, m_tmpMemoryBankData.SaveString(saveLastOutput: false));
        }

        private void SyncDrawDataToTextBoxes()
        {
            string hex = m_tmpMemoryBankData.SaveString(saveLastOutput: false);
            if (hex.Length < 256)
            {
                hex += new string('0', 256 - hex.Length);
            }
            for (int i = 0; i < 16; i++)
            {
                SetTextBoxText(m_lineTextBoxes[i], hex.Substring(i * 16, 16));
            }
            SetTextBoxText(m_linearTextBox, hex);
        }

        private void SyncAllGridCells()
        {
            for (int row = 0; row < 16; row++)
            {
                for (int col = 0; col < 16; col++)
                {
                    UpdateCellColor(row, col);
                }
            }
        }

        private void UpdateCellColor(int row, int col)
        {
            int index = row * 16 + col;
            byte val = (index < m_tmpMemoryBankData.Data.Count)
                ? m_tmpMemoryBankData.Data.Array[index]
                : (byte)0;
            var cellWidget = m_gridCells[row, col];
            var container = cellWidget.ParentWidget as ContainerWidget;
            if (container != null && container.Children.Count >= 2)
            {
                var rect = container.Children[0] as RectangleWidget;
                if (rect != null)
                {
                    rect.FillColor = s_drawColors[MathUtils.Clamp(val, 0, 15)];
                }
                var lbl = container.Children[1] as LabelWidget;
                if (lbl != null)
                {
                    // Values 1-7: show hex digit as text (no color fill visible)
                    // Values 0, 8-F: color fill, no text
                    if (val >= 1 && val <= 7)
                    {
                        lbl.Text = s_hexLabels[val];
                        lbl.Color = Color.White;
                    }
                    else
                    {
                        lbl.Text = "";
                    }
                }
            }
        }

        private void SetCellValue(int row, int col, byte value)
        {
            int index = row * 16 + col;
            while (m_tmpMemoryBankData.Data.Count <= index)
            {
                m_tmpMemoryBankData.Data.Add(0);
            }
            m_tmpMemoryBankData.Data.Array[index] = value;
            UpdateCellColor(row, col);
        }

        private void HandleColorButtonClicks()
        {
            for (int i = 0; i < m_colorButtons.Length; i++)
            {
                var btn = m_colorButtons[i];
                int hexValue = (int)btn.Tag;

                if (btn.IsClicked)
                {
                    if (m_selectedColor == hexValue)
                    {
                        m_selectedColor = -1;
                    }
                    else
                    {
                        m_selectedColor = hexValue;
                    }
                    UpdateColorButtonVisuals();
                }
            }
        }

        private void UpdateColorButtonVisuals()
        {
            for (int i = 0; i < m_colorButtons.Length; i++)
            {
                var btn = m_colorButtons[i];
                int hexValue = (int)btn.Tag;
                var container = btn.ParentWidget as ContainerWidget;
                if (container != null && container.Children.Count > 0)
                {
                    var swatch = container.Children[0] as RectangleWidget;
                    if (swatch != null)
                    {
                        if (hexValue == m_selectedColor)
                        {
                            swatch.OutlineColor = Color.White;
                            swatch.OutlineThickness = 2f;
                        }
                        else
                        {
                            swatch.OutlineColor = Color.Gray;
                            swatch.OutlineThickness = 1f;
                        }
                    }
                }
            }
        }

        private void HandleGridCellClicks()
        {
            if (m_selectedColor < 0) return; // no color selected

            bool anyPressed = false;
            for (int row = 0; row < 16; row++)
            {
                for (int col = 0; col < 16; col++)
                {
                    if (m_gridCells[row, col].IsPressed)
                    {
                        anyPressed = true;
                        // Skip if already painted this cell during this drag
                        if (m_lastPaintedCell.X == row && m_lastPaintedCell.Y == col)
                            continue;
                        m_lastPaintedCell = new Point2(row, col);
                        SetCellValue(row, col, (byte)m_selectedColor);
                        return;
                    }
                }
            }
            // No cell pressed — mouse released, reset drag tracking
            if (!anyPressed)
            {
                m_lastPaintedCell = new Point2(-1, -1);
            }
        }

        private void Dismiss(bool result)
        {
            DialogsManager.HideDialog(this);
            if (m_handler != null && result)
            {
                m_handler();
            }
        }
    }
}
