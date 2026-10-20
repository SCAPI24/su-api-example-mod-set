using System;
using System.Collections.Generic;
using System.Globalization;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// The three HUD gaps of the H3 replace view that <c>CLIENT_UI_STATE</c> now closes, as
    /// **pure** decisions (no Engine / Game types, so <c>P:\tmp\tsverify</c> compiles and
    /// tests this exact source):
    ///   ① <see cref="UiMirrorButtonState"/> — the two left buttons' <c>IsChecked</c>;
    ///   ② <see cref="UiMirrorMessageState"/> — the client's visible small messages, aged the
    ///      way the engine ages them, so they also **disappear** on the host;
    ///   ③ <see cref="UiHandcraftPlan"/> — the handcrafting grid → ingredient strings →
    ///      result slot, using the engine's own rules.
    ///
    /// The engine adapters (<c>Engine/UiMirrorEngine.cs</c> / <c>Engine/UiMirrorCrafting.cs</c>)
    /// only write widgets from these decisions; every threshold lives here so a regression is
    /// caught by a unit test rather than by a device test.
    /// </summary>
    public static class UiMirrorButtonState
    {
        /// <summary>
        /// The two engine predicates behind the left HUD buttons, computed from the client's
        /// modal panel — the same inputs <c>ComponentGui.UpdateWidgets</c> uses on its own
        /// player (<c>m_inventoryButtonWidget.IsChecked = IsInventoryVisible()</c> at
        /// Source: Survivalcraft/Game/ComponentGui.cs:430-431, definitions at :751-763).
        ///
        /// Why the host must supply this: the third view's buttons belong to the **host's**
        /// ComponentGui, whose <c>ModalPanelWidget</c> stays null while the client's panel is
        /// open (the mirrored panel is a widget we hang in a CanvasWidget, not the host's
        /// modal container), so the engine keeps writing <c>false</c> every frame.
        /// </summary>
        public static void Evaluate(bool modalPresent, string modalType, out bool inventoryChecked,
            out bool clothingChecked)
        {
            clothingChecked = UiStateMirrorPlan.IsClothingButtonChecked(modalPresent, modalType);
            inventoryChecked = UiStateMirrorPlan.IsInventoryButtonChecked(modalPresent, modalType);
        }
    }

    /// <summary>
    /// One mirrored small message the host is currently showing, with the timing that makes it
    /// expire on the client's schedule instead of the host's.
    /// </summary>
    public readonly struct UiMirroredMessage
    {
        public UiMirroredMessage(string text, uint packedColor, bool blinking,
            double expiresAtHostSeconds)
        {
            Text = text ?? string.Empty;
            PackedColor = packedColor;
            Blinking = blinking;
            ExpiresAtHostSeconds = expiresAtHostSeconds;
        }

        public string Text { get; }
        public uint PackedColor { get; }
        public bool Blinking { get; }

        /// <summary>Host clock (seconds) at which this entry stops being displayed.</summary>
        public double ExpiresAtHostSeconds { get; }

        public bool SameKey(string text, uint color, bool blinking) =>
            PackedColor == color && Blinking == blinking &&
            string.Equals(Text, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Per-view state machine mirroring the client's <c>MessageWidget</c> list.
    ///
    /// Engine facts it reproduces:
    ///   · a message lives <c>blinking ? 6 s : 4 s + min(1 s * newlines, 4 s)</c>
    ///     (<c>MessageWidget.cs:Message..ctor</c>);
    ///   · the widget keeps at most 3 and drops older non-blinking entries
    ///     (<c>MessageWidget.cs:RemoveOldMessages</c>: <c>count-num-1 &gt;= 3</c>, and
    ///     <c>num2 &gt; 0 &amp;&amp; !Blinking</c>);
    ///   · it also drops on absolute time (<c>FrameStartTime &gt;= StartTime + Duration</c>).
    ///
    /// The client sends the entries **it is currently showing** plus each entry's age, so the
    /// host neither restarts their timers (a message that flashed on the client would linger
    /// forever here) nor forgets to clear them: an entry the client dropped is gone from the
    /// list, and an entry whose remaining time ran out on the host is dropped locally too.
    ///
    /// No engine objects, no static state — one instance per mirrored view.
    /// </summary>
    public sealed class UiMirrorMessageState
    {
        private readonly List<UiMirroredMessage> m_visible = new List<UiMirroredMessage>(4);
        private readonly List<UiMirroredMessage> m_scratch = new List<UiMirroredMessage>(4);

        /// <summary>Currently displayed entries, oldest first (immutable snapshot per tick).</summary>
        public IReadOnlyList<UiMirroredMessage> Visible => m_visible;

        /// <summary>Entries added / removed by the last <see cref="Sync"/>.</summary>
        public int LastAdded { get; private set; }

        /// <summary>Entries dropped by the last <see cref="Sync"/> (expired or client-cleared).</summary>
        public int LastRemoved { get; private set; }

        /// <summary>Total entries ever displayed (counter for the 10 s summary).</summary>
        public long TotalShown { get; private set; }

        /// <summary>Total entries dropped because their engine duration ran out on the host.</summary>
        public long TotalExpired { get; private set; }

        public void Reset()
        {
            m_visible.Clear();
            m_scratch.Clear();
            LastAdded = 0;
            LastRemoved = 0;
            TotalShown = 0;
            TotalExpired = 0;
        }

        /// <summary>
        /// Engine duration of one message in seconds
        /// (Source: Survivalcraft/Game/MessageWidget.cs:Message..ctor:
        /// <c>Duration = blinking ? 6 : 4 + Min(1 * text.Count(c =&gt; c == '\n'), 4)</c>).
        /// </summary>
        public static float DurationSeconds(string text, bool blinking)
        {
            if (blinking) return 6f;
            int newlines = 0;
            if (text != null)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '\n') newlines++;
                }
            }
            return 4f + Math.Min(1f * newlines, 4f);
        }

        /// <summary>
        /// Reconcile the host's list with the client's visible list at host time
        /// <paramref name="hostNowSeconds"/>. Returns true when the *set* of visible entries
        /// changed (the caller then rebuilds its own <c>MessageWidget</c>).
        ///
        /// Matching is by key (text + color + blinking) **in order**, so a client that shows
        /// the same hint twice still produces two entries here.
        /// </summary>
        public bool Sync(UiSmallMessageData[] clientMessages, double hostNowSeconds)
        {
            m_scratch.Clear();
            int count = clientMessages?.Length ?? 0;
            if (count > UiStatePolicy.MaxSmallMessages) count = UiStatePolicy.MaxSmallMessages;
            for (int i = 0; i < count; i++)
            {
                UiSmallMessageData entry = clientMessages[i];
                double remaining = DurationSeconds(entry.Text, entry.Blinking) -
                    entry.AgeMs / 1000.0;
                if (remaining <= 0.0) continue; // the client is already past its own lifetime
                double expiresAt = hostNowSeconds + remaining;
                // Keep an existing copy of the same key so its expiry is not restarted.
                bool reused = false;
                for (int j = 0; j < m_visible.Count; j++)
                {
                    UiMirroredMessage existing = m_visible[j];
                    if (!existing.SameKey(entry.Text, entry.PackedColor, entry.Blinking)) continue;
                    if (existing.ExpiresAtHostSeconds <= hostNowSeconds) continue;
                    if (ContainsScratch(existing)) continue;
                    m_scratch.Add(new UiMirroredMessage(existing.Text, existing.PackedColor,
                        existing.Blinking, Math.Min(existing.ExpiresAtHostSeconds, expiresAt)));
                    reused = true;
                    break;
                }
                if (reused) continue;
                m_scratch.Add(new UiMirroredMessage(entry.Text, entry.PackedColor, entry.Blinking,
                    expiresAt));
            }

            // Drop expired entries; count how many the host itself timed out (not the client).
            int expired = 0;
            for (int i = m_visible.Count - 1; i >= 0; i--)
            {
                if (m_visible[i].ExpiresAtHostSeconds > hostNowSeconds) continue;
                expired++;
                TotalExpired++;
            }

            LastAdded = 0;
            for (int i = 0; i < m_scratch.Count; i++)
            {
                bool known = false;
                for (int j = 0; j < m_visible.Count; j++)
                {
                    if (!m_visible[j].SameKey(m_scratch[i].Text, m_scratch[i].PackedColor,
                            m_scratch[i].Blinking)) continue;
                    known = true;
                    break;
                }
                if (!known)
                {
                    LastAdded++;
                    TotalShown++;
                }
            }
            LastRemoved = m_visible.Count + LastAdded - m_scratch.Count;
            if (expired > 0 && LastRemoved < expired) LastRemoved = expired;
            bool changed = LastAdded > 0 || !SameList(m_visible, m_scratch);
            m_visible.Clear();
            m_visible.AddRange(m_scratch);
            return changed;
        }

        private bool ContainsScratch(in UiMirroredMessage candidate)
        {
            for (int i = 0; i < m_scratch.Count; i++)
            {
                if (m_scratch[i].SameKey(candidate.Text, candidate.PackedColor, candidate.Blinking))
                    return true;
            }
            return false;
        }

        private static bool SameList(List<UiMirroredMessage> left, List<UiMirroredMessage> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!left[i].SameKey(right[i].Text, right[i].PackedColor, right[i].Blinking))
                    return false;
            }
            return true;
        }

        /// <summary>Auditable one-liner (10 s summary / tests).</summary>
        public string Describe() =>
            "visible=" + m_visible.Count + " shown=" + TotalShown + " expired=" + TotalExpired +
            " added=" + LastAdded + " removed=" + LastRemoved;
    }

    /// <summary>
    /// Counters of the three HUD follow-ups (<c>viewButtonState</c> / <c>viewMessageMirror</c> /
    /// <c>viewCraftingRefresh</c>) plus their explicit failure counters. One instance per
    /// engine owner; no static state. Every write that happens in the engine adapter bumps a
    /// counter here, so "the third screen follows the client" is auditable from one log line.
    /// </summary>
    public sealed class UiMirrorHudCounters
    {
        /// <summary>Button state writes (a button actually flipped to the client's value).</summary>
        public long ViewButtonState { get; private set; }

        /// <summary>Button writes that could not happen (widget missing / write threw).</summary>
        public long ViewButtonFailures { get; private set; }

        /// <summary>Mirrored small messages shown on the host (each entry, not each rebuild).</summary>
        public long ViewMessageMirror { get; private set; }

        /// <summary>Small-message failures (no MessageWidget parent / start-time shift unavailable).</summary>
        public long ViewMessageFailures { get; private set; }

        /// <summary>Overlay <c>FloatingMessage</c>/<c>Message</c> writes (per frame while active).</summary>
        public long ViewOverlayMessage { get; private set; }

        /// <summary>Handcrafting grid refreshes (grid or derived result actually changed).</summary>
        public long ViewCraftingRefresh { get; private set; }

        /// <summary>Handcrafting failures (invalid payload / recipes manager threw / no terrain).</summary>
        public long ViewCraftingFailures { get; private set; }

        /// <summary>Mutation attempts on the read-only presentation inventory (must stay 0).</summary>
        public long ViewCraftingMutationAttempts { get; private set; }

        /// <summary>Frames this HUD mirror was driven (denominator).</summary>
        public long ViewHudFrames { get; private set; }

        /// <summary>Last failure reason (stable key + detail; null = none).</summary>
        public string LastFailureReason { get; private set; }

        public void NoteFrame() => ViewHudFrames++;
        public void NoteButton() => ViewButtonState++;
        public void NoteOverlayMessage() => ViewOverlayMessage++;
        public void NoteMessage(int count) => ViewMessageMirror += count > 0 ? count : 0;
        public void NoteCraftingRefresh() => ViewCraftingRefresh++;
        public void NoteMutationAttempt() => ViewCraftingMutationAttempts++;

        public void NoteButtonFailure(string reason)
        {
            ViewButtonFailures++;
            LastFailureReason = "button: " + reason;
        }

        public void NoteMessageFailure(string reason)
        {
            ViewMessageFailures++;
            LastFailureReason = "message: " + reason;
        }

        public void NoteCraftingFailure(string reason)
        {
            ViewCraftingFailures++;
            LastFailureReason = "crafting: " + reason;
        }

        /// <summary>Auditable one-liner (10 s summary / status line / tests).</summary>
        public string Describe() =>
            "viewButtonState=" + ViewButtonState +
            " viewButtonFailures=" + ViewButtonFailures +
            " viewMessageMirror=" + ViewMessageMirror +
            " viewMessageFailures=" + ViewMessageFailures +
            " viewOverlayMessage=" + ViewOverlayMessage +
            " viewCraftingRefresh=" + ViewCraftingRefresh +
            " viewCraftingFailures=" + ViewCraftingFailures +
            " viewCraftingMutationAttempts=" + ViewCraftingMutationAttempts +
            " viewHudFrames=" + ViewHudFrames +
            " viewHudFailureReason=" + (LastFailureReason ?? "none");
    }

    /// <summary>
    /// The client's handcrafting grid → the host mirror's ingredient / result values.
    ///
    /// Engine facts reproduced (Source: Survivalcraft/Game/ComponentCraftingTable.cs):
    ///   · layout: <c>SlotsCount = grid*grid + 2</c>, result = <c>SlotsCount-2</c>,
    ///     remains = <c>SlotsCount-1</c> (<c>:18-20</c>, grid size from <c>Load</c> <c>:88-96</c>);
    ///   · ingredients: per grid cell, <c>block.CraftingId + ":" + data</c> when the slot has
    ///     items, <c>null</c> otherwise, written at index <c>x + y*3</c> (3x3 array, <c>:101-122</c>);
    ///   · batches = the smallest non-empty slot count (<c>num = Min(num, slotCount)</c>, <c>:119</c>);
    ///   · result = <c>recipe.ResultValue</c> with count <c>recipe.ResultCount * batches</c>,
    ///     and no result when the recipe is missing / has <c>ResultValue == 0</c> (<c>:124-137</c>).
    ///
    /// The host never sends a result of its own: it rebuilds the ingredient strings, asks the
    /// engine's own <c>CraftingRecipesManager.FindMatchingRecipe</c>, and applies the same
    /// arithmetic — so a recipe change in the game data cannot desynchronise the two panels.
    /// </summary>
    public static class UiHandcraftPlan
    {
        /// <summary>Engine grid indices live in a fixed 3x3 ingredient array (<c>:105-121</c>).</summary>
        public const int IngredientArrayLength = 9;

        /// <summary>
        /// Crafting table layout for a slot count, exactly the engine's
        /// <c>ComponentCraftingTable.Load/RemainsSlotIndex/ResultSlotIndex</c> derivation.
        /// </summary>
        public static bool TryGetLayout(int slotsCount, out int gridSize, out int resultSlot,
            out int remainsSlot)
        {
            gridSize = 0;
            resultSlot = -1;
            remainsSlot = -1;
            if (!UiStatePolicy.IsCraftingTableSlotsCount(slotsCount)) return false;
            int grid = (int)Math.Round(Math.Sqrt(slotsCount - 2));
            if (grid < 1 || grid > 3 || grid * grid + 2 != slotsCount) return false;
            gridSize = grid;
            resultSlot = slotsCount - 2;
            remainsSlot = slotsCount - 1;
            return true;
        }

        /// <summary>
        /// Build the 9-entry ingredient array the engine builds. <paramref name="craftingKeyOf"/>
        /// is the caller's <c>slotValue → block.CraftingId + ":" + data</c>; keeping it a
        /// delegate is what lets this file stay free of Game types (no <c>Terrain</c> /
        /// <c>BlocksManager</c> reference here).
        /// Returns false + reason when the inputs cannot produce a grid (never a silent zero).
        /// </summary>
        public static bool TryBuildIngredients(int[] values, int[] counts, int gridSize,
            Func<int, string> craftingKeyOf, out string[] ingredients, out int batches,
            out string reason)
        {
            ingredients = null;
            batches = 0;
            reason = null;
            if (values == null || counts == null || values.Length != counts.Length)
            {
                reason = "grid-arrays-mismatch";
                return false;
            }
            if (gridSize < 1 || gridSize > 3)
            {
                reason = "grid-size-invalid(" + gridSize + ")";
                return false;
            }
            int needed = gridSize * gridSize;
            if (values.Length < needed)
            {
                reason = "grid-shorter-than-" + needed + "(" + values.Length + ")";
                return false;
            }
            if (craftingKeyOf == null)
            {
                reason = "no-block-key-resolver";
                return false;
            }
            var built = new string[IngredientArrayLength];
            int smallest = int.MaxValue;
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    int cell = x + y * 3;
                    int slot = x + y * gridSize;
                    int count = counts[slot];
                    if (count <= 0)
                    {
                        built[cell] = null;
                        continue;
                    }
                    built[cell] = craftingKeyOf(values[slot]);
                    if (string.IsNullOrEmpty(built[cell]))
                    {
                        reason = "ingredient-key-empty(slot " + slot + ")";
                        return false;
                    }
                    if (count < smallest) smallest = count;
                }
            }
            if (smallest == int.MaxValue) smallest = 0;
            ingredients = built;
            batches = smallest;
            return true;
        }

        /// <summary>
        /// Result slot contents for a matched recipe. Mirrors
        /// <c>ComponentCraftingTable.UpdateCraftingResult:124-137</c>: a null recipe (or
        /// <c>ResultValue == 0</c>) means "no result" — never a stale value.
        /// </summary>
        public static bool TryComposeResult(bool hasRecipe, int resultValue, int resultCount,
            int batches, out int value, out int count, out string reason)
        {
            value = 0;
            count = 0;
            reason = null;
            if (!hasRecipe)
            {
                reason = "no-matching-recipe";
                return false;
            }
            if (resultValue == 0)
            {
                reason = "recipe-result-zero";
                return false;
            }
            if (resultCount <= 0)
            {
                reason = "recipe-result-count-non-positive(" + resultCount + ")";
                return false;
            }
            if (batches <= 0)
            {
                reason = "no-batches";
                return false;
            }
            value = resultValue;
            count = resultCount * batches;
            return true;
        }

        /// <summary>Auditable one-liner for the counters / tests.</summary>
        public static string DescribeGrid(int[] values, int[] counts)
        {
            if (values == null || counts == null || values.Length != counts.Length)
                return "grid{invalid}";
            int filled = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > 0) filled++;
            }
            return "grid{" + values.Length + " slots, " + filled + " filled}";
        }
    }
}
