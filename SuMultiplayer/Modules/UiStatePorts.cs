using System;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// Read-only access to the **client's open container panel** (the block cell whose
    /// inventory the client's modal panel is bound to). Implemented by the block-use
    /// module (<c>Modules/BlockUseSyncModule.cs</c>), which already tracks both directions:
    ///   · client: <c>m_openKey</c> = the replica panel it opened (set from ContainerState);
    ///   · host:   <c>m_openContainers[clientId]</c> = the container that client has open
    ///             (set on BlockUseRequest, cleared on its Close request / client leave).
    ///
    /// The UI mirror does not invent a cell: without this key a container panel is
    /// reported as unsupported (<c>uiMirrorUnsupported:&lt;type&gt;</c>) instead of showing
    /// an empty panel.
    /// </summary>
    public interface IUiContainerCellPort
    {
        /// <summary>Client: the container cell of the currently open replica panel.</summary>
        bool TryGetLocalContainerCell(out int x, out int y, out int z);

        /// <summary>Host: the container cell a given client currently has open.</summary>
        bool TryGetClientContainerCell(int clientId, out int x, out int y, out int z);
    }

    /// <summary>
    /// Client-side capture of the interface layer (implemented by
    /// <c>Engine/ClientUiStateReader.cs</c>). Read-only: it inspects the engine's current
    /// screen / dialogs / modal panel / HUD gate / input switch and copies them into a value.
    /// </summary>
    public interface IClientUiStateSource
    {
        /// <summary>
        /// Capture the primary local player's UI state. false + reason = the engine cannot
        /// answer right now (no world / no local player / no current screen); the caller
        /// keeps its previous snapshot and retries next frame (它绝不猜一个状态).
        /// </summary>
        bool TryCapture(out ClientUiStateData state, out string reason);
    }

    /// <summary>
    /// What one host-side apply actually changed. Bit flags so the module can count
    /// transitions (and unsupported types) without re-reading engine state.
    /// </summary>
    [Flags]
    public enum UiMirrorOutcome
    {
        None = 0,
        /// <summary>Input form (touch pads / side panels) changed in the view.</summary>
        Input = 1 << 0,
        /// <summary>View HUD visibility changed.</summary>
        Hud = 1 << 1,
        /// <summary>Screen changed and needed no overlay (the client is on the game screen).</summary>
        Screen = 1 << 2,
        /// <summary>A dialog was instantiated in the view.</summary>
        DialogOpened = 1 << 3,
        /// <summary>A mirrored dialog was removed from the view.</summary>
        DialogClosed = 1 << 4,
        /// <summary>A modal panel was instantiated in the view.</summary>
        ModalOpened = 1 << 5,
        /// <summary>A mirrored modal panel was removed from the view.</summary>
        ModalClosed = 1 << 6,
        /// <summary>The client's screen is not the game screen and cannot be mirrored.</summary>
        UnsupportedScreen = 1 << 7,
        /// <summary>The open dialog class cannot be rebuilt from its name.</summary>
        UnsupportedDialog = 1 << 8,
        /// <summary>The open modal panel class cannot be rebuilt (or its data is missing).</summary>
        UnsupportedModal = 1 << 9,
        /// <summary>The chat input line is open; the mod's chat HUD is not per-view clonable.</summary>
        UnsupportedChat = 1 << 10,
        /// <summary>input_kind was 0 (unknown): the previous mapping is kept.</summary>
        InputUnknown = 1 << 11,
        /// <summary>No host-side view follows that client (yet).</summary>
        ViewMissing = 1 << 12,
        /// <summary>The client's input form was re-asserted because the engine reset it.</summary>
        InputReasserted = 1 << 13,
        /// <summary>The engine switch for the input form could not be written (explicit failure).</summary>
        UnsupportedInput = 1 << 14
    }

    /// <summary>
    /// Host-side application of a client's UI state to the split-screen view that follows
    /// that client (implemented by <c>Engine/UiMirrorEngine.cs</c>).
    ///
    /// The view is resolved by the engine's own binding: the GameWidget whose
    /// <c>Target</c> is that client avatar's <c>ComponentPlayer</c> — the auto client view
    /// (<c>Engine/ClientViewEngine.cs</c>) and H3 replace mode
    /// (<c>Engine/MirrorCharacterEngine.cs:BindViewToSource</c>) both establish exactly that.
    /// </summary>
    public interface IUiMirrorPort
    {
        /// <summary>
        /// Apply one snapshot. false + reason = nothing was applied (no view / build failed);
        /// <paramref name="outcome"/> still carries the flags that explain it.
        /// Never writes anything on the client avatar: only the view's own HUD widgets and
        /// its local player's <c>ComponentInput.IsControlledByTouch</c> are touched.
        /// </summary>
        bool TryApply(int clientId, in ClientUiStateData state, out UiMirrorOutcome outcome,
            out string reason);

        /// <summary>
        /// Cheap read-only check: does the current view still show this client's last applied
        /// state? false = the view was (re)created or the engine reset the input switch, so the
        /// caller should re-apply its cached snapshot (rate limited by the caller).
        /// </summary>
        bool IsApplied(int clientId);

        /// <summary>Drop one client's mirrored widgets and restore the view's own state.</summary>
        void ResetClient(int clientId, string reason);

        /// <summary>Drop every mirrored widget (room end / world unload / module detach).</summary>
        void ResetAll(string reason);

        /// <summary>Status-line counters (no behaviour change).</summary>
        string Describe();
    }

    /// <summary>
    /// Optional per-frame presentation capability of <see cref="IUiMirrorPort"/>.
    ///
    /// Why a separate interface: the phase-2 HUD items (left inventory / clothing buttons,
    /// bubbling messages, screen-overlay words) are **per-frame** facts — the engine rewrites
    /// <c>Button.IsChecked</c> and clears <c>ComponentScreenOverlays</c> every frame
    /// (Source: ComponentGui.cs:430-431, ComponentScreenOverlays.cs:60-77), and messages must
    /// disappear exactly when the client's copy does. <see cref="IUiMirrorPort.TryApply"/> only
    /// runs on a snapshot (<=10 Hz), so these need one pass **after** the engine update and
    /// **before** Draw (the mod's Frame.Update hook: Program.cs:146-147), i.e. from the
    /// module's per-frame <c>Tick</c>.
    ///
    /// Implemented by <c>Engine/UiMirrorEngine.cs</c>; a port that only supports snapshot
    /// application leaves it out, and the module logs exactly that (no silent no-op).
    /// </summary>
    public interface IUiMirrorTickPort
    {
        /// <summary>
        /// One presentation pass. Must only write display-layer fields of the view that
        /// follows each client (buttons / message widget / screen overlays); no authoritative
        /// state, no client records, no second widget model.
        /// </summary>
        void Present();
    }
}
