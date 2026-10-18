using MacroHid.Core;
using NetLimiter.Service;

namespace MacroHid.Runtime;

public static class NetLimitSession
{
    internal const string FilterName = "MacroHID-WeakNet";
    private const ushort LocalPort = 4045;

    private static readonly object Gate = new();
    private static PacketThrottleService? throttle;
    private static NLClient? netLimiterClient;
    private static bool netLimiterActive;

    internal static Action<string, uint>? TestApply { get; set; }
    internal static Action? TestClear { get; set; }

    public static bool TryStart(string processName, long bytesPerSecond, out string? error)
    {
        return TryStart(processName, bytesPerSecond, NetLimitEngine.BuiltIn, out error);
    }

    public static bool TryStart(string processName, long bytesPerSecond, NetLimitEngine engine, out string? error)
    {
        if (!PlaybackEffectScope.TryAcquire("network", out error)) return false;
        if (WeakNetSession.IsDeniedProcess(processName))
        {
            error = $"不能对系统进程 {WeakNetProcessQuery.NormalizeFileName(processName)} 开启弱网限速。";
            return false;
        }

        var bytes = (uint)NetLimitStep.ClampBytesPerSecond(bytesPerSecond);
        if (TestApply is not null)
        {
            TestApply(WeakNetProcessQuery.NormalizeFileName(processName), bytes);
            error = null;
            return true;
        }

        if (!WeakNetProcessQuery.TryResolveRunningPath(processName, out var path))
        {
            error = $"进程 {WeakNetProcessQuery.NormalizeFileName(processName)} 没有在运行。";
            return false;
        }

        lock (Gate)
        {
            try
            {
                if (engine == NetLimitEngine.NetLimiter)
                {
                    ApplyNetLimiter(path, bytes);
                    netLimiterActive = true;
                }
                else
                {
                    NativeFiles.EnsureExtracted();
                    throttle ??= new PacketThrottleService();
                    if (!throttle.TryStart(path, bytes, out error))
                    {
                        return false;
                    }
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                return false;
            }
        }
    }

    public static void Stop()
    {
        Stop(NetLimitEngine.BuiltIn);
        Stop(NetLimitEngine.NetLimiter);
    }

    public static void Stop(NetLimitEngine engine)
    {
        if (!PlaybackEffectScope.CanRelease("network")) return;
        if (engine == NetLimitEngine.BuiltIn && TestClear is not null)
        {
            TestClear();
            return;
        }

        lock (Gate)
        {
            if (engine == NetLimitEngine.NetLimiter)
            {
                if (!netLimiterActive)
                {
                    return;
                }

                try
                {
                    ClearNetLimiter();
                }
                catch
                {
                    // 服务没开时没有可撤销的限速。
                }
                finally
                {
                    netLimiterActive = false;
                }

                return;
            }

            throttle?.Stop();
        }
    }

    public static void EndPlayback()
    {
        Stop();
    }

    public static void ResetForTests()
    {
        TestApply = null;
        TestClear = null;
    }

    private static void ApplyNetLimiter(string executablePath, uint bytesPerSecond)
    {
        var client = Open();
        if (!client.IsLimiterEnabled)
        {
            client.IsLimiterEnabled = true;
        }

        RemoveOwned(client);
        var filter = new Filter(FilterName);
        filter.Functions.Add(new FFAppIdEqual(new AppId(executablePath)));
        filter = client.AddFilter(filter);
        client.AddRule(filter.Id, new LimitRule(RuleDir.Out, bytesPerSecond) { IsEnabled = true });
    }

    private static void ClearNetLimiter()
    {
        var client = Open();
        RemoveOwned(client);
    }

    private static void RemoveOwned(NLClient client)
    {
        foreach (var filter in client.Filters
                     .Where(filter => string.Equals(filter.Name, FilterName, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            client.RemoveFilter(filter);
        }
    }

    internal static void WarmConnection()
    {
        try
        {
            Open();
        }
        catch
        {
            netLimiterClient = null;
        }
    }

    internal static void KeepBuiltInWarm()
    {
        lock (Gate)
        {
            throttle ??= new PacketThrottleService();
            throttle.KeepWarm = true;
            throttle.EnsurePump();
        }
    }

    internal static void ReleaseWarm()
    {
        lock (Gate)
        {
            if (throttle is not null)
            {
                throttle.KeepWarm = false;
                throttle.Stop();
            }
        }
    }

    private static NLClient Open()
    {
        if (netLimiterClient is not null)
        {
            return netLimiterClient;
        }

        var client = new NLClient();
        client.Connect("localhost", LocalPort, null, null);
        netLimiterClient = client;
        return client;
    }

    private static string Describe(Exception ex)
    {
        return ex switch
        {
            AdminRequiredException => "开启 NetLimiter 限速需要管理员权限，并且本机要已安装并启动 NetLimiter。",
            FailedToConnectException => "无法连接 NetLimiter。请先安装并启动 NetLimiter。",
            _ => string.IsNullOrWhiteSpace(ex.Message) ? "无法开启弱网限速。" : ex.Message
        };
    }
}
