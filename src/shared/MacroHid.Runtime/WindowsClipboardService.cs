using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

internal static class WindowsClipboardService
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static bool TrySetText(
        string text,
        CancellationToken cancellationToken,
        out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Clipboard text output is only supported on Windows.";
            return false;
        }

        if (text is null)
        {
            error = "Clipboard text cannot be null.";
            return false;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    return TrySetOpenedClipboardText(text, out error);
                }
                finally
                {
                    _ = CloseClipboard();
                }
            }

            if (attempt < 19)
            {
                cancellationToken.WaitHandle.WaitOne(15);
            }
        }

        error = new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Windows clipboard.").Message;
        return false;
    }

    private static bool TrySetOpenedClipboardText(string text, out string? error)
    {
        error = null;
        if (!EmptyClipboard())
        {
            error = new Win32Exception(Marshal.GetLastWin32Error(), "Could not clear the Windows clipboard.").Message;
            return false;
        }

        var characters = (text + '\0').ToCharArray();
        var byteCount = checked((nuint)(characters.Length * sizeof(char)));
        var memory = GlobalAlloc(GmemMoveable, byteCount);
        if (memory == nint.Zero)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate clipboard memory.").Message;
            return false;
        }

        var ownershipTransferred = false;
        try
        {
            var target = GlobalLock(memory);
            if (target == nint.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error(), "Could not lock clipboard memory.").Message;
                return false;
            }

            try
            {
                Marshal.Copy(characters, 0, target, characters.Length);
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }

            if (SetClipboardData(CfUnicodeText, memory) == nint.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error(), "Could not write text to the Windows clipboard.").Message;
                return false;
            }

            ownershipTransferred = true;
            return true;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                _ = GlobalFree(memory);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
