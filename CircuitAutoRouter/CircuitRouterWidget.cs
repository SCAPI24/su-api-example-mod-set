using Engine;
using Engine.Graphics;
using Game;

namespace CircuitAutoRouter
{
    public class CircuitRouterWidget : CanvasWidget
    {
        private SubsystemCircuitRouter m_router;
        private ComponentPlayer m_player;
        private BevelledButtonWidget m_setArea1Btn;
        private BevelledButtonWidget m_setArea2Btn;
        private BevelledButtonWidget m_connectBtn;
        private BevelledButtonWidget m_setNumberBtn;
        private LabelWidget m_statusLabel;
        private LabelWidget m_selectedLabel;
        private LabelWidget m_area1Label;
        private LabelWidget m_area2Label;
        private BevelledButtonWidget m_closeBtn;

        // Number controls
        private StackPanelWidget m_numberPanel;
        private LabelWidget m_numberLabel;
        private BevelledButtonWidget m_numberMinusBtn;
        private BevelledButtonWidget m_numberPlusBtn;
        private BevelledButtonWidget m_numberConfirmBtn;

        // Clear selection button
        private BevelledButtonWidget m_clearSelBtn;

        // Face cycle buttons
        private BevelledButtonWidget m_area1FaceBtn;
        private BevelledButtonWidget m_area2FaceBtn;

        // Chain-link button
        private BevelledButtonWidget m_chainLinkBtn;

        public CircuitRouterWidget(SubsystemCircuitRouter router, ComponentPlayer player)
        {
            m_router = router;
            m_player = player;

            Size = new Vector2(700f, 460f);
            HorizontalAlignment = WidgetAlignment.Center;
            VerticalAlignment = WidgetAlignment.Center;
            Margin = new Vector2(0f, 30f);

            RectangleWidget bg = new RectangleWidget
            {
                Size = new Vector2(700f, 460f),
                FillColor = new Color(0, 0, 0, 180),
                OutlineColor = Color.White,
                OutlineThickness = 1f
            };
            Children.Add(bg);

            StackPanelWidget mainPanel = new StackPanelWidget
            {
                Direction = LayoutDirection.Vertical,
                Margin = new Vector2(15f, 10f),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Center
            };

            // Title
            mainPanel.Children.Add(new LabelWidget
            {
                Text = "Circuit Auto Router",
                Color = Color.White,
                HorizontalAlignment = WidgetAlignment.Center,
                Margin = new Vector2(0f, 5f)
            });

            // Status label
            m_statusLabel = new LabelWidget
            {
                Text = "Use Rod to select blocks",
                Color = Color.Yellow,
                HorizontalAlignment = WidgetAlignment.Center,
                Margin = new Vector2(0f, 2f)
            };
            mainPanel.Children.Add(m_statusLabel);

            // Info row
            StackPanelWidget infoRow = new StackPanelWidget
            {
                Direction = LayoutDirection.Horizontal,
                HorizontalAlignment = WidgetAlignment.Center,
                Margin = new Vector2(0f, 2f)
            };
            m_selectedLabel = new LabelWidget { Text = "Sel: 0", Color = Color.Green, Margin = new Vector2(8f, 0f) };
            m_area1Label = new LabelWidget { Text = "Area1: --", Color = new Color(255, 255, 0), Margin = new Vector2(8f, 0f) };
            m_area2Label = new LabelWidget { Text = "Area2: --", Color = new Color(255, 60, 60), Margin = new Vector2(8f, 0f) };
            infoRow.Children.Add(m_selectedLabel);
            infoRow.Children.Add(m_area1Label);
            infoRow.Children.Add(m_area2Label);
            mainPanel.Children.Add(infoRow);

            // Row 1: Set Area 1 | Set Area 2
            StackPanelWidget btnRow1 = new StackPanelWidget { Direction = LayoutDirection.Horizontal, HorizontalAlignment = WidgetAlignment.Center, Margin = new Vector2(0f, 5f) };
            m_setArea1Btn = CreateHalfButton("Set Area 1");
            m_setArea2Btn = CreateHalfButton("Set Area 2");
            btnRow1.Children.Add(m_setArea1Btn);
            btnRow1.Children.Add(m_setArea2Btn);
            mainPanel.Children.Add(btnRow1);

            // Row 2: A1 Face | A2 Face
            StackPanelWidget btnRow2 = new StackPanelWidget { Direction = LayoutDirection.Horizontal, HorizontalAlignment = WidgetAlignment.Center, Margin = new Vector2(0f, 3f) };
            m_area1FaceBtn = CreateHalfButton("A1 Face: --");
            m_area2FaceBtn = CreateHalfButton("A2 Face: --");
            btnRow2.Children.Add(m_area1FaceBtn);
            btnRow2.Children.Add(m_area2FaceBtn);
            mainPanel.Children.Add(btnRow2);

            // Row 3: Connect | Chain Link
            StackPanelWidget btnRow3 = new StackPanelWidget { Direction = LayoutDirection.Horizontal, HorizontalAlignment = WidgetAlignment.Center, Margin = new Vector2(0f, 3f) };
            m_connectBtn = CreateHalfButton("Connect");
            m_chainLinkBtn = CreateHalfButton("Chain Link");
            btnRow3.Children.Add(m_connectBtn);
            btnRow3.Children.Add(m_chainLinkBtn);
            mainPanel.Children.Add(btnRow3);

            // Row 4: Set Number | Clear Selection
            StackPanelWidget btnRow4 = new StackPanelWidget { Direction = LayoutDirection.Horizontal, HorizontalAlignment = WidgetAlignment.Center, Margin = new Vector2(0f, 3f) };
            m_setNumberBtn = CreateHalfButton("Set Number");
            m_clearSelBtn = CreateHalfButton("Clear Selection");
            btnRow4.Children.Add(m_setNumberBtn);
            btnRow4.Children.Add(m_clearSelBtn);
            mainPanel.Children.Add(btnRow4);

            // Row 5: Close
            StackPanelWidget btnRow5 = new StackPanelWidget { Direction = LayoutDirection.Horizontal, HorizontalAlignment = WidgetAlignment.Center, Margin = new Vector2(0f, 3f) };
            m_closeBtn = CreateHalfButton("Close");
            btnRow5.Children.Add(m_closeBtn);
            mainPanel.Children.Add(btnRow5);

            // Number sub-panel (hidden)
            m_numberPanel = new StackPanelWidget { Direction = LayoutDirection.Horizontal, HorizontalAlignment = WidgetAlignment.Center, Margin = new Vector2(0f, 3f), IsVisible = false };
            m_numberMinusBtn = CreateSmallButton("-");
            m_numberLabel = new LabelWidget { Text = m_router.CircuitNumber.ToString(), Color = Color.White, Margin = new Vector2(8f, 0f), HorizontalAlignment = WidgetAlignment.Center };
            m_numberPlusBtn = CreateSmallButton("+");
            m_numberConfirmBtn = CreateSmallButton("OK");
            m_numberPanel.Children.Add(m_numberMinusBtn);
            m_numberPanel.Children.Add(m_numberLabel);
            m_numberPanel.Children.Add(m_numberPlusBtn);
            m_numberPanel.Children.Add(m_numberConfirmBtn);
            mainPanel.Children.Add(m_numberPanel);

            Children.Add(mainPanel);

            // Restore button highlight from current mode
            m_setNumberBtn.ColorTransform = m_router.IsSetNumberMode ? new Color(255, 255, 0) : Color.White;
        }

        public override void Update()
        {
            base.Update();

            if (m_clearSelBtn.IsClicked)
            {
                m_router.ClearSelection();
                m_statusLabel.Text = "Cleared";
            }
            if (m_setArea1Btn.IsClicked)
            {
                m_router.SetMode(RouterMode.SetArea1);
                m_statusLabel.Text = m_router.Area1Count > 0 ? "Area 1 OK" : "Select first!";
            }
            if (m_setArea2Btn.IsClicked)
            {
                m_router.SetMode(RouterMode.SetArea2);
                m_statusLabel.Text = m_router.Area2Count > 0 ? "Area 2 OK" : "Select first!";
            }
            if (m_area1FaceBtn.IsClicked)
            {
                m_router.CycleArea1Face();
                m_statusLabel.Text = $"A1 Face: {m_router.Area1FaceName}";
            }
            if (m_area2FaceBtn.IsClicked)
            {
                m_router.CycleArea2Face();
                m_statusLabel.Text = $"A2 Face: {m_router.Area2FaceName}";
            }
            if (m_connectBtn.IsClicked)
            {
                m_router.Connect();
            }
            if (m_chainLinkBtn.IsClicked)
            {
                if (m_router.IsChainLinkMode)
                {
                    m_router.ExitChainLink();
                }
                else
                {
                    m_router.StartChainLink();
                }
            }
            if (m_setNumberBtn.IsClicked)
            {
                m_router.ToggleSetNumber();
            }
            if (m_numberMinusBtn.IsClicked)
            {
                int num = m_router.CircuitNumber;
                if (num > 1) m_router.SetCircuitNumber(num - 1);
                m_numberLabel.Text = m_router.CircuitNumber.ToString();
            }
            if (m_numberPlusBtn.IsClicked)
            {
                m_router.SetCircuitNumber(m_router.CircuitNumber + 1);
                m_numberLabel.Text = m_router.CircuitNumber.ToString();
            }
            if (m_numberConfirmBtn.IsClicked)
            {
                m_numberPanel.IsVisible = false;
            }
            if (m_closeBtn.IsClicked)
            {
                // Don't reset SetNumber mode when closing panel — user needs it active to click blocks
                if (m_router.Mode != RouterMode.SetNumber)
                    m_router.SetMode(RouterMode.None);
                m_player.ComponentGui.ModalPanelWidget = null;
                return;
            }

            // Update info labels
            m_selectedLabel.Text = $"Sel: {m_router.SelectedCount}";
            m_area1Label.Text = $"Area1: {m_router.Area1Count}";
            m_area2Label.Text = $"Area2: {m_router.Area2Count}";
            m_area1FaceBtn.Text = $"A1 Face: {m_router.Area1FaceName}";
            m_area2FaceBtn.Text = $"A2 Face: {m_router.Area2FaceName}";

            // Highlight active mode (only SetNumber is a toggle mode)
            m_setNumberBtn.ColorTransform = (m_router.Mode == RouterMode.SetNumber) ? new Color(255, 255, 0) : Color.White;
        }

        private BevelledButtonWidget CreateHalfButton(string text)
        {
            return new BevelledButtonWidget
            {
                Text = text,
                Size = new Vector2(320f, 48f),
                Margin = new Vector2(5f, 3f),
                HorizontalAlignment = WidgetAlignment.Center
            };
        }

        private BevelledButtonWidget CreateSmallButton(string text)
        {
            return new BevelledButtonWidget
            {
                Text = text,
                Size = new Vector2(40f, 30f),
                Margin = new Vector2(3f, 2f),
                HorizontalAlignment = WidgetAlignment.Center
            };
        }
    }
}
