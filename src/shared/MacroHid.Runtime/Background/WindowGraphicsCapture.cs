using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

internal sealed class WindowGraphicsCapture : IDisposable
{
    private readonly object gate = new();
    private nint device;
    private nint context;
    private nint winrtDevice;
    private nint item;
    private nint pool;
    private nint session;
    private nint boundWindow;
    private bool disposed;

    public BackgroundClientImage? CopyClient(BackgroundWindowTarget window)
    {
        lock (gate)
        {
            if (disposed || window.Hwnd == 0 || window.ClientWidth <= 0 || window.ClientHeight <= 0)
            {
                return null;
            }

            return CopyClientCore(window);
        }
    }

    private BackgroundClientImage? CopyClientCore(BackgroundWindowTarget window)
    {

        if (boundWindow != window.Hwnd && !Start(window.Hwnd))
        {
            return null;
        }

        var frame = WaitForFrame();
        if (frame == 0)
        {
            return null;
        }

        try
        {
            nint surface = 0;
            if (Invoke(frame, 6, out surface) < 0 || surface == 0)
            {
                return null;
            }

            try
            {
                return CopySurface(surface, window);
            }
            finally
            {
                Marshal.Release(surface);
            }
        }
        finally
        {
            CloseAndRelease(frame);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ReleaseSession();
            Release(ref context);
            Release(ref device);
            Release(ref winrtDevice);
        }
    }

    private bool Start(nint hwnd)
    {
        ReleaseSession();
        if (device == 0 && !CreateDevice())
        {
            return false;
        }

        item = CreateItem(hwnd);
        if (item == 0 || Invoke(item, 7, out SizeInt32 size) < 0 || size.Width <= 0 || size.Height <= 0)
        {
            ReleaseSession();
            return false;
        }

        pool = CreatePool(winrtDevice, size);
        if (pool == 0 || Invoke(pool, item, 10, out session) < 0 || session == 0)
        {
            ReleaseSession();
            return false;
        }

        DisableCursor();
        HideBorder();
        if (Invoke(session, 6) < 0)
        {
            ReleaseSession();
            return false;
        }

        boundWindow = hwnd;
        return true;
    }

    private nint WaitForFrame()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (Invoke(pool, 7, out nint frame) >= 0 && frame != 0)
            {
                return frame;
            }

            Thread.Sleep(15);
        }

        return 0;
    }

    private BackgroundClientImage? CopySurface(nint surface, BackgroundWindowTarget window)
    {
        var accessIid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        var textureIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        if (Marshal.QueryInterface(surface, ref accessIid, out var access) < 0 || access == 0)
        {
            return null;
        }

        try
        {
            var getInterface = GetDelegate<GetInterfaceDelegate>(access, 3);
            if (getInterface(access, ref textureIid, out var texture) < 0 || texture == 0)
            {
                return null;
            }

            try
            {
                var desc = default(Texture2DDesc);
                GetDelegate<GetDescDelegate>(texture, 10)(texture, out desc);
                if (desc.Width == 0 || desc.Height == 0)
                {
                    return null;
                }

                desc.MipLevels = 1;
                desc.ArraySize = 1;
                desc.SampleCount = 1;
                desc.SampleQuality = 0;
                desc.Usage = 3;
                desc.BindFlags = 0;
                desc.CpuAccessFlags = 0x20000;
                desc.MiscFlags = 0;
                var create = GetDelegate<CreateTextureDelegate>(device, 5);
                if (create(device, ref desc, 0, out var staging) < 0 || staging == 0)
                {
                    return null;
                }

                try
                {
                    GetDelegate<CopyResourceDelegate>(context, 47)(context, staging, texture);
                    var map = GetDelegate<MapDelegate>(context, 14);
                    if (map(context, staging, 0, 1, 0, out var mapped) < 0 || mapped.Data == 0)
                    {
                        return null;
                    }

                    try
                    {
                        return ReadClient(mapped, (int)desc.Width, (int)desc.Height, window);
                    }
                    finally
                    {
                        GetDelegate<UnmapDelegate>(context, 15)(context, staging, 0);
                    }
                }
                finally
                {
                    Marshal.Release(staging);
                }
            }
            finally
            {
                Marshal.Release(texture);
            }
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    private static BackgroundClientImage ReadClient(MappedSubresource mapped, int textureWidth, int textureHeight, BackgroundWindowTarget window)
    {
        var (offsetX, offsetY) = ClientOffset(window.Hwnd);
        var pixels = new byte[window.ClientWidth * window.ClientHeight * 4];
        for (var y = 0; y < window.ClientHeight; y++)
        {
            var sourceY = offsetY + y;
            if (sourceY < 0 || sourceY >= textureHeight)
            {
                continue;
            }

            var sourceX = Math.Max(0, offsetX);
            var destinationX = sourceX - offsetX;
            var rowWidth = Math.Min(window.ClientWidth - destinationX, textureWidth - sourceX);
            if (rowWidth <= 0)
            {
                continue;
            }

            Marshal.Copy(
                mapped.Data + (sourceY * (int)mapped.RowPitch) + (sourceX * 4),
                pixels,
                ((y * window.ClientWidth) + destinationX) * 4,
                rowWidth * 4);
        }

        return new BackgroundClientImage(window.ClientWidth, window.ClientHeight, pixels);
    }

    private static (int X, int Y) ClientOffset(nint hwnd)
    {
        var origin = new WinPoint(0, 0);
        if (!ClientToScreen(hwnd, ref origin))
        {
            return (0, 0);
        }

        if (DwmGetWindowAttribute(hwnd, 9, out var bounds, Marshal.SizeOf<WinRect>()) != 0)
        {
            return (0, 0);
        }

        return (origin.X - bounds.Left, origin.Y - bounds.Top);
    }

    private void DisableCursor()
    {
        var cursorIid = new Guid("2c39ae40-7d2e-5044-804e-8b6799d4cf9e");
        if (Marshal.QueryInterface(session, ref cursorIid, out var cursorSession) < 0 || cursorSession == 0)
        {
            return;
        }

        try
        {
            _ = GetDelegate<PutIntDelegate>(cursorSession, 7)(cursorSession, 0);
        }
        finally
        {
            Marshal.Release(cursorSession);
        }
    }

    private void HideBorder()
    {
        var borderIid = new Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be");
        if (Marshal.QueryInterface(session, ref borderIid, out var borderSession) < 0 || borderSession == 0)
        {
            return;
        }

        try
        {
            _ = GetDelegate<PutIntDelegate>(borderSession, 7)(borderSession, 0);
        }
        finally
        {
            Marshal.Release(borderSession);
        }
    }

    private bool CreateDevice()
    {
        var flags = 0x20u;
        if (D3D11CreateDevice(0, 1, 0, flags, 0, 0, 7, out device, out _, out context) < 0)
        {
            if (D3D11CreateDevice(0, 5, 0, flags, 0, 0, 7, out device, out _, out context) < 0)
            {
                return false;
            }
        }

        var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        if (Marshal.QueryInterface(device, ref dxgiIid, out var dxgi) < 0 || dxgi == 0)
        {
            return false;
        }

        try
        {
            return CreateDirect3D11DeviceFromDXGIDevice(dxgi, out winrtDevice) >= 0 && winrtDevice != 0;
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    private static nint CreateItem(nint hwnd)
    {
        var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var itemIid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
        var factory = ActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", interopIid);
        if (factory == 0)
        {
            return 0;
        }

        try
        {
            var create = GetDelegate<CreateForWindowDelegate>(factory, 3);
            return create(factory, hwnd, ref itemIid, out var itemPointer) >= 0 ? itemPointer : 0;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static nint CreatePool(nint winrtDevice, SizeInt32 size)
    {
        var staticsIid = new Guid("7784056a-67aa-4d53-ae54-1088d5a8ca21");
        var factory = ActivationFactory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", staticsIid);
        if (factory == 0)
        {
            return 0;
        }

        try
        {
            var create = GetDelegate<CreatePoolDelegate>(factory, 6);
            return create(factory, winrtDevice, 87, 2, size, out var pool) >= 0 ? pool : 0;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static nint ActivationFactory(string className, Guid iid)
    {
        var name = WindowsCreateString(className, className.Length, out var hstring);
        if (name < 0)
        {
            return 0;
        }

        try
        {
            return RoGetActivationFactory(hstring, ref iid, out var factory) >= 0 ? factory : 0;
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    private void ReleaseSession()
    {
        CloseAndRelease(ref session);
        CloseAndRelease(ref pool);
        CloseAndRelease(ref item);
        boundWindow = 0;
    }

    private static void CloseAndRelease(nint pointer)
    {
        if (pointer == 0)
        {
            return;
        }

        var closable = new Guid("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
        if (Marshal.QueryInterface(pointer, ref closable, out var closePointer) >= 0 && closePointer != 0)
        {
            try
            {
                _ = GetDelegate<CloseDelegate>(closePointer, 6)(closePointer);
            }
            finally
            {
                Marshal.Release(closePointer);
            }
        }

        Marshal.Release(pointer);
    }

    private static void CloseAndRelease(ref nint pointer)
    {
        var current = pointer;
        pointer = 0;
        CloseAndRelease(current);
    }

    private static void Release(ref nint pointer)
    {
        if (pointer != 0)
        {
            Marshal.Release(pointer);
            pointer = 0;
        }
    }

    private static int Invoke(nint instance, int slot)
    {
        return GetDelegate<HresultDelegate>(instance, slot)(instance);
    }

    private static int Invoke(nint instance, int slot, out nint result)
    {
        return GetDelegate<HresultOutPtrDelegate>(instance, slot)(instance, out result);
    }

    private static int Invoke(nint instance, nint argument, int slot, out nint result)
    {
        return GetDelegate<HresultPtrOutPtrDelegate>(instance, slot)(instance, argument, out result);
    }

    private static int Invoke(nint instance, int slot, out SizeInt32 result)
    {
        return GetDelegate<GetSizeDelegate>(instance, slot)(instance, out result);
    }

    private static T GetDelegate<T>(nint instance, int slot)
        where T : Delegate
    {
        var table = Marshal.ReadIntPtr(instance);
        var method = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeInt32
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public int SampleCount;
        public int SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public nint Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;

        public WinPoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HresultDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HresultOutPtrDelegate(nint self, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HresultPtrOutPtrDelegate(nint self, nint argument, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSizeDelegate(nint self, out SizeInt32 size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(nint self, nint hwnd, ref Guid iid, out nint item);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreatePoolDelegate(nint self, nint device, int format, int buffers, SizeInt32 size, out nint pool);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutIntDelegate(nint self, int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceDelegate(nint self, ref Guid iid, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetDescDelegate(nint self, out Texture2DDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureDelegate(nint self, ref Texture2DDesc desc, nint initialData, out nint texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(nint self, nint destination, nint source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(nint self, nint resource, uint subresource, int mapType, uint mapFlags, out MappedSubresource mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(nint self, nint resource, uint subresource);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint className, ref Guid iid, out nint factory);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref WinPoint point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out WinRect rect, int size);
}
