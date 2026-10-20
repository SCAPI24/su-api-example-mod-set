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

    /// <summary>
    /// Why a client snapshot is being sent (diagnostics only; not on the wire).
    /// </summary>
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
    /// One visible <c>MessageWidget</c> entry on the client (small hint / pickup / level /
    /// crafting note). Pure values: the host re-creates the same engine widget from it.
    ///
    /// Source: Survivalcraft/Game/MessageWidget.cs:Message(text, color, blinking) / MaxMessages = 3
    /// </summary>
    public readonly struct UiSmallMessageData
    {
        public UiSmallMessageData(string text, uint packedColor, bool blinking, uint ageMs)
        {
            Text = text ?? string.Empty;
            PackedColor = packedColor;
            Blinking = blinking;
            AgeMs = ageMs;
        }

        /// <summary>The exact label text the client is drawing.</summary>
        public string Text { get; }

        /// <summary>Engine <c>Color.PackedValue</c> (ARGB).</summary>
        public uint PackedColor { get; }

        /// <summary>Engine blinking flag (decides the 6 s vs 4 s fade curve).</summary>
        public bool Blinking { get; }

        /// <summary>
        /// Age of the entry on the client, in milliseconds. The host shifts its own copy by
        /// the same age so both expire on the engine's schedule
        /// (Source: Survivalcraft/Game/MessageWidget.cs:RemoveOldMessages).
        /// </summary>
        public uint AgeMs { get; }

        // Age **is** part of equality: a client that shows the same text again after the
        // previous copy expired (age resets to ~0) must re-send, otherwise the host would see
        // an unchanged list and keep the new hint unmirrored until the 2 s heartbeat. While a
        // message is visible the age keeps growing, so snapshots are re-sent at the module's
        // 100 ms change limit; the host keeps the *earliest* expiry per entry
        // (UiMirrorMessageState.Sync), so this only refreshes the alignment, never restarts it.
        public bool SameAs(in UiSmallMessageData other) =>
            PackedColor == other.PackedColor && Blinking == other.Blinking &&
            AgeMs == other.AgeMs && string.Equals(Text, other.Text, StringComparison.Ordinal);

        public string Describe() =>
            "\"" + (Text.Length > 32 ? Text.Substring(0, 32) + "…" : Text) + "\" color=0x" +
            PackedColor.ToString("X8", CultureInfo.InvariantCulture) +
            (Blinking ? " blinking" : string.Empty) + " age=" + AgeMs + "ms";
    }

    /// <summary>
    /// The client's handcrafting grid (<c>FullInventoryWidget</c> left half), minus the
    /// derived result slot. Pure values so the host can rebuild the same
    /// <c>ComponentCraftingTable</c> contents and let the **engine** recompute the result.
    ///
    /// Why the result slot is excluded: it is not an input, it is derived by
    /// <c>ComponentCraftingTable.UpdateCraftingResult</c> from the grid + the player's
    /// level (Source: Survivalcraft/Game/ComponentCraftingTable.cs:99-142). Mirroring the
    /// client's result value would be a second model of the recipe; instead the host
    /// writes only the grid and calls the same engine method.
    /// </summary>
    public readonly struct UiHandcraftData
    {
        public UiHandcraftData(int[] values, int[] counts)
        {
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
        }

        /// <summary>Slot values, length = the crafting table's SlotsCount.</summary>
        public int[] Values { get; }

        /// <summary>Slot counts, same length as <see cref="Values"/>.</summary>
        public int[] Counts { get; }

        public int SlotsCount => Math.Min(Values.Length, Counts.Length);

        public bool SameAs(UiHandcraftData other)
        {
            if (SlotsCount != other.SlotsCount) return false;
            for (int i = 0; i < SlotsCount; i++)
            {
                if (Values[i] != other.Values[i] || Counts[i] != other.Counts[i]) return false;
            }
            return true;
        }

        /// <summary>Non-empty input slots (diagnostics: "the grid is not empty").</summary>
        public int NonEmptySlots
        {
            get
            {
                int count = 0;
                for (int i = 0; i < SlotsCount; i++)
                {
                    if (Counts[i] > 0) count++;
                }
                return count;
            }
        }

        public string Describe() =>
            "handcraft slots=" + SlotsCount + " filled=" + NonEmptySlots;
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
            : this(inputKind, hudVisible, screen, dialogPresent, dialogType, modalPresent,
                modalType, chatInputOpen, null, null, 0f, null, 0f, false,
                default(UiHandcraftData))
        {
        }

        public ClientUiStateData(UiInputKind inputKind, bool hudVisible, string screen,
            bool dialogPresent, string dialogType, bool modalPresent, string modalType,
            bool chatInputOpen, UiSmallMessageData[] smallMessages, string floatingMessage,
            float floatingMessageFactor, string overlayMessage, float overlayMessageFactor,
            bool handcraftPresent, UiHandcraftData handcraft)
        {
            InputKind = inputKind;
            HudVisible = hudVisible;
            Screen = screen ?? string.Empty;
            DialogPresent = dialogPresent;
            DialogType = dialogType ?? string.Empty;
            ModalPresent = modalPresent;
            ModalType = modalType ?? string.Empty;
            ChatInputOpen = chatInputOpen;
            SmallMessages = smallMessages ?? Array.Empty<UiSmallMessageData>();
            FloatingMessage = floatingMessage ?? string.Empty;
            FloatingMessageFactor = floatingMessageFactor;
            OverlayMessage = overlayMessage ?? string.Empty;
            OverlayMessageFactor = overlayMessageFactor;
            HandcraftPresent = handcraftPresent;
            Handcraft = handcraft;
        }

        public UiInputKind InputKind { get; }
        public bool HudVisible { get; }
        public string Screen { get; }
        public bool DialogPresent { get; }
        public string DialogType { get; }
        public bool ModalPresent { get; }
        public string ModalType { get; }
        public bool ChatInputOpen { get; }

        /// <summary>Visible <c>MessageWidget</c> entries, oldest first (at most 3).</summary>
        public UiSmallMessageData[] SmallMessages { get; }

        /// <summary><c>ComponentScreenOverlays.FloatingMessage</c> (empty = none).</summary>
        public string FloatingMessage { get; }

        /// <summary><c>ComponentScreenOverlays.FloatingMessageFactor</c>.</summary>
        public float FloatingMessageFactor { get; }

        /// <summary><c>ComponentScreenOverlays.Message</c> (empty = none).</summary>
        public string OverlayMessage { get; }

        /// <summary><c>ComponentScreenOverlays.MessageFactor</c>.</summary>
        public float OverlayMessageFactor { get; }

        /// <summary>The client's modal panel is FullInventoryWidget and its grid was captured.</summary>
        public bool HandcraftPresent { get; }

        /// <summary>The client's handcrafting grid (meaningful only when present).</summary>
        public UiHandcraftData Handcraft { get; }

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
                ChatInputOpen == other.ChatInputOpen &&
                SameSmallMessages(in other) &&
                string.Equals(FloatingMessage, other.FloatingMessage, StringComparison.Ordinal) &&
                SameFactor(FloatingMessageFactor, other.FloatingMessageFactor) &&
                string.Equals(OverlayMessage, other.OverlayMessage, StringComparison.Ordinal) &&
                SameFactor(OverlayMessageFactor, other.OverlayMessageFactor) &&
                HandcraftPresent == other.HandcraftPresent &&
                (!HandcraftPresent || Handcraft.SameAs(other.Handcraft));
        }

        private bool SameSmallMessages(in ClientUiStateData other)
        {
            if (SmallMessages.Length != other.SmallMessages.Length) return false;
            for (int i = 0; i < SmallMessages.Length; i++)
            {
                if (!SmallMessages[i].SameAs(in other.SmallMessages[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// Factors are 0..1 fade values: compare with the same epsilon the HUD writers use
        /// so a 1e-7 flicker does not produce traffic.
        /// </summary>
        private static bool SameFactor(float left, float right) =>
            Math.Abs(left - right) <= 1e-4f;

        public string Describe()
        {
            return "input=" + UiStatePolicy.Name(InputKind) +
                " hud=" + (HudVisible ? "visible" : "hidden") +
                " screen=" + (Screen.Length == 0 ? "-" : Screen) +
                " dialog=" + (DialogPresent ? DialogType : "-") +
                " modal=" + (ModalPresent ? ModalType : "-") +
                " chat=" + (ChatInputOpen ? "open" : "closed") +
                " messages=" + SmallMessages.Length +
                " floating=" + (FloatingMessage.Length == 0
                    ? "-"
                    : "\"" + FloatingMessage + "\":" + FloatingMessageFactor.ToString("F2",
                        CultureInfo.InvariantCulture)) +
                " overlayMsg=" + (OverlayMessage.Length == 0
                    ? "-"
                    : "\"" + OverlayMessage + "\":" + OverlayMessageFactor.ToString("F2",
                        CultureInfo.InvariantCulture)) +
                " handcraft=" + (HandcraftPresent ? Handcraft.Describe() : "-");
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

        /// <summary>Defensive wire bound (the message is 17 small fields + two short names).</summary>
        public const int MaxWireBytes = 1024;

        /// <summary>
        /// Visible <c>MessageWidget</c> entries the engine keeps at once
        /// (Source: Survivalcraft/Game/MessageWidget.cs:MaxMessages = 3). The host mirrors
        /// at most this many; a longer list on the wire is rejected, not truncated.
        /// </summary>
        public const int MaxSmallMessages = 3;

        /// <summary>
        /// Longest small-message text that can be mirrored. Engine hints are short one-liners
        /// (the longest built-in is "Digging with bare hands is slow, use a stick"); a longer
        /// string means the payload is not a hint, so it is rejected instead of clipped.
        /// </summary>
        public const int MaxMessageTextLength = 96;

        /// <summary>
        /// Largest handcrafting inventory the mirror accepts: engine grids are 2x2 (6 slots)
        /// or 3x3 (11 slots) including the derived result + remains slots
        /// (Source: Pak/Database.xml:Player/CraftingTable SlotsCount=6).
        /// </summary>
        public const int MaxHandcraftSlots = 11;

        /// <summary><c>ComponentScreenOverlays</c> only draws a word / hint above this factor
        /// (Source: Survivalcraft/Game/ComponentScreenOverlays.cs:119,123).</summary>
        public const float OverlayDrawThreshold = 0.01f;

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

        /// <summary>
        /// A small-message text is mirrorable when it is non-empty, single-line-safe and
        /// within <see cref="MaxMessageTextLength"/>. Control characters (other than tab)
        /// are rejected: the label widget cannot draw them and they are never engine text.
        /// </summary>
        public static bool TryValidateMessageText(string text, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(text))
            {
                reason = "text-empty";
                return false;
            }
            if (text.Length > MaxMessageTextLength)
            {
                reason = "text-too-long(" + text.Length + ">" + MaxMessageTextLength + ")";
                return false;
            }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\r' || c == '\t') continue;
                if (char.IsControl(c))
                {
                    reason = "text-control-char(0x" + ((int)c).ToString("X2",
                        CultureInfo.InvariantCulture) + ")";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// A screen-overlay word / hint is mirrored as-is when non-empty and short; the
        /// engine writes only literals here ("Zzz...", "Aaa...", "Ugh...", sleep hints).
        /// </summary>
        public static bool TryValidateOverlayText(string text, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(text)) return true; // none = legal (cleared)
            return TryValidateMessageText(text, out reason);
        }

        /// <summary>
        /// An overlay factor is a 0..1 fade value. Values outside [0,1] or non-finite are
        /// rejected: the host would otherwise write a nonsense alpha into the overlay.
        /// </summary>
        public static bool TryValidateFactor(float factor, out string reason)
        {
            reason = null;
            if (float.IsNaN(factor) || float.IsInfinity(factor))
            {
                reason = "factor-not-finite";
                return false;
            }
            if (factor < 0f || factor > 1f)
            {
                reason = "factor-out-of-range(" + factor.ToString("F3",
                    CultureInfo.InvariantCulture) + ")";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The handcrafting grid must have exactly the slot count a crafting table can have
        /// (grid*grid + result + remains) and no negative counts.
        /// </summary>
        public static bool TryValidateHandcraft(in UiHandcraftData handcraft, out string reason)
        {
            reason = null;
            int slots = handcraft.SlotsCount;
            if (slots <= 0 || slots > MaxHandcraftSlots)
            {
                reason = "slots-out-of-range(" + slots + ")";
                return false;
            }
            if (!IsCraftingTableSlotsCount(slots))
            {
                reason = "slots-not-a-crafting-grid(" + slots + ")";
                return false;
            }
            for (int i = 0; i < slots; i++)
            {
                if (handcraft.Counts[i] < 0)
                {
                    reason = "count-negative(slot " + i + ")";
                    return false;
                }
                if (handcraft.Counts[i] > 0 && handcraft.Values[i] == 0)
                {
                    reason = "value-zero-with-count(slot " + i + ")";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Engine <c>ComponentCraftingTable</c> slot layout: <c>SlotsCount = grid*grid + 2</c>
        /// with grid 1..3 (Source: ComponentCraftingTable.cs:88-96, plus RemainsSlotIndex /
        /// ResultSlotIndex :18-20). Skill grids in the game database use 6 (2x2).
        /// </summary>
        public static bool IsCraftingTableSlotsCount(int slots)
        {
            switch (slots)
            {
                case 3: // 1x1 + result + remains
                case 6: // 2x2 + result + remains (player handcrafting)
                case 11: // 3x3 + result + remains (crafting table block)
                    return true;
                default:
                    return false;
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
            // ---- phase 2: small messages / overlay words / handcrafting grid
            if (data.SmallMessages.Length > UiStatePolicy.MaxSmallMessages)
            {
                reason = "small-messages-too-many(" + data.SmallMessages.Length + ">" +
                    UiStatePolicy.MaxSmallMessages + ")";
                return false;
            }
            for (int i = 0; i < data.SmallMessages.Length; i++)
            {
                UiSmallMessageData entry = data.SmallMessages[i];
                if (!UiStatePolicy.TryValidateMessageText(entry.Text, out string textReason))
                {
                    reason = "small-message[" + i + "]-" + textReason;
                    return false;
                }
                encoded.SmallMessages.Add(new P.UiSmallMessage
                {
                    Text = entry.Text,
                    Color = entry.PackedColor,
                    Blinking = entry.Blinking,
                    AgeMs = entry.AgeMs
                });
            }
            if (!UiStatePolicy.TryValidateOverlayText(data.FloatingMessage, out reason))
            {
                reason = "floating-message-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryValidateFactor(data.FloatingMessageFactor, out reason))
            {
                reason = "floating-factor-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryValidateOverlayText(data.OverlayMessage, out reason))
            {
                reason = "overlay-message-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryValidateFactor(data.OverlayMessageFactor, out reason))
            {
                reason = "overlay-factor-" + reason;
                return false;
            }
            encoded.FloatingMessage = data.FloatingMessage;
            encoded.FloatingMessageFactor = data.FloatingMessageFactor;
            encoded.OverlayMessage = data.OverlayMessage;
            encoded.OverlayMessageFactor = data.OverlayMessageFactor;
            encoded.HandcraftPresent = data.HandcraftPresent;
            if (data.HandcraftPresent)
            {
                UiHandcraftData handcraft = data.Handcraft;
                UiHandcraftData validated = handcraft;
                if (!UiStatePolicy.TryValidateHandcraft(in validated, out reason))
                {
                    reason = "handcraft-" + reason;
                    return false;
                }
                int slots = handcraft.SlotsCount;
                for (int i = 0; i < slots; i++)
                {
                    encoded.HandcraftValues.Add(handcraft.Values[i]);
                    encoded.HandcraftCounts.Add(handcraft.Counts[i]);
                }
            }
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
            // ---- phase 2: small messages / overlay words / handcrafting grid
            if (message.SmallMessages.Count > UiStatePolicy.MaxSmallMessages)
            {
                reason = "small-messages-too-many(" + message.SmallMessages.Count + ">" +
                    UiStatePolicy.MaxSmallMessages + ")";
                return false;
            }
            var smallMessages = new UiSmallMessageData[message.SmallMessages.Count];
            for (int i = 0; i < message.SmallMessages.Count; i++)
            {
                P.UiSmallMessage entry = message.SmallMessages[i];
                if (entry == null)
                {
                    reason = "small-message[" + i + "]-null";
                    return false;
                }
                if (!UiStatePolicy.TryValidateMessageText(entry.Text, out string textReason))
                {
                    reason = "small-message[" + i + "]-" + textReason;
                    return false;
                }
                smallMessages[i] = new UiSmallMessageData(entry.Text, entry.Color,
                    entry.Blinking, entry.AgeMs);
            }
            if (!UiStatePolicy.TryValidateOverlayText(message.FloatingMessage, out reason))
            {
                reason = "floating-message-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryValidateFactor(message.FloatingMessageFactor, out reason))
            {
                reason = "floating-factor-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryValidateOverlayText(message.OverlayMessage, out reason))
            {
                reason = "overlay-message-" + reason;
                return false;
            }
            if (!UiStatePolicy.TryValidateFactor(message.OverlayMessageFactor, out reason))
            {
                reason = "overlay-factor-" + reason;
                return false;
            }
            var handcraft = default(UiHandcraftData);
            if (message.HandcraftPresent)
            {
                int slots = message.HandcraftValues.Count;
                if (slots != message.HandcraftCounts.Count)
                {
                    reason = "handcraft-length-mismatch(" + slots + " vs " +
                        message.HandcraftCounts.Count + ")";
                    return false;
                }
                var values = new int[slots];
                var counts = new int[slots];
                for (int i = 0; i < slots; i++)
                {
                    values[i] = message.HandcraftValues[i];
                    counts[i] = message.HandcraftCounts[i];
                }
                handcraft = new UiHandcraftData(values, counts);
                UiHandcraftData validated = handcraft;
                if (!UiStatePolicy.TryValidateHandcraft(in validated, out reason))
                {
                    reason = "handcraft-" + reason;
                    return false;
                }
            }
            revision = message.Revision;
            data = new ClientUiStateData((UiInputKind)message.InputKind, message.HudVisible,
                screen, message.DialogPresent, dialogType, message.ModalPresent, modalType,
                message.ChatInputOpen, smallMessages, message.FloatingMessage,
                message.FloatingMessageFactor, message.OverlayMessage,
                message.OverlayMessageFactor, message.HandcraftPresent, handcraft);
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

        /// <summary>Engine class name of the clothing panel (the one the Clothing button tracks).</summary>
        public const string ClothingWidgetName = "ClothingWidget";

        /// <summary>
        /// The client's <c>ClothingButton.IsChecked</c>, i.e. the engine predicate
        /// <c>IsClothingVisible() = ModalPanelWidget is ClothingWidget</c>
        /// (Source: Survivalcraft/Game/ComponentGui.cs:751-754 written at :431).
        ///
        /// Deliberately a **name comparison, not a whitelist lookup**: the engine compares the
        /// live widget type, and the host must light the button for exactly the same condition
        /// — including modal panels this mod cannot instantiate.
        /// </summary>
        public static bool IsClothingButtonChecked(bool modalPresent, string modalType)
        {
            return modalPresent &&
                string.Equals(modalType, ClothingWidgetName, StringComparison.Ordinal);
        }

        /// <summary>
        /// The client's <c>InventoryButton.IsChecked</c>: engine
        /// <c>IsInventoryVisible() = ModalPanelWidget != null &amp;&amp; !IsClothingVisible()</c>
        /// (Source: Survivalcraft/Game/ComponentGui.cs:756-763 written at :430).
        /// </summary>
        public static bool IsInventoryButtonChecked(bool modalPresent, string modalType)
        {
            return modalPresent && !IsClothingButtonChecked(modalPresent, modalType);
        }

        /// <summary>
        /// The crafting table the <c>FullInventoryWidget</c> left half is bound to
        /// (Source: Survivalcraft/Game/ComponentGui.cs:547 + FullInventoryWidget.cs:41-49):
        /// the handcrafting grid only has to follow the client while **that** panel is open,
        /// and only for its grid (the result slot is derived, see
        /// <see cref="UiHandcraftData"/>).
        /// </summary>
        public static bool IsHandcraftModal(bool modalPresent, string modalType)
        {
            return modalPresent &&
                string.Equals(modalType, "FullInventoryWidget", StringComparison.Ordinal);
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
