using System;
using System.Globalization;
using Google.Protobuf;
using P = SuMultiplayer.Protocol.UiState;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// Client interface-layer kinds carried by <c>CLIENT_UI_STATE</c> (id 100).
    /// Numbers are part of the wire format: append only, never renumber.
    /// </summary>
    public enum UiInputKind
    {
        /// <summary>Never sent by this build (forward compatible: host keeps the previous mapping).</summary>
        Unknown = 0,
        /// <summary>Touch screen pads / rose (client shows the engine touch controls).</summary>
        Touch = 1,
        /// <summary>Keyboard + mouse.</summary>
        KeyboardMouse = 2,
        /// <summary>One of the four gamepads.</summary>
        Gamepad = 3
    }

    /// <summary>
    /// Engine modal panel classes this mod knows how to instantiate in a host view.
    /// The wire only carries the class name; this enum is the local whitelist.
    /// </summary>
    public enum UiModalKind
    {
        None = 0,
        /// <summary>Source: Survivalcraft/Game/FullInventoryWidget.cs</summary>
        Inventory = 1,
        /// <summary>Source: Survivalcraft/Game/CreativeInventoryWidget.cs</summary>
        CreativeInventory = 2,
        /// <summary>Source: Survivalcraft/Game/ClothingWidget.cs</summary>
        Clothing = 3,
        /// <summary>Source: Survivalcraft/Game/ChestWidget.cs</summary>
        Chest = 4,
        /// <summary>Source: Survivalcraft/Game/FurnaceWidget.cs</summary>
        Furnace = 5,
        /// <summary>Source: Survivalcraft/Game/CraftingTableWidget.cs</summary>
        CraftingTable = 6,
        /// <summary>Source: Survivalcraft/Game/DispenserWidget.cs</summary>
        Dispenser = 7,
        /// <summary>Source: Survivalcraft/Game/VitalStatsWidget.cs</summary>
        VitalStats = 8,
    }

    /// <summary>
    /// Engine dialog classes this mod knows how to instantiate in a host view.
    /// Parameterised dialogs (MessageDialog / TextBoxDialog / ListSelectionDialog /
    /// edit dialogs) cannot be rebuilt from a class name and are therefore reported
    /// as unsupported instead of being faked.
    /// </summary>
    public enum UiDialogKind
    {
        None = 0,
        /// <summary>Source: Survivalcraft/Game/GameMenuDialog.cs（暂停菜单 / 世界与玩家统计）</summary>
        GameMenu = 1,
        /// <summary>Source: Survivalcraft/Game/KeyboardHelpDialog.cs</summary>
        KeyboardHelp = 2,
        /// <summary>Source: Survivalcraft/Game/GamepadHelpDialog.cs</summary>
        GamepadHelp = 3
    }

    /// <summary>Why a client snapshot is being sent (diagnostics only; not on the wire).</summary>
    public enum UiSendReason
    {
        None = 0,
        /// <summary>First snapshot after connect / reconnect.</summary>
        First = 1,
        /// <summary>A field changed.</summary>
        Changed = 2,
        /// <summary>Nothing changed but the heartbeat interval elapsed.</summary>
        Heartbeat = 3
    }

    /// <summary>
    /// One client interface-layer snapshot (pure values, no engine types).
    /// Strings are empty when the corresponding "present" flag is false.
    /// </summary>
    public readonly struct ClientUiStateData
    {
        public ClientUiStateData(UiInputKind inputKind, bool hudVisible, string screen,
            bool dialogPresent, string dialogType, bool modalPresent, string modalType,
            bool chatInputOpen)
        {
            InputKind = inputKind;
            HudVisible = hudVisible;
            Screen = screen ?? string.Empty;
            DialogPresent = dialogPresent;
            DialogType = dialogType ?? string.Empty;
            ModalPresent = modalPresent;
            ModalType = modalType ?? string.Empty;
            ChatInputOpen = chatInputOpen;
        }

        public UiInputKind InputKind { get; }
        public bool HudVisible { get; }
        public string Screen { get; }
        public bool DialogPresent { get; }
        public string DialogType { get; }
        public bool ModalPresent { get; }
        public string ModalType { get; }
        public bool ChatInputOpen { get; }

        /// <summary>
        /// Field equality (revision is not part of the data): the diff layer only sends
        /// when this is false, so an unchanged client produces no traffic.
        /// </summary>
        public bool SameState(in ClientUiStateData other)
        {
            return InputKind == other.InputKind &&
                HudVisible == other.HudVisible &&
                string.Equals(Screen, other.Screen, StringComparison.Ordinal) &&
                DialogPresent == other.DialogPresent &&
                string.Equals(DialogType, other.DialogType, StringComparison.Ordinal) &&
                ModalPresent == other.ModalPresent &&
                string.Equals(ModalType, other.ModalType, StringComparison.Ordinal) &&
                ChatInputOpen == other.ChatInputOpen;
        }

        public string Describe()
        {
            return "input=" + UiStatePolicy.Name(InputKind) +
                " hud=" + (HudVisible ? "visible" : "hidden") +
                " screen=" + (Screen.Length == 0 ? "-" : Screen) +
                " dialog=" + (DialogPresent ? DialogType : "-") +
                " modal=" + (ModalPresent ? ModalType : "-") +
                " chat=" + (ChatInputOpen ? "open" : "closed");
        }
    }

    /// <summary>
    /// The engine <c>Game.WidgetInputDevice</c> bits this module reads (numeric copies so
    /// the pure logic compiles without the Game assembly; the values are guarded by a
    /// source-guard test against Survivalcraft/Game/WidgetInputDevice.cs).
    /// Source: Survivalcraft/Game/WidgetInputDevice.cs:6-33
    /// </summary>
    public static class UiInputDeviceBits
    {
        public const uint None = 0;
        public const uint Keyboard = 0x1;
        public const uint MultiKeyboards = 0x1E;
        public const uint Mouse = 0x20;
        public const uint MultiMice = 0x3C0;
        public const uint Touch = 0x400;
        public const uint Gamepads = 0x7800;
    }

    /// <summary>
    /// Wire / behaviour policy of client UI state mirroring: bounds, validation, the
    /// input-kind mapping, type whitelists and the diff/heartbeat decision.
    ///
    /// Engine facts behind the mapping (why <c>IsControlledByTouch</c> is the live switch):
    ///   · <c>ComponentInput.IsControlledByTouch</c> starts true, is set false by mouse
    ///     movement / gamepad sticks / VR, and true again by the touch widgets
    ///     (Source: Survivalcraft/Game/ComponentInput.cs:30,168,297,316,426,489,511,519);
    ///   · <c>ComponentGui.UpdateWidgets</c> is the ONLY consumer that decides whether the
    ///     touch pads / move rose / side panels are drawn
    ///     (Source: Survivalcraft/Game/ComponentGui.cs:395-419);
    ///   · the user's device selection (<c>PlayerData.InputDevice</c>) is persisted and
    ///     feeds <c>GameWidget.DetermineInputDevices</c> for *every* local view
    ///     (Source: Survivalcraft/Game/PlayerData.cs:388,401; GameWidget.cs:122-163), so it
    ///     is only read here, never sent as a host-side write.
    /// </summary>
    public static class UiStatePolicy
    {
        /// <summary>Heartbeat: an unchanged client still re-asserts its UI once per 2 s.</summary>
        public const int HeartbeatMs = 2000;

        /// <summary>Hard cap on change-driven sends (10 Hz) — UI changes are human scale.</summary>
        public const int MinSendIntervalMs = 100;

        /// <summary>Engine class names are short; anything longer is not an engine type name.</summary>
        public const int MaxNameLength = 64;

        /// <summary>Defensive wire bound (the message is 9 small fields + two short names).</summary>
        public const int MaxWireBytes = 1024;

        /// <summary>The engine screen name that means "playing" (nothing to overlay).</summary>
        public const string GameScreenName = "GameScreen";

        /// <summary>
        /// Map the client's active GameWidget device selection + live touch switch to the
        /// wire kind. The live switch wins: the engine shows touch controls while it is
        /// true even if the user configured mouse + keyboard (ComponentGui reads it, not
        /// PlayerData.InputDevice).
        /// </summary>
        public static bool TryMapInputKind(uint devices, bool isControlledByTouch,
            out UiInputKind kind, out string reason)
        {
            kind = UiInputKind.Unknown;
            reason = null;
            if (isControlledByTouch)
            {
                kind = UiInputKind.Touch;
                return true;
            }
            if ((devices & UiInputDeviceBits.Gamepads) != 0)
            {
                kind = UiInputKind.Gamepad;
                return true;
            }
            if ((devices & (UiInputDeviceBits.Keyboard | UiInputDeviceBits.MultiKeyboards |
                    UiInputDeviceBits.Mouse | UiInputDeviceBits.MultiMice)) != 0)
            {
                kind = UiInputKind.KeyboardMouse;
                return true;
            }
            if ((devices & UiInputDeviceBits.Touch) != 0)
            {
                kind = UiInputKind.Touch;
                return true;
            }
            // No touch switch, no gamepad, no keyboard/mouse: the engine would still show
            // the touch layout (ComponentInput starts touch-controlled), so mirror that.
            kind = UiInputKind.Touch;
            return true;
        }

        /// <summary>
        /// A bounded, validated engine class name: 1..64 chars from [A-Za-z0-9_.].
        /// Rejects markup / control characters / whitespace instead of sanitising them.
        /// </summary>
        public static bool TryGetValidName(string name, out string normalized, out string reason)
        {
            normalized = null;
            reason = null;
            if (string.IsNullOrEmpty(name))
            {
                reason = "name-empty";
                return false;
            }
            if (name.Length > MaxNameLength)
            {
                reason = "name-too-long(" + name.Length + ">" + MaxNameLength + ")";
                return false;
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '_' || c == '.';
                if (ok) continue;
                reason = "name-invalid-char(0x" + ((int)c).ToString("X2",
                    CultureInfo.InvariantCulture) + ")";
                return false;
            }
            normalized = name;
            return true;
        }

        public static bool TryGetValidOptionalName(bool present, string name, out string normalized,
            out string reason)
        {
            if (!present)
            {
                normalized = string.Empty;
                reason = null;
                if (!string.IsNullOrEmpty(name))
                {
                    reason = "name-present-without-flag";
                    return false;
                }
                return true;
            }
            return TryGetValidName(name, out normalized, out reason);
        }

        /// <summary>Diagnostics word for an input kind.</summary>
        public static string Name(UiInputKind kind)
        {
            switch (kind)
            {
                case UiInputKind.Touch: return "touch";
                case UiInputKind.KeyboardMouse: return "keyboard-mouse";
                case UiInputKind.Gamepad: return "gamepad";
                default: return "unknown";
            }
        }
    }

    /// <summary>
    /// Strict <c>CLIENT_UI_STATE</c> codec. Every invalid input is rejected with a reason
    /// (never truncated / never defaulted):
    ///   · revision &gt; 0;
    ///   · input_kind 0..3;
    ///   · screen / dialog_type / modal_type 1..64 chars [A-Za-z0-9_.], and the type
    ///     fields must be empty exactly when their "present" flag is false;
    ///   · encoded size within <see cref="UiStatePolicy.MaxWireBytes"/>.
    /// </summary>
    public static class UiStateCodec
    {
        public static bool TryEncode(uint revision, in ClientUiStateData data,
            out P.ClientUiState message, out string reason)
        {
            message = null;
            reason = null;
            if (revision == 0)
            {
                reason = "revision-zero";
                return false;
            }
            if (data.InputKind != UiInputKind.Unknown && data.InputKind != UiInputKind.Touch &&
                data.InputKind != UiInputKind.KeyboardMouse && data.InputKind != UiInputKind.Gamepad)
            {
                reason = "input-kind-invalid(" + (int)data.InputKind + ")";
                return false;
            }
            if (!UiStatePolicy.TryGetValidName(data.Screen, out string screen, out reason))
            {
                reason = "screen-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryGetValidOptionalName(data.DialogPresent, data.DialogType,
                    out string dialogType, out reason))
            {
                reason = "dialog-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryGetValidOptionalName(data.ModalPresent, data.ModalType,
                    out string modalType, out reason))
            {
                reason = "modal-" + reason;
                return false;
            }
            var encoded = new P.ClientUiState
            {
                Revision = revision,
                InputKind = (uint)data.InputKind,
                HudVisible = data.HudVisible,
                Screen = screen,
                DialogPresent = data.DialogPresent,
                DialogType = dialogType,
                ModalPresent = data.ModalPresent,
                ModalType = modalType,
                ChatInputOpen = data.ChatInputOpen
            };
            if (encoded.CalculateSize() > UiStatePolicy.MaxWireBytes)
            {
                reason = "wire-too-large(" + encoded.CalculateSize() + ")";
                return false;
            }
            message = encoded;
            return true;
        }

        public static bool TryDecode(P.ClientUiState message, out ClientUiStateData data,
            out uint revision, out string reason)
        {
            data = default;
            revision = 0;
            reason = null;
            if (message == null)
            {
                reason = "message-null";
                return false;
            }
            if (message.Revision == 0)
            {
                reason = "revision-zero";
                return false;
            }
            if (message.InputKind > (uint)UiInputKind.Gamepad)
            {
                reason = "input-kind-invalid(" + message.InputKind + ")";
                return false;
            }
            if (!UiStatePolicy.TryGetValidName(message.Screen, out string screen, out reason))
            {
                reason = "screen-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryGetValidOptionalName(message.DialogPresent, message.DialogType,
                    out string dialogType, out reason))
            {
                reason = "dialog-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryGetValidOptionalName(message.ModalPresent, message.ModalType,
                    out string modalType, out reason))
            {
                reason = "modal-" + reason;
                return false;
            }
            if (message.CalculateSize() > UiStatePolicy.MaxWireBytes)
            {
                reason = "wire-too-large";
                return false;
            }
            revision = message.Revision;
            data = new ClientUiStateData((UiInputKind)message.InputKind, message.HudVisible,
                screen, message.DialogPresent, dialogType, message.ModalPresent, modalType,
                message.ChatInputOpen);
            return true;
        }
    }

    /// <summary>
    /// Change-driven send decision with a heartbeat (no per-frame traffic):
    ///   · no previous snapshot (connect / reconnect) → send immediately;
    ///   · fields differ → send, but never more often than <see cref="UiStatePolicy.MinSendIntervalMs"/>;
    ///   · fields identical and heartbeat elapsed → send (recovers a lost / unapplied state).
    /// </summary>
    public static class UiStateDiff
    {
        public static bool ShouldSend(bool hasPrevious, in ClientUiStateData previous,
            long previousMs, in ClientUiStateData next, long nowMs, out UiSendReason reason)
        {
            reason = UiSendReason.None;
            if (!hasPrevious)
            {
                reason = UiSendReason.First;
                return true;
            }
            if (!next.SameState(in previous))
            {
                // Change detected. A same-frame change pair is deferred (not dropped): the
                // next frame still differs from the last *sent* snapshot.
                if (nowMs - previousMs < UiStatePolicy.MinSendIntervalMs) return false;
                reason = UiSendReason.Changed;
                return true;
            }
            if (nowMs - previousMs >= UiStatePolicy.HeartbeatMs)
            {
                reason = UiSendReason.Heartbeat;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// What the host can build from a client's UI state, and what it explicitly cannot.
    /// Pure name → engine-class mapping; the engine adapter instantiates the class.
    /// </summary>
    public static class UiStateMirrorPlan
    {
        /// <summary>Modal panel class name → kind. Unknown / null → <see cref="UiModalKind.None"/>.</summary>
        public static UiModalKind ClassifyModal(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return UiModalKind.None;
            switch (typeName)
            {
                case "FullInventoryWidget": return UiModalKind.Inventory;
                case "CreativeInventoryWidget": return UiModalKind.CreativeInventory;
                case "ClothingWidget": return UiModalKind.Clothing;
                case "ChestWidget": return UiModalKind.Chest;
                case "FurnaceWidget": return UiModalKind.Furnace;
                case "CraftingTableWidget": return UiModalKind.CraftingTable;
                case "DispenserWidget": return UiModalKind.Dispenser;
                case "VitalStatsWidget": return UiModalKind.VitalStats;
                default: return UiModalKind.None;
            }
        }

        /// <summary>Dialog class name → kind. Unknown / null → <see cref="UiDialogKind.None"/>.</summary>
        public static UiDialogKind ClassifyDialog(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return UiDialogKind.None;
            switch (typeName)
            {
                case "GameMenuDialog": return UiDialogKind.GameMenu;
                case "KeyboardHelpDialog": return UiDialogKind.KeyboardHelp;
                case "GamepadHelpDialog": return UiDialogKind.GamepadHelp;
                default: return UiDialogKind.None;
            }
        }

        /// <summary>Container panels need the block cell the client's panel is bound to.</summary>
        public static bool IsContainerModal(UiModalKind kind)
        {
            return kind == UiModalKind.Chest || kind == UiModalKind.Furnace ||
                kind == UiModalKind.CraftingTable || kind == UiModalKind.Dispenser;
        }

        /// <summary>
        /// Full engine screens cannot be mirrored inside one GameWidget: ScreensManager owns
        /// a single CurrentScreen instance per process (Source: ScreensManager.cs:49,83-113)
        /// and a second instance would run its Update/Leave side effects on the host. Only the
        /// game screen (= no overlay screen) is "mirrored" — and that is a no-op.
        /// </summary>
        public static bool IsScreenMirrorable(string screenName)
        {
            return string.Equals(screenName, UiStatePolicy.GameScreenName,
                StringComparison.Ordinal);
        }

        public static string Name(UiModalKind kind)
        {
            switch (kind)
            {
                case UiModalKind.Inventory: return "FullInventoryWidget";
                case UiModalKind.CreativeInventory: return "CreativeInventoryWidget";
                case UiModalKind.Clothing: return "ClothingWidget";
                case UiModalKind.Chest: return "ChestWidget";
                case UiModalKind.Furnace: return "FurnaceWidget";
                case UiModalKind.CraftingTable: return "CraftingTableWidget";
                case UiModalKind.Dispenser: return "DispenserWidget";
                case UiModalKind.VitalStats: return "VitalStatsWidget";
                default: return "none";
            }
        }

        public static string Name(UiDialogKind kind)
        {
            switch (kind)
            {
                case UiDialogKind.GameMenu: return "GameMenuDialog";
                case UiDialogKind.KeyboardHelp: return "KeyboardHelpDialog";
                case UiDialogKind.GamepadHelp: return "GamepadHelpDialog";
                default: return "none";
            }
        }
    }
}
