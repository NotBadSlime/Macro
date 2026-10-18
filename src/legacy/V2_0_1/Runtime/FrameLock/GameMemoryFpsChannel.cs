using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MacroHid.Runtime;

internal interface IGameFpsMemory : IDisposable
{
    bool IsAlive { get; }
    byte[] Read(nint address, int size);
    void Write(nint address, byte[] data);
    bool IsWritable(nint address, int size);
    bool IsExecutable(nint address);
}

internal sealed class GameMemoryFpsChannel : IYsrFpsChannel
{
    private readonly IGameFpsMemory memory;
    private readonly nint address;
    private int samples, overwritten, readbackFailures;
    private long nextReport;
    internal GameMemoryFpsChannel(IGameFpsMemory memory, nint address)
    {
        this.memory = memory;
        this.address = address;
        if (!memory.IsWritable(address, 4) || Read() is < 1 or > 1000)
            throw new InvalidOperationException("帧率地址不可写或当前值无效，未修改内存。");
    }

    internal static GameMemoryFpsChannel OpenGame()
    {
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("内存锁帧需要 64 位 MacroStudio。");
        var games = Process.GetProcessesByName("YuanShen").Concat(Process.GetProcessesByName("GenshinImpact")).ToArray();
        try
        {
            if (games.Length != 1) throw new InvalidOperationException(games.Length == 0
                ? "未检测到原神游戏，请先启动游戏。" : "检测到多个游戏进程，请保留一个后再使用内存锁帧。");
            var game = games[0];
            var memory = new WindowsGameFpsMemory(game.Id);
            try
            {
                var module = game.MainModule ?? throw new InvalidOperationException("无法读取游戏主模块。");
                var location = GameFpsLocator.Locate(module.FileName);
                if (location.ImageSize != module.ModuleMemorySize) throw new InvalidOperationException("游戏文件与运行中的模块不一致，未修改内存。");
                ValidateRuntime(memory, module.BaseAddress, location);
                GameFpsDiagnostics.Log($"OPEN pid={game.Id} file={module.FileName} base=0x{module.BaseAddress:X} fpsRva=0x{location.FpsRva:X} setter={Convert.ToHexString(memory.Read(module.BaseAddress + checked((int)location.SetterBodyRva), 16))} getter={Convert.ToHexString(memory.Read(module.BaseAddress + checked((int)location.GetterBodyRva), 16))}");
                return new(memory, module.BaseAddress + checked((int)location.FpsRva));
            }
            catch { memory.Dispose(); throw; }
        }
        finally { foreach (var game in games) game.Dispose(); }
    }

    internal static void ValidateRuntime(IGameFpsMemory memory, nint moduleBase, GameFpsLocator.Location location)
    {
        // Launcher hooks may replace setter/getter entries. Require two untouched
        // call sites and either the original body or a recognized executable jump.
        bool Matches(uint rva, byte[] expected) => memory.Read(moduleBase + checked((int)rva), expected.Length).SequenceEqual(expected);
        if (!Matches(location.SetterCallRva, location.SetterCall) || !Matches(location.GetterCallRva, location.GetterCall))
            throw new InvalidOperationException("运行中的帧率调用位置与游戏文件不一致，未修改内存。");
        bool ValidBody(uint rva, byte[] expected)
        {
            nint body = moduleBase + checked((int)rva);
            if (Matches(rva, expected)) return true;
            var bytes = memory.Read(body, 6);
            nint destination;
            if (bytes[0] == 0xE9)
                destination = body + 5 + BitConverter.ToInt32(bytes, 1);
            else if (bytes[0] == 0xFF && bytes[1] == 0x25)
            {
                nint pointer = body + 6 + BitConverter.ToInt32(bytes, 2);
                destination = checked((nint)BitConverter.ToInt64(memory.Read(pointer, 8)));
            }
            else return false;
            return destination != body && memory.IsExecutable(destination);
        }
        if (!ValidBody(location.SetterBodyRva, location.SetterBody) || !ValidBody(location.GetterBodyRva, location.GetterBody))
            throw new InvalidOperationException("帧率函数存在无法识别的改写，未修改内存。请关闭其他帧率补丁后重试。");
    }

    public int Read()
    {
        if (!memory.IsAlive) throw new InvalidOperationException("游戏已退出。");
        return BitConverter.ToInt32(memory.Read(address, 4));
    }
    public void Write(int fps)
    {
        if (fps is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        if (!memory.IsAlive) throw new InvalidOperationException("游戏已退出。");
        if (!memory.IsWritable(address, 4)) throw new InvalidOperationException("帧率地址已不可写，已停止锁帧。");
        int target = fps == 0 ? 999 : fps;
        int before = Read();
        memory.Write(address, BitConverter.GetBytes(target));
        int after = Read();
        samples++;
        if (before != target) overwritten++;
        if (after != target) readbackFailures++;
        if (Environment.TickCount64 >= nextReport)
        {
            GameFpsDiagnostics.Log($"WRITE address=0x{address:X} target={target} before={before} after={after} samples={samples} differentBeforeWrite={overwritten} differentAfterWrite={readbackFailures}");
            samples = overwritten = readbackFailures = 0;
            nextReport = Environment.TickCount64 + 1000;
        }
    }
    public void Restore(int fps) { GameFpsDiagnostics.Log($"RESTORE value={fps}"); if (memory.IsAlive) Write(fps); }
    public void Dispose() => memory.Dispose();
}

internal sealed class WindowsGameFpsMemory : IGameFpsMemory
{
    private readonly SafeProcessHandle handle;
    public WindowsGameFpsMemory(int pid)
    {
        // VM operation/read/write, query information and synchronize. No remote thread rights.
        handle = OpenProcess(0x00100438, false, pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"无法打开游戏进程（PID：{pid}，OpenProcess）。{new Win32Exception(error).Message}");
        }
    }
    public bool IsAlive => WaitForSingleObject(handle, 0) == 0x102;
    public byte[] Read(nint address, int size)
    {
        var buffer = new byte[size];
        if (!ReadProcessMemory(handle, address, buffer, (nuint)size, out var read) || read != (nuint)size)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取游戏帧率内存。");
        return buffer;
    }
    public void Write(nint address, byte[] data)
    {
        if (!WriteProcessMemory(handle, address, data, (nuint)data.Length, out var written) || written != (nuint)data.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入游戏帧率内存。");
    }
    public bool IsWritable(nint address, int size)
    {
        if (VirtualQueryEx(handle, address, out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0) return false;
        long offset = address.ToInt64() - info.BaseAddress.ToInt64();
        return info.State == 0x1000 && (info.Protect & (0x100u | 0x01u)) == 0 &&
            (info.Protect & (0x04u | 0x08u | 0x40u | 0x80u)) != 0 && offset >= 0 &&
            (ulong)offset + (uint)size <= info.RegionSize;
    }
    public bool IsExecutable(nint address)
    {
        if (VirtualQueryEx(handle, address, out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0) return false;
        return info.State == 0x1000 && (info.Protect & (0x100u | 0x01u)) == 0 &&
            (info.Protect & (0x10u | 0x20u | 0x40u | 0x80u)) != 0;
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryInfo info, nuint length);
}
