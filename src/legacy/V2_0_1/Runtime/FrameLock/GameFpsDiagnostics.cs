namespace MacroHid.Runtime;

internal static class GameFpsDiagnostics
{
    internal static string Describe(Exception error)
    {
        if (error is System.ComponentModel.Win32Exception windowsError)
            return $"{error.Message}（Windows 错误码：{windowsError.NativeErrorCode}）";
        return error.Message;
    }

    internal static void Log(string message)
    {
        if (message.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) AppLog.Error("FPS", message);
        else AppLog.Info("FPS", message);
    }
}
