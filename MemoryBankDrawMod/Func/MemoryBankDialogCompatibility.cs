using Engine;
using Game;
using System;
using System.Reflection;

namespace MemoryBankDrawMod
{
    internal static class MemoryBankDialogCompatibility
    {
        // Source: EditMemoryBankDialog — preserve the data and completion callback supplied by its owner.
        private static readonly FieldInfo s_memoryBankDataField =
            typeof(EditMemoryBankDialog).GetField(
                "m_memoryBankData",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo s_handlerField =
            typeof(EditMemoryBankDialog).GetField(
                "m_handler",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static void ReplaceNativeDialogs()
        {
            if (s_memoryBankDataField == null || s_handlerField == null)
            {
                return;
            }

            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            for (int i = dialogs.Count - 1; i >= 0; i--)
            {
                if (dialogs[i].GetType() != typeof(EditMemoryBankDialog))
                {
                    continue;
                }

                var nativeDialog = (EditMemoryBankDialog)dialogs[i];
                ContainerWidget parentWidget = nativeDialog.ParentWidget;
                MemoryBankData memoryBankData =
                    s_memoryBankDataField.GetValue(nativeDialog) as MemoryBankData;
                Action handler = s_handlerField.GetValue(nativeDialog) as Action;
                if (parentWidget == null || memoryBankData == null)
                {
                    continue;
                }

                var drawDialog = new SuEditMemoryBankDialog(memoryBankData, handler);
                DialogsManager.HideDialog(nativeDialog);
                DialogsManager.ShowDialog(parentWidget, drawDialog);
            }
        }
    }
}
