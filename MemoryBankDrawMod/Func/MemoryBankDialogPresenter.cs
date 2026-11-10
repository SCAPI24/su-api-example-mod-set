using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace MemoryBankDrawMod
{
    internal static class MemoryBankDialogPresenter
    {
        private static readonly List<SuEditMemoryBankDialog> s_ownedDialogs = new List<SuEditMemoryBankDialog>();
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
        public static void EnhanceOpenDialogs()
        {
            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            for (int i = s_ownedDialogs.Count - 1; i >= 0; i--)
                if (!dialogs.Contains(s_ownedDialogs[i])) s_ownedDialogs.RemoveAt(i);
            for (int i = dialogs.Count - 1; i >= 0; i--)
            {
                if (dialogs[i].GetType() != typeof(EditMemoryBankDialog))
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
                DialogsManager.HideDialog(nativeDialog);
                DialogsManager.ShowDialog(parentWidget, drawDialog);
                s_ownedDialogs.Add(drawDialog);
            }
        }

        // Source: DialogsManager.cs:HideDialog — unloading cancels only this Mod's live editors, without invoking save callbacks.
        public static void CloseOwnedDialogs()
        {
            var errors = new List<Exception>();
            foreach (SuEditMemoryBankDialog dialog in s_ownedDialogs)
            {
                try
                {
                    if (DialogsManager.Dialogs.Contains(dialog)) DialogsManager.HideDialog(dialog);
                    dialog.Dispose();
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            s_ownedDialogs.Clear();
            if (errors.Count != 0) throw new AggregateException("[MemoryBankDrawMod] Dialog cleanup failed.", errors);
        }
    }
}
