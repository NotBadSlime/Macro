using MacroHid.Core;

namespace MacroHid.Runtime;

internal static class EffectPathWarmup
{
    private static readonly object Gate = new();
    private static readonly Dictionary<MacroDocument, Func<string, MacroDocument?>?> Armed = new();
    private static int epoch;

    public static void Arm(MacroDocument document, Func<string, MacroDocument?>? resolver)
    {
        lock (Gate)
        {
            Armed[document] = resolver;
        }

        Schedule();
    }

    public static void Disarm(MacroDocument document)
    {
        var removed = false;
        lock (Gate)
        {
            removed = Armed.Remove(document);
        }

        if (removed)
        {
            Schedule();
        }
    }

    private static void Schedule()
    {
        var ticket = Interlocked.Increment(ref epoch);
        List<KeyValuePair<MacroDocument, Func<string, MacroDocument?>?>> docs;
        lock (Gate)
        {
            docs = Armed.ToList();
        }

        if (docs.Count == 0)
        {
            WeakNetSession.ReleaseWarm();
            NetLimitSession.ReleaseWarm();
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ => Apply(ticket, docs), null);
    }

    private static void Apply(
        int ticket,
        List<KeyValuePair<MacroDocument, Func<string, MacroDocument?>?>> docs)
    {
        if (ticket != Volatile.Read(ref epoch))
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drop = false;
        var throttle = false;
        var limiter = false;
        var frame = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in docs)
        {
            Collect(pair.Key, pair.Value, names, seen, ref drop, ref throttle, ref limiter, ref frame);
        }

        if (ticket != Volatile.Read(ref epoch))
        {
            return;
        }

        try
        {
            if (drop)
            {
                WeakNetSession.KeepWarm();
            }

            if (throttle)
            {
                NetLimitSession.KeepBuiltInWarm();
            }

            if (limiter)
            {
                NetLimitSession.WarmConnection();
            }

            if (frame)
            {
                FrameLockRtss.Prepare();
            }

            if (names.Count > 0)
            {
                RefreshProcessPaths(ticket, names);
            }
        }
        catch
        {
            // 预热失败时，触发路径仍会自己打开。
        }
    }

    private static void RefreshProcessPaths(int ticket, HashSet<string> names)
    {
        while (ticket == Volatile.Read(ref epoch))
        {
            foreach (var name in names)
            {
                WeakNetProcessQuery.TryResolveRunningPath(name, out _, allowCache: false);
            }

            Thread.Sleep(30);
        }
    }

    private static void Collect(
        MacroDocument document,
        Func<string, MacroDocument?>? resolver,
        HashSet<string> names,
        HashSet<string> seenMacros,
        ref bool drop,
        ref bool throttle,
        ref bool limiter,
        ref bool frame)
    {
        Walk(document.Steps, resolver, names, seenMacros, ref drop, ref throttle, ref limiter, ref frame);
        foreach (var condition in document.EffectiveConditions)
        {
            Walk(condition.ThenSteps, resolver, names, seenMacros, ref drop, ref throttle, ref limiter, ref frame);
        }
    }

    private static void Walk(
        IReadOnlyList<MacroStep> steps,
        Func<string, MacroDocument?>? resolver,
        HashSet<string> names,
        HashSet<string> seenMacros,
        ref bool drop,
        ref bool throttle,
        ref bool limiter,
        ref bool frame)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case WeakNetStep weak:
                    drop = true;
                    AddName(names, weak.ProcessName);
                    break;
                case NetLimitStep limit when limit.Engine == NetLimitEngine.NetLimiter:
                    limiter = true;
                    AddName(names, limit.ProcessName);
                    break;
                case NetLimitStep limit:
                    throttle = true;
                    AddName(names, limit.ProcessName);
                    break;
                case FrameLockStep:
                    frame = true;
                    break;
                case RepeatStep repeat:
                    Walk(repeat.Steps, resolver, names, seenMacros, ref drop, ref throttle, ref limiter, ref frame);
                    break;
                case ParallelStep parallel:
                    Walk(parallel.Left.Steps, resolver, names, seenMacros, ref drop, ref throttle, ref limiter, ref frame);
                    Walk(parallel.Right.Steps, resolver, names, seenMacros, ref drop, ref throttle, ref limiter, ref frame);
                    break;
                case PixelWhenStep pixel:
                    Walk(pixel.ThenSteps, resolver, names, seenMacros, ref drop, ref throttle, ref limiter, ref frame);
                    break;
                case TextWhenStep text:
                    Walk(text.ThenSteps, resolver, names, seenMacros, ref drop, ref throttle, ref limiter, ref frame);
                    break;
                case MacroCallStep call when resolver is not null && seenMacros.Add(call.Macro):
                    if (resolver(call.Macro) is { } nested)
                    {
                        Collect(nested, resolver, names, seenMacros, ref drop, ref throttle, ref limiter, ref frame);
                    }

                    break;
            }
        }
    }

    private static void AddName(HashSet<string> names, string? processName)
    {
        var name = WeakNetProcessQuery.NormalizeFileName(processName);
        if (name.Length > 0)
        {
            names.Add(name);
        }
    }
}
