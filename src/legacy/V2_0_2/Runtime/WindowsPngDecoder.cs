using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal static class WindowsPngDecoder
{
    private static readonly Lazy<IntPtr> Startup = new(() =>
    {
        var input = new StartupInput { Version = 1 };
        Check(GdiplusStartup(out var token, ref input, IntPtr.Zero));
        return token;
    });
    internal static (int Width, int Height, RgbColor[] Pixels) Decode(byte[] data)
    {
        _ = Startup.Value;
        Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream));
        IntPtr image = IntPtr.Zero;
        var locked = false;
        var bits = new BitmapData();
        try
        {
            stream.Write(data, data.Length, IntPtr.Zero);
            stream.Seek(0, 0, IntPtr.Zero);
            Check(GdipCreateBitmapFromStream(stream, out image));
            Check(GdipGetImageWidth(image, out var width));
            Check(GdipGetImageHeight(image, out var height));
            if (width == 0 || height == 0 || (long)width * height > 16_777_216)
                throw new InvalidOperationException("模板图片尺寸无效或超过 1600 万像素。");
            var rect = new Rect { Width = (int)width, Height = (int)height };
            Check(GdipBitmapLockBits(image, ref rect, 1, 0x26200A, ref bits));
            locked = true;
            var pixels = new RgbColor[checked((int)(width * height))];
            var row = new byte[checked((int)width * 4)];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(bits.Scan0, checked(y * bits.Stride)), row, 0, row.Length);
                for (var x = 0; x < width; x++)
                    pixels[y * (int)width + x] = new RgbColor(row[x * 4 + 2], row[x * 4 + 1], row[x * 4]);
            }
            return ((int)width, (int)height, pixels);
        }
        finally
        {
            if (locked) GdipBitmapUnlockBits(image, ref bits);
            if (image != IntPtr.Zero) GdipDisposeImage(image);
            Marshal.ReleaseComObject(stream);
        }
    }
    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"PNG 解码失败（GDI+ {status}）。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInput { public uint Version; public IntPtr Callback; public int SuppressThread; public int SuppressCodecs; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapData { public uint Width, Height; public int Stride, Format; public IntPtr Scan0; public UIntPtr Reserved; }
    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
    [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr memory, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromStream(IStream stream, out IntPtr image);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageWidth(IntPtr image, out uint width);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageHeight(IntPtr image, out uint height);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapLockBits(IntPtr image, ref Rect rect, uint flags, int format, ref BitmapData bits);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapUnlockBits(IntPtr image, ref BitmapData bits);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(IntPtr image);
}
