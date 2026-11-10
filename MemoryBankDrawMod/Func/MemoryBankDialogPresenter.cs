using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace MemoryBankDrawMod
{
    internal sealed class MemoryBankDialogPresenter : IDisposable
    {
        private readonly List<SuEditMemoryBankDialog> m_ownedDialogs = new List<SuEditMemoryBankDialog>();
        private readonly HashSet<Dialog> m_enhancedNativeDialogs = new HashSet<Dialog>();
        private readonly List<Dialog> m_removedNativeDialogs = new List<Dialog>();
        private bool m_disposed;
        // Source: EditMemoryBankDialog — preserve the data and completion callback supplied by its owner.
        private static readonly FieldInfo s_memoryBankDataField =
            typeof(EditMemoryBankDialog).GetField(
                "m_memoryBankData",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo s_handlerField =
            typeof(EditMemoryBankDialog).GetField(
                "m_handler",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static void ValidateContract()
        {
            if (s_memoryBankDataField == null || s_memoryBankDataField.FieldType != typeof(MemoryBankData)
                || s_handlerField == null || s_handlerField.FieldType != typeof(Action))
                throw new InvalidOperationException("[MemoryBankDrawMod] EditMemoryBankDialog data/callback fields do not match the required contract.");
            SuEditMemoryBankDialog.ValidateTextBoxContract();
        }

        // Source: existing MemoryBankDialogCompatibility.ReplaceNativeDialogs — preserve the original owning subsystem's state.
        public void EnhanceOpenDialogs()
        {
            if (m_disposed) throw new ObjectDisposedException(nameof(MemoryBankDialogPresenter));
            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            // Source: Dispatcher.Dispatch executes immediately on the game thread; on other
            // threads it queues work. Identity ownership also guards any queued lifecycle calls.
            // Source: mod-development-guide.md Android AOT: avoid trimmed HashSet.RemoveWhere.
            m_removedNativeDialogs.Clear();
            foreach (Dialog native in m_enhancedNativeDialogs)
                if (!dialogs.Contains(native)) m_removedNativeDialogs.Add(native);
            foreach (Dialog native in m_removedNativeDialogs) m_enhancedNativeDialogs.Remove(native);
            m_removedNativeDialogs.Clear();
            for (int i = m_ownedDialogs.Count - 1; i >= 0; i--)
                if (!dialogs.Contains(m_ownedDialogs[i]) && m_ownedDialogs[i].ParentWidget == null)
                {
                    // Source: DialogsManager.HideAllDialogs/Update: externally closed editors
                    // also release their callback after the exit animation fully detaches them.
                    m_ownedDialogs[i].CancelOwnedDialog();
                    m_ownedDialogs[i].Dispose();
                    m_ownedDialogs.RemoveAt(i);
                }
            for (int i = dialogs.Count - 1; i >= 0; i--)
            {
                if (dialogs[i].GetType() != typeof(EditMemoryBankDialog) || m_enhancedNativeDialogs.Contains(dialogs[i]))
                {
                    continue;
                }

                var nativeDialog = (EditMemoryBankDialog)dialogs[i];
                ContainerWidget parentWidget = nativeDialog.ParentWidget;
                MemoryBankData memoryBankData =
                    (MemoryBankData)s_memoryBankDataField.GetValue(nativeDialog);
                Action handler = (Action)s_handlerField.GetValue(nativeDialog);
                if (parentWidget == null || memoryBankData == null)
                {
                    throw new InvalidOperationException("[MemoryBankDrawMod] An open native memory editor has no parent or data owner.");
                }

                var drawDialog = new SuEditMemoryBankDialog(memoryBankData, handler);
                m_ownedDialogs.Add(drawDialog);
                m_enhancedNativeDialogs.Add(nativeDialog);
                DialogsManager.HideDialog(nativeDialog);
                DialogsManager.ShowDialog(parentWidget, drawDialog);
            }
        }

        // Source: DialogsManager.cs:HideDialog — unloading cancels only this Mod's live editors, without invoking save callbacks.
        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            var errors = new List<Exception>();
            foreach (SuEditMemoryBankDialog dialog in m_ownedDialogs)
            {
                try
                {
                    dialog.CancelOwnedDialog();
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            m_ownedDialogs.Clear();
            m_enhancedNativeDialogs.Clear();
            m_removedNativeDialogs.Clear();
            if (errors.Count != 0) throw new AggregateException("[MemoryBankDrawMod] Dialog cleanup failed.", errors);
        }
    }
}
