using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("Fufu 帧率地址对照工具：只读内存，不修改游戏或 DLL。\n请保持同一游戏进程运行，关闭 Macro 的锁帧操作。");
string output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacroStudio", "fufu-target-probe");
Directory.CreateDirectory(output);
nint handle = 0;
try
{
    using var game = Process.GetProcessesByName("YuanShen").Concat(Process.GetProcessesByName("GenshinImpact")).Single();
    var modules = game.Modules.Cast<ProcessModule>().Select(m => new Module(m.ModuleName, m.FileName, m.BaseAddress.ToInt64(), m.ModuleMemorySize)).ToArray();
    handle = Native.OpenProcess(0x410, false, game.Id); // Query + read only; no write rights.
    if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    Console.Write("确认游戏当前稳定为 178 FPS 后，按回车记录："); Console.ReadLine();
    var found = new List<long>(); long scanned = 0, failed = 0;
    var buffer = new byte[1024 * 1024];
    for (long cursor = 0x10000; ;)
    {
        if (Native.VirtualQueryEx(handle, (nint)cursor, out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0) break;
        long start = info.BaseAddress.ToInt64();
        if (info.RegionSize > long.MaxValue || start > long.MaxValue - (long)info.RegionSize) break;
        long end = start + (long)info.RegionSize;
        if (end <= cursor) break;
        bool writable = info.State == 0x1000 && (info.Protect & 0x101) == 0 && (info.Protect & 0xCC) != 0;
        if (writable)
        {
            for (long address = start; address < end; address += buffer.Length)
            {
                int size = (int)Math.Min(buffer.Length, end - address);
                if (!Native.ReadProcessMemory(handle, (nint)address, buffer, (nuint)size, out var count)) { failed += size; continue; }
                int available = checked((int)count); scanned += available;
                for (int offset = (int)((4 - address % 4) % 4); offset + 4 <= available; offset += 4)
                    if (BitConverter.ToInt32(buffer, offset) == 178) found.Add(address + offset);
            }
        }
        cursor = end;
    }
    string identity = $"{DateTime.Now:yyyyMMdd-HHmmss}-pid{game.Id}";
    string baselinePath = Path.Combine(output, identity + "-178.json");
    File.WriteAllText(baselinePath, JsonSerializer.Serialize(new { pid = game.Id, time = DateTime.Now, scannedBytes = scanned, unreadableBytes = failed, modules, addresses = found.Select(Describe).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"已保存 178 快照：{found.Count} 个候选；已读 {scanned / 1024 / 1024} MB。\n{baselinePath}\n不要退出游戏。现在去芙芙工具箱改为 143，并确认实际帧率随之变化。");
    Console.Write("实际已稳定为 143 后，回到这里按回车："); Console.ReadLine();
    if (game.HasExited) throw new InvalidOperationException("游戏进程已退出，快照不能继续比较。");
    var matches = Filter(found, 143);
    string firstPath = Path.Combine(output, identity + "-143.json");
    File.WriteAllText(firstPath, JsonSerializer.Serialize(matches.Select(Describe).ToArray(), new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"178 → 143 同步变化的地址：{matches.Count} 个。\n现在再用芙芙工具箱改为 167，并确认实际帧率变化。");
    Console.Write("实际已稳定为 167 后，按回车："); Console.ReadLine();
    if (game.HasExited) throw new InvalidOperationException("游戏进程已退出。");
    matches = Filter(matches, 167);
    string result = Path.Combine(output, identity + "-confirmed.json");
    File.WriteAllText(result, JsonSerializer.Serialize(matches.Select(Describe).ToArray(), new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"两次同步变化的候选：{matches.Count} 个。结果：{result}\n结果仍需核对代码用途，不能直接认定为控制字段。可以在芙芙工具箱恢复原目标 178。");
    List<long> Filter(List<long> candidates, int target)
    {
        var bytes = new byte[4]; var result = new List<long>();
        foreach (var candidate in candidates)
            if (Native.ReadProcessMemory(handle, (nint)candidate, bytes, 4, out var read) && read == 4 && BitConverter.ToInt32(bytes) == target) result.Add(candidate);
        return result;
    }
    object Describe(long address)
    {
        var module = modules.FirstOrDefault(m => address >= m.Base && address - m.Base < m.Size);
        Native.VirtualQueryEx(handle, (nint)address, out var region, (nuint)Marshal.SizeOf<MemoryInfo>());
        return new { address = $"0x{address:X}", module = module?.Name, modulePath = module?.Path, rva = module == null ? null : $"0x{address - module.Base:X}", allocation = $"0x{region.AllocationBase:X}", regionType = $"0x{region.Type:X}" };
    }
}
catch (Exception ex) { Console.WriteLine("未完成：" + ex.Message); }
finally { if (handle != 0) Native.CloseHandle(handle); }
Console.WriteLine("按回车退出。"); Console.ReadLine();
record Module(string Name, string Path, long Base, int Size);
[StructLayout(LayoutKind.Sequential)]
struct MemoryInfo { public nint BaseAddress, AllocationBase; public uint AllocationProtect; public nuint RegionSize; public uint State, Protect, Type; }
static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(nint process, nint address, byte[] bytes, nuint size, out nuint read);
    [DllImport("kernel32.dll")] public static extern nuint VirtualQueryEx(nint process, nint address, out MemoryInfo info, nuint size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint handle);
}
