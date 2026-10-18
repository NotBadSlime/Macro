namespace MacroHid.Runtime;

internal sealed class NativePlaybackRunControl : IDisposable
{
    private IntPtr handle;

    private NativePlaybackRunControl(IntPtr handle)
    {
        this.handle = handle;
    }

    public IntPtr Handle => handle;

    public static bool TryCreate(out NativePlaybackRunControl? control)
    {
        control = null;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var handle = NativePlaybackInterop.MhpCreatePlaybackControl();
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            control = new NativePlaybackRunControl(handle);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    public void Pause()
    {
        if (handle != IntPtr.Zero)
        {
            NativePlaybackInterop.MhpPausePlayback(handle);
        }
    }

    public void Resume()
    {
        if (handle != IntPtr.Zero)
        {
            NativePlaybackInterop.MhpResumePlayback(handle);
        }
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (current != IntPtr.Zero)
        {
            NativePlaybackInterop.MhpDestroyPlaybackControl(current);
        }
    }
}
