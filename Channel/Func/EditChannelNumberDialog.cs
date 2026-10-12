using System;
using System.Xml.Linq;
using Engine;
using Engine.Media;
using Game;

namespace Channel
{
    /// <summary>
    /// 通道号选择对话框：16×16 = 256 格单选，选中格绿色，标题下方显示 "#N"，
    /// 底部沿用 EditVoltageLevelDialog 的 OK / Cancel。
    /// 格子做法照 MemoryBankDrawMod 的画图面板（CanvasWidget + RectangleWidget + ClickableWidget）。
    /// Source: Mod/MemoryBankDrawMod/Func/SuEditMemoryBankDialog.cs:BuildDrawPanel
    /// </summary>
    public sealed class EditChannelNumberDialog : Dialog
    {
        private const int GridSize = 16;
        private const int CellSize = 16;
        private const int CellGap = 2;
        private const int CellCount = GridSize * GridSize;

        // 原版 EditVoltageLevelDialog 的框架只有 500×300，塞不下 16×16 网格 + 标题 + 按钮
        // （会把底部 OK/Cancel 顶出 ClampToBounds 之外）→ 放大框架。
        private static readonly Vector2 FrameSize = new Vector2(520f, 500f);
        private const float DefaultFrameWidth = 500f;
        private const float DefaultFrameHeight = 300f;

        private static readonly Color s_selectedColor = new Color(0, 255, 0);
        private static readonly Color s_emptyColor = new Color(28, 28, 28);
        private static readonly Color s_cellOutlineColor = new Color(70, 70, 70);

        private readonly Action<int> m_handler;
        private readonly RectangleWidget[] m_cellRects = new RectangleWidget[CellCount];
        private readonly ClickableWidget[] m_cells = new ClickableWidget[CellCount];
        private readonly LabelWidget m_numberLabel;
        private readonly ButtonWidget m_okButton;
        private readonly ButtonWidget m_cancelButton;
        private int m_selected;

        public EditChannelNumberDialog(int channel, Action<int> handler)
        {
            // Source: Survivalcraft/Game/EditVoltageLevelDialog.cs —— 复用同一个对话框框架
            XElement node = ContentManager.Get<XElement>("Dialogs/EditVoltageLevelDialog");
            LoadContents(this, node);

            m_handler = handler;
            m_selected = MathUtils.Clamp(channel, 0, CellCount - 1);

            m_okButton = Children.Find<ButtonWidget>("EditVoltageLevelDialog.OK");
            m_cancelButton = Children.Find<ButtonWidget>("EditVoltageLevelDialog.Cancel");
            // 不抛异常：万一某个 pak 改过这个对话框布局，退化为把网格挂到对话框根上
            SliderWidget slider = Children.Find<SliderWidget>("EditVoltageLevelDialog.VoltageSlider", throwIfNotFound: false);

            EnlargeFrame();

            LabelWidget titleLabel = FindLabel(this, "Adjust Voltage");
            if (titleLabel != null)
            {
                titleLabel.Text = ChannelText.Channel;
                // 中文环境必须换成本仓库的中文字体（SuAPICore 的嵌入图集），否则显示缺字方块
                // Source: EntitySystem/SuAPICore/SuAPIFonts.cs:SuAPIFonts.GetMatchingProfile
                BitmapFont chineseFont = ChannelText.GetChineseFont(titleLabel.Font);
                if (chineseFont != null)
                    titleLabel.Font = chineseFont;
            }

            m_numberLabel = new LabelWidget
            {
                Text = "#" + m_selected,
                Color = Color.White,
                FontScale = 1f,
                HorizontalAlignment = WidgetAlignment.Center,
                IsHitTestVisible = false
            };
            // 中文图集本身覆盖 ASCII（Pericles 原生字形），所以 "#N" 用同一字体也安全
            if (titleLabel != null && titleLabel.Font != null)
                m_numberLabel.Font = titleLabel.Font;

            CanvasWidget gridPanel = BuildGridPanel();

            StackPanelWidget content = new StackPanelWidget();
            content.Direction = LayoutDirection.Vertical;
            content.HorizontalAlignment = WidgetAlignment.Center;
            content.VerticalAlignment = WidgetAlignment.Center;
            content.Children.Add(m_numberLabel);
            content.Children.Add(gridPanel);

            if (slider != null)
                ReplaceWidget(slider, content);
            else
                Children.Add(content);

            // 注册给翻译 Mod（词条由本 Mod 自带）
            ChannelText.RegisterWithTranslationMod(this);
        }

        public override void Update()
        {
            if (m_okButton != null && m_okButton.IsClicked)
                Dismiss(m_selected);

            if (base.Input.Cancel || (m_cancelButton != null && m_cancelButton.IsClicked))
                Dismiss(null);

            for (int i = 0; i < m_cells.Length; i++)
            {
                if (m_cells[i] == null || !m_cells[i].IsClicked || m_selected == i)
                    continue;
                m_selected = i;
                m_numberLabel.Text = "#" + m_selected;
                RefreshCells();
            }
        }

        private void Dismiss(int? result)
        {
            DialogsManager.HideDialog(this);
            if (m_handler != null && result.HasValue)
                m_handler(result.Value);
        }

        private CanvasWidget BuildGridPanel()
        {
            int gridPixelSize = GridSize * (CellSize + CellGap) - CellGap;
            int panelSize = gridPixelSize + 4;

            CanvasWidget panel = new CanvasWidget();
            panel.Size = new Vector2(panelSize, panelSize);
            panel.ClampToBounds = true;
            panel.HorizontalAlignment = WidgetAlignment.Center;

            RectangleWidget background = new RectangleWidget
            {
                FillColor = new Color(10, 10, 10, 220),
                OutlineColor = new Color(80, 80, 80, 220),
                OutlineThickness = 1f,
                IsHitTestVisible = false,
                Size = new Vector2(panelSize, panelSize)
            };
            panel.Children.Add(background);

            for (int row = 0; row < GridSize; row++)
            {
                for (int col = 0; col < GridSize; col++)
                {
                    int index = row * GridSize + col;

                    CanvasWidget cell = new CanvasWidget();
                    cell.Size = new Vector2(CellSize, CellSize);

                    RectangleWidget rect = new RectangleWidget
                    {
                        FillColor = s_emptyColor,
                        OutlineColor = s_cellOutlineColor,
                        OutlineThickness = 1f,
                        IsHitTestVisible = false,
                        Size = new Vector2(CellSize, CellSize)
                    };

                    ClickableWidget clickable = new ClickableWidget();

                    cell.Children.Add(rect);
                    cell.Children.Add(clickable);
                    panel.Children.Add(cell);
                    panel.SetWidgetPosition(cell,
                        new Vector2(2 + col * (CellSize + CellGap), 2 + row * (CellSize + CellGap)));

                    m_cellRects[index] = rect;
                    m_cells[index] = clickable;
                }
            }

            RefreshCells();
            return panel;
        }

        private void RefreshCells()
        {
            for (int i = 0; i < m_cellRects.Length; i++)
            {
                if (m_cellRects[i] == null)
                    continue;
                m_cellRects[i].FillColor = (i == m_selected) ? s_selectedColor : s_emptyColor;
            }
        }

        /// <summary>把原版 500×300 的对话框框架放大到 FrameSize，并同步背景底框尺寸。</summary>
        private void EnlargeFrame()
        {
            CanvasWidget frame = FindCanvas(this, DefaultFrameWidth, DefaultFrameHeight);
            if (frame == null)
            {
                // 布局被改过（pak 换过这个对话框）时兜底：网格仍可用，只是可能被裁
                Log.Warning("[Channel] EditVoltageLevelDialog frame not found; channel dialog layout may overflow");
                return;
            }
            frame.Size = FrameSize;
            foreach (Widget child in frame.Children)
            {
                if (child is BevelledRectangleWidget background)
                    background.Size = FrameSize;
            }
        }

        private static CanvasWidget FindCanvas(Widget root, float width, float height)
        {
            if (!(root is ContainerWidget container))
                return null;
            foreach (Widget child in container.Children)
            {
                if (child is CanvasWidget canvas
                    && Math.Abs(canvas.Size.X - width) < 1f
                    && Math.Abs(canvas.Size.Y - height) < 1f)
                {
                    return canvas;
                }
                CanvasWidget found = FindCanvas(child, width, height);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static void ReplaceWidget(Widget oldWidget, Widget replacement)
        {
            if (oldWidget == null || replacement == null)
                return;
            ContainerWidget parent = oldWidget.ParentWidget as ContainerWidget;
            if (parent == null)
                return;
            int index = parent.Children.IndexOf(oldWidget);
            if (index < 0)
                return;
            parent.Children.Remove(oldWidget);
            parent.Children.Insert(index, replacement);
        }

        private static LabelWidget FindLabel(Widget root, string text)
        {
            if (!(root is ContainerWidget container))
                return null;
            foreach (Widget child in container.Children)
            {
                if (child is LabelWidget label && label.Text == text)
                    return label;
                LabelWidget found = FindLabel(child, text);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
