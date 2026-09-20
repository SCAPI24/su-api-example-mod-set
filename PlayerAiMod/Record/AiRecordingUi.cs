using Game;
using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 录制相关的游戏内界面：命名对话框与"同名覆盖"询问（P0-9）。
    ///
    /// 复用游戏现成组件（计划 §6.1）：
    ///   · `TextBoxDialog(title, text, maximumLength, handler)`：自动聚焦、回车确认、ESC 取消；
    ///   · `MessageDialog(large, small, button1, button2, handler)`：两个按钮的询问。
    /// 两者都通过 `DialogsManager.ShowDialog` 挂上去（内部 Dispatch 到游戏线程，任何线程调用都安全）。
    ///
    /// 这一层只负责"问"，不负责"存"：存盘在 <see cref="AiRecordingSession"/>（可自检的纯逻辑）。
    /// </summary>
    public static class AiRecordingUi
    {
        /// <summary>命名对话框：回调收到 null 表示取消（录制仍留在"待命名"状态，不会被丢掉）。</summary>
        public static void ShowNameDialog(string suggestedName, Action<string> onName)
        {
            try
            {
                var dialog = new TextBoxDialog("录制命名", suggestedName ?? string.Empty,
                    AiRecordingSession.MaxNameLength, onName);
                DialogsManager.ShowDialog(null, dialog);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][rec] cannot show the naming dialog: "
                    + exception.GetType().Name + ": " + exception.Message);
                onName?.Invoke(null);
            }
        }

        /// <summary>同名询问：回调 true = 覆盖，false = 保持不覆盖（用户可换名字另存）。</summary>
        public static void ShowOverwriteDialog(string fileName, Action<bool> onDecision)
        {
            try
            {
                var dialog = new MessageDialog("文件已存在", fileName + "\n要覆盖它吗？",
                    "覆盖", "取消",
                    button => onDecision?.Invoke(button == MessageDialogButton.Button1));
                DialogsManager.ShowDialog(null, dialog);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][rec] cannot show the overwrite dialog: "
                    + exception.GetType().Name + ": " + exception.Message);
                onDecision?.Invoke(false);
            }
        }

        /// <summary>一条提示（录制状态、保存结果、失败原因）。</summary>
        public static void ShowInfo(string title, string message)
        {
            try
            {
                var dialog = new MessageDialog(title ?? "AI", message ?? string.Empty, "OK", null, null);
                DialogsManager.ShowDialog(null, dialog);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi][rec] cannot show the info dialog: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }
    }
}
