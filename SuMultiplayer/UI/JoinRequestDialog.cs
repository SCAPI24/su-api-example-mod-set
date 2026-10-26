using System;
using System.Xml.Linq;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 主机侧加入请求对话框（三按钮 Allow / Later / Reject，2026-10-26）。
    ///
    /// 布局参考引擎 EditMemoryBankDialog（同款 Dialog 骨架 + 一排 160x60 按钮；布局资产
    /// Content/Dialogs/JoinRequestDialog.xml 随 .scmod 分发）。与引擎 MessageDialog 的差别：
    ///   · 三个按钮，中间是 Later（稍后）—— 点了不裁决，连接保持等待，条目留在请求队列，
    ///     由 IF 按钮的队列页随时再弹出；
    ///   · **模态输入屏蔽**：引擎 DialogsManager 的 CoverWidget 只挡鼠标不挡键盘，游戏弹窗时
    ///     WASD / 跳跃 / 挖掘照常生效（用户实测）。这里在 Update 里每帧清空本地玩家的
    ///     PlayerInput 意图（ComponentInput 的产出、ComponentLocomotion/Miner 的输入）——
    ///     对话框存活期间角色完全不动。Esc = Later（不丢请求）。
    /// </summary>
    public sealed class JoinRequestDialog : Dialog
    {
        private readonly LabelWidget m_detailLabel;
        private readonly ButtonWidget m_allowButton;
        private readonly ButtonWidget m_laterButton;
        private readonly ButtonWidget m_rejectButton;

        /// <summary>决议回调："Allow" / "Later" / "Reject"。只在按钮按下时触发一次（对话框随即关闭）。</summary>
        private readonly Action<string> m_decided;
        private bool m_decidedDone;

        public JoinRequestDialog(string detail, Action<string> decided)
        {
            XElement node = ContentManager.Get<XElement>("Mod/Dialogs/JoinRequestDialog");
            LoadContents(this, node);
            m_detailLabel = Children.Find<LabelWidget>("JoinRequestDialog.Detail");
            m_allowButton = Children.Find<ButtonWidget>("JoinRequestDialog.Allow");
            m_laterButton = Children.Find<ButtonWidget>("JoinRequestDialog.Later");
            m_rejectButton = Children.Find<ButtonWidget>("JoinRequestDialog.Reject");
            m_detailLabel.Text = detail ?? string.Empty;
            m_decided = decided;
        }

        public override void Update()
        {
            SuppressPlayerInput();
            if (m_allowButton.IsClicked) Decide("Allow");
            else if (m_laterButton.IsClicked) Decide("Later");
            else if (m_rejectButton.IsClicked) Decide("Reject");
            else if (Input.Cancel || Input.Back) Decide("Later");
        }

        private void Decide(string kind)
        {
            if (m_decidedDone || m_decided == null) return;
            m_decidedDone = true;
            DialogsManager.HideDialog(this);
            m_decided(kind);
        }

        /// <summary>
        /// 对话框存活期间把所有本地玩家的输入意图清零。PlayerInput 是 ComponentInput 每帧
        /// 重建的意图包（Game/PlayerInput.cs：Move/Jump/Dig/Aim/Interact/Drop/Look…），
        /// 在对话框 Update（控件树阶段）清空后，本帧的移动 / 挖掘 / 交互 / 丢物品全部失效。
        /// 关闭后自动恢复（下一帧 ComponentInput 重新按键盘 / 触摸写入）。
        /// </summary>
        private void SuppressPlayerInput()
        {
            Project project = GameManager.Project;
            SubsystemPlayers players = project == null
                ? null
                : project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;
            for (int i = 0; i < players.ComponentPlayers.Count; i++)
            {
                ComponentPlayer player = players.ComponentPlayers[i];
                ComponentInput input = player?.Entity?.FindComponent<ComponentInput>(false);
                if (input == null) continue;
                PlayerInput pi = input.PlayerInput;
                pi.Look = Vector2.Zero;
                pi.CameraLook = Vector2.Zero;
                pi.Move = Vector3.Zero;
                pi.CrouchMove = Vector3.Zero;
                pi.CameraMove = Vector3.Zero;
                pi.CameraCrouchMove = Vector3.Zero;
                pi.VrMove = null;
                pi.VrLook = null;
                pi.ToggleCreativeFly = false;
                pi.ToggleCrouch = false;
                pi.ToggleMount = false;
                pi.EditItem = false;
                pi.Jump = false;
                pi.ScrollInventory = 0;
                pi.ToggleInventory = false;
                pi.ToggleClothing = false;
                pi.TakeScreenshot = false;
                pi.SwitchCameraMode = false;
                pi.TimeOfDay = false;
                pi.Lighting = false;
                pi.Precipitation = false;
                pi.Fog = false;
                pi.Dig = null;
                pi.Hit = null;
                pi.Aim = null;
                pi.Interact = null;
                pi.PickBlockType = null;
                pi.Drop = false;
                pi.SelectInventorySlot = null;
                // PlayerInput 属性是只读（返回副本）：写回必须落到私有字段 m_playerInput
                // （ComponentInput 下一帧重建前，本帧所有读者都从这里取）。
                Game.Program.ModManager?.ModParentField?.ModifyParentField(
                    input, "m_playerInput", pi, typeof(ComponentInput));
            }
        }
    }
}
