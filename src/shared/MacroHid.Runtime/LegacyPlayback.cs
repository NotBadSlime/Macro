using System.Reflection;
using MacroHid.Core;
using MacroHid.LegacyContract;

namespace MacroHid.Runtime;

/// <summary>
/// Loads the earlier releases' playback code (legacy/&lt;Folder&gt;/MacroHid.Legacy.&lt;Folder&gt;.dll next to the app) and keeps
/// one bridge per release for the life of the process: each release's static state (warm workers, sessions) lives in its own
/// assembly, as it did in that release's own process.
/// </summary>
public static class LegacyPlaybackRuntimes
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ILegacyRuntime> Loaded = new(StringComparer.Ordinal);
    private static readonly LegacyEffectHost EffectHost = new();

    public static string RootDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "legacy");

    public static bool IsAvailable(PlaybackVersionInfo version) =>
        version.Folder is not null && File.Exists(AssemblyPath(version.Folder));

    public static ILegacyRuntime Get(PlaybackVersionInfo version)
    {
        var folder = version.Folder ?? throw new ArgumentException("The current engine is not a legacy release.", nameof(version));
        lock (Gate)
        {
            if (Loaded.TryGetValue(folder, out var known)) return known;
            var path = AssemblyPath(folder);
            if (!File.Exists(path)) throw new FileNotFoundException($"MacroHID {version.Label} 的播放引擎文件缺失：{path}", path);
            var assembly = Assembly.LoadFrom(path);
            var type = assembly.GetType("MacroHid.LegacyBridge.LegacyBridge", throwOnError: true)!;
            var runtime = (ILegacyRuntime)Activator.CreateInstance(type)!;
            runtime.SetEffectHost(EffectHost);
            Loaded[folder] = runtime;
            return runtime;
        }
    }

    private static string AssemblyPath(string folder) => Path.Combine(RootDirectory, folder, $"MacroHid.Legacy.{folder}.dll");

    /// <summary>
    /// Card types the macro (and the macros it calls) uses that <paramref name="runtime"/> can run neither itself nor through
    /// the current app. Empty when the release can play it.
    /// </summary>
    public static IReadOnlyList<string> UnsupportedCards(ILegacyRuntime runtime, IEnumerable<MacroDocument> documents)
    {
        var known = runtime.StepTypes;
        var external = runtime.ExternalStepTypes;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (var type in LegacyMacroJson.StepTypes(document))
            {
                if (type == "comment" || known.Contains(type) || external.Contains(type)) continue;
                missing.Add(type);
            }
        }
        return missing.ToArray();
    }
}

/// <summary>Turns a current document into .mcrx text an earlier release's parser reads.</summary>
public static class LegacyMacroJson
{
    /// <summary>
    /// The current serializer's text. Comment cards are dropped for releases that predate them (they are inert and the older
    /// parser would reject them); everything else is left for the release to read as it did.
    /// </summary>
    public static string For(ILegacyRuntime runtime, MacroDocument document)
    {
        if (runtime.StepTypes.Contains("comment") || !StepTypes(document).Contains("comment"))
            return McrxSerializer.Serialize(document);
        return McrxSerializer.Serialize(document with { Steps = WithoutComments(document.Steps) });
    }

    public static IReadOnlySet<string> StepTypes(MacroDocument document)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        using var json = System.Text.Json.JsonDocument.Parse(McrxSerializer.Serialize(document));
        var root = json.RootElement;
        if (root.TryGetProperty("steps", out var steps)) CollectSteps(steps, types);
        if (root.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var condition in conditions.EnumerateArray())
                if (condition.TryGetProperty("then", out var then)) CollectSteps(then, types);
        return types;
    }

    // Step arrays only: "steps" (document, repeat, parallel.branch), "then" (pixel.when, text.when) and "branches" (parallel).
    private static void CollectSteps(System.Text.Json.JsonElement array, HashSet<string> types)
    {
        if (array.ValueKind != System.Text.Json.JsonValueKind.Array) return;
        foreach (var step in array.EnumerateArray())
        {
            if (step.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (step.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String)
                types.Add(type.GetString()!);
            foreach (var name in new[] { "steps", "then", "branches" })
                if (step.TryGetProperty(name, out var nested)) CollectSteps(nested, types);
        }
    }

    private static IReadOnlyList<MacroStep> WithoutComments(IReadOnlyList<MacroStep> steps) =>
        steps.Where(step => step is not CommentStep).Select(step => step switch
        {
            RepeatStep repeat => repeat with { Steps = WithoutComments(repeat.Steps) },
            PixelWhenStep pixel => pixel with { ThenSteps = WithoutComments(pixel.ThenSteps) },
            _ => step
        }).ToArray();
}

/// <summary>Runs a card an earlier release does not know with the current implementation (ILegacyEffectHost).</summary>
internal sealed class LegacyEffectHost : ILegacyEffectHost
{
    private readonly QpcHighResolutionClock clock = new();

    public IDisposable BeginRun() => new PlaybackEffectScope();

    public void Execute(string stepJson, CancellationToken cancellationToken)
    {
        var document = McrxParser.Parse($"{{\"version\":1,\"name\":\"(legacy card)\",\"steps\":[{stepJson}]}}");
        var runner = new ManagedMacroControlFlowRunner(
            NoInputSink.Instance,
            new QpcPlaybackDelayStrategy(clock, PrecisionMode.Balanced),
            clock,
            pixelEvaluator: null,
            macroResolver: null);
        uint sequence = 1;
        var actions = 0;
        runner.Run(document, clock.GetTimestamp(), clock.Frequency, cancellationToken, noWait: false, ref sequence, ref actions);
    }

    private sealed class NoInputSink : IMacroInputSink
    {
        public static readonly NoInputSink Instance = new();
        public bool IsAvailable => true;
        public void Submit(uint sequence, InputAction action) =>
            throw new InvalidOperationException("Effect cards run for an earlier release must not send input.");
        public InputSubmissionStats? GetStats() => null;
    }
}

/// <summary>
/// An earlier release's prepared macro, carried through the current UI's "executor" slot. The current
/// <see cref="MacroPlaybackController"/> hands control to the release's own controller when it gets one of these.
/// </summary>
public sealed class LegacyPreparedExecutor : IMacroPlaybackExecutor, IDisposable
{
    private ILegacyPreparedMacro? prepared;

    public LegacyPreparedExecutor(PlaybackVersionInfo version, ILegacyPreparedMacro prepared)
    {
        Version = version;
        this.prepared = prepared;
    }

    public PlaybackVersionInfo Version { get; }

    /// <summary>
    /// The release's "new MacroPlaybackExecutor(sink, resolver); Prepare(document, options)" for <paramref name="document"/>,
    /// with called macros resolved through <paramref name="resolver"/> (the UI's frozen snapshot).
    /// </summary>
    public static LegacyPreparedExecutor Prepare(PlaybackVersionInfo version, MacroDocument document,
        Func<string, MacroDocument?> resolver, PlaybackExecutionOptions options, bool immediate)
    {
        var runtime = LegacyPlaybackRuntimes.Get(version);
        var called = new List<MacroDocument>();
        var pending = new Queue<MacroDocument>([document]);
        var seen = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance);
        while (pending.TryDequeue(out var current) && seen.Count < 4096)
        {
            if (!seen.Add(current)) continue;
            called.Add(current);
            foreach (var name in MacroCallReferenceCollector.Collect(current))
                if (resolver(name) is { } target) pending.Enqueue(target);
        }
        var unsupported = LegacyPlaybackRuntimes.UnsupportedCards(runtime, called);
        if (unsupported.Count > 0)
            throw new NotSupportedException($"MacroHID {version.Label} 没有这些卡片，无法用该版本播放：{string.Join("、", unsupported)}。请选择更新的版本。");
        var legacyOptions = new LegacyPlaybackOptions(
            options.Mode.ToString(), options.Count, options.Precision.ToString(), options.AffinityMask,
            options.BackgroundWindow, options.BackgroundProcess,
            (long)options.PressFloor.ZeroGapHold.TotalMicroseconds, (long)options.PressFloor.ModifierChordHold.TotalMicroseconds,
            immediate);
        var json = LegacyMacroJson.For(runtime, document);
        string? Resolve(string name) => resolver(name) is { } target ? LegacyMacroJson.For(runtime, target) : null;
        return new LegacyPreparedExecutor(version, runtime.Prepare(json, Resolve, legacyOptions));
    }

    internal ILegacyController TakeController()
    {
        var taken = Interlocked.Exchange(ref prepared, null) ?? throw new InvalidOperationException("This prepared macro was already used.");
        return taken.CreateController();
    }

    public Task<PlaybackRunResult> RunAsync(MacroDocument document, PlaybackExecutionOptions options, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("An earlier release's macro runs through its own controller.");

    public void Dispose() => Interlocked.Exchange(ref prepared, null)?.Dispose();
}
