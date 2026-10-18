using MacroHid.Core;

namespace MacroHid.Runtime;

public static class ClipboardTextSender
{
    public static IReadOnlyList<InputAction> CreatePasteActions()
    {
        return
        [
            new KeyInputAction(KeyActionKind.Down, HidKey.V, HidModifier.LeftCtrl),
            new KeyInputAction(KeyActionKind.Up, HidKey.V, HidModifier.LeftCtrl)
        ];
    }

    public static bool TrySend(
        string text,
        SendInputMacroSink sink,
        CancellationToken cancellationToken,
        out string? error)
    {
        if (!WindowsClipboardService.TrySetText(text, cancellationToken, out error))
        {
            return false;
        }

        sink.SubmitPrepared(0, PreparedInputBatch.FromActions(CreatePasteActions()));
        return true;
    }
}
