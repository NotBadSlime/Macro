using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio;
using MacroStudio.Controls;

internal static class SchedulingUiTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try { RunSta(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("UI scheduling audit failed", failure);
    }
    private static void RunSta()
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MacroStudio;component/Themes/LightTheme.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MacroStudio;component/Themes/SharedStyles.xaml", UriKind.Relative) });
        VerifyDependencyPreparation();
        var panel = new StepSequencePanel();
        var steps = Enumerable.Range(0, 1000).Select(i => (MacroStep)new WaitStep(TimeSpan.FromMilliseconds(i + 1))).ToList();
        panel.SetSteps(steps);
        panel.Measure(new Size(1000, 600)); panel.Arrange(new Rect(0, 0, 1000, 600)); panel.UpdateLayout();
        var list = (ListBox)panel.FindName("StepList");
        list.SelectedItems.Add(list.Items[10]); list.SelectedItems.Add(list.Items[20]);
        var firstSelected = list.Items[10]; var secondSelected = list.Items[20];
        var samples = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            steps[500] = new WaitStep(TimeSpan.FromMilliseconds(i + 500));
            var watch = Stopwatch.StartNew(); panel.SetSteps(steps); panel.UpdateLayout(); watch.Stop();
            if (i >= 5) samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        if (!list.SelectedItems.Contains(firstSelected) || !list.SelectedItems.Contains(secondSelected)) throw new Exception("Edit lost multi-selection");
        var moved = steps[10]; steps.RemoveAt(10); steps.Insert(70, moved); panel.SetSteps(steps);
        if (!ReferenceEquals(list.Items[70], firstSelected)) throw new Exception("Move recreated a stable row");
        if (!VirtualizingPanel.GetIsVirtualizing(list)) throw new Exception("Virtualization disabled");
        samples.Sort();
        Console.WriteLine($"METRIC 1000-row edit p50={samples[samples.Count/2]:F3}ms p95={samples[(int)(samples.Count*0.95)]:F3}ms p99={samples[^1]:F3}ms");
        if (samples[(int)(samples.Count*0.95)] >= 50) throw new Exception("1000-row edit P95 exceeded 50ms");
        var allocated = 0;
        for (int i=0;i<1000;i++) if (list.ItemContainerGenerator.ContainerFromIndex(i) is not null) allocated++;
        Console.WriteLine($"METRIC realized row containers={allocated}/1000");
        if (allocated >= 1000) throw new Exception("All rows realized despite virtualization");

        var sequence = new SequencePanel();
        sequence.Initialize(new MacroStudio.Services.MacroEditorState(new MacroLibraryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MacroHID-UiAudit-" + Guid.NewGuid()))));
        sequence.SetEditorDocument(new MacroDocument(1, "undo", [new WaitStep(TimeSpan.FromMilliseconds(1)), new WaitStep(TimeSpan.FromMilliseconds(2))]));
        var sequenceRows = (StepSequencePanel)sequence.FindName("StepSequenceControl");
        var sequenceList = (ListBox)sequenceRows.FindName("StepList");
        sequenceList.SelectedItems.Add(sequenceList.Items[0]); sequenceList.SelectedItems.Add(sequenceList.Items[1]);
        sequence.InsertSteps([new WaitStep(TimeSpan.FromMilliseconds(3))]);
        sequence.UndoLastChange();
        if (sequence.GetCurrentDocument().Steps.Count != 2 || sequenceList.SelectedItems.Count != 2) throw new Exception("Undo lost document or selection");
        sequence.RedoLastChange();
        if (sequence.GetCurrentDocument().Steps.Count != 3) throw new Exception("Redo lost edit");

        using var hook = new GlobalKeyboardHook();
        using var received = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        using var running = new MacroPlaybackController(new MacroDocument(1, "hotkey cancellation", []), new WaitingExecutor(received));
        running.RunNowAsync().GetAwaiter().GetResult();
        hook.TriggerPressed += (_, _) => running.Stop();
        hook.TriggerReleased += (_, _) => released.Set();
        hook.Start([new("audit", new HotkeyGesture(HidModifier.None, HidKey.F12))]);
        var dispatcher = (Dispatcher)typeof(GlobalKeyboardHook).GetField("hookDispatcher", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hook)!;
        var down = typeof(GlobalKeyboardHook).GetMethod("HandleKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var up = typeof(GlobalKeyboardHook).GetMethod("HandleKeyUp", BindingFlags.NonPublic | BindingFlags.Instance)!;
        double latency = 0;
        var probe = Task.Run(() => {
            var watch = Stopwatch.StartNew();
            dispatcher.Invoke(() => down.Invoke(hook, [0x7B, 0, false]));
            if (!received.Wait(50)) throw new Exception("Hotkey callback depended on blocked UI");
            latency = watch.Elapsed.TotalMilliseconds;
            hook.Start([new("audit", new HotkeyGesture(HidModifier.None, HidKey.F11))]);
            dispatcher.Invoke(() => up.Invoke(hook, [0x7B, 0, false]));
            if (!released.Wait(50)) throw new Exception("Changed binding lost original release");
        });
        Thread.Sleep(1000); // Deliberately block the UI's dispatcher, without generating OS input.
        probe.GetAwaiter().GetResult();
        running.StopAsync().GetAwaiter().GetResult();
        Console.WriteLine($"METRIC hotkey cancellation signal with UI blocked={latency:F3}ms");
        if (latency > 50) throw new Exception("Hotkey latency exceeded 50ms");
        Console.WriteLine("PASS UI selection, row reuse, virtualization, and independent hotkey release");
        app.Shutdown();
    }

    private sealed class WaitingExecutor(ManualResetEventSlim cancelled) : IMacroPlaybackExecutor
    {
        public async Task<PlaybackRunResult> RunAsync(MacroDocument document, PlaybackExecutionOptions options, CancellationToken token)
        {
            using var registration = token.Register(cancelled.Set);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new(PlaybackRunStatus.Completed, 0, 0, false, null);
        }
    }

    private static void VerifyDependencyPreparation()
    {
        // Exercise the actual UI preparation path without opening a window or touching the user's library.
        var store = new MacroLibraryStore(Path.Combine(Path.GetTempPath(), "MacroHID-SnapshotAudit-" + Guid.NewGuid()));
        var child = store.CreateMacro("child", steps: [new WaitStep(TimeSpan.FromMilliseconds(1))]);
        var root = McrxParser.Parse("{\"name\":\"root\",\"steps\":[{\"type\":\"macro.call\",\"macro\":\"child\"}]}");
        var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        typeof(MainWindow).GetField("libraryStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, store);
        var capture = typeof(MainWindow).GetMethod("CaptureResolver", BindingFlags.Instance | BindingFlags.NonPublic)!;
        (Func<string, MacroDocument?> Resolver, string Fingerprint) Read() =>
            ((Func<string, MacroDocument?>, string))capture.Invoke(window, [root, null])!;
        var old = Read();
        if (Read().Fingerprint != old.Fingerprint) throw new Exception("Unchanged dependency changed the fingerprint");
        store.SaveMacro(child.Id, store.ReadMacro(child.Id) with { Steps = [new WaitStep(TimeSpan.FromMilliseconds(2))] });
        var next = Read();
        if (old.Fingerprint == next.Fingerprint) throw new Exception("Dependency edit did not invalidate the plan");
        if (((WaitStep)old.Resolver("child")!.Steps[0]).Duration != TimeSpan.FromMilliseconds(1)) throw new Exception("Running snapshot changed");
        store.DeleteMacro(child.Id);
        try { Read(); throw new Exception("Missing dependency accepted"); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        if (next.Resolver("child") is null) throw new Exception("Failed update discarded the valid snapshot");
        Console.WriteLine("PASS listener dependency fingerprints, immutable snapshot, and failed preparation isolation");
    }
}
