using System.Runtime.InteropServices;

namespace InputApiProbe;

/// <summary>A do-nothing WH_KEYBOARD_LL hook on its own message thread: the lower bound of what an active hook adds to the input path.</summary>
internal sealed class EmptyHook : IDisposable
{
    private delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);

    private readonly HookProc proc = (code, w, l) => CallNextHookEx(IntPtr.Zero, code, w, l);
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private bool installed;

    public EmptyHook()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "ApiProbe-empty-hook", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait();
        if (!installed) throw new InvalidOperationException("Could not install the empty low-level hook");
    }

    private void Run()
    {
        threadId = Native.GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(13, proc, IntPtr.Zero, 0);
        installed = hook != IntPtr.Zero;
        ready.Set();
        if (!installed) return;
        while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(hook);
    }

    public void Dispose()
    {
        Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); // WM_QUIT
        if (!thread.Join(2000)) throw new InvalidOperationException("Empty hook thread did not stop");
        GC.KeepAlive(proc);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
}
