using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio;

internal static class Program
{
    private const int Header = 128, Stride = 128;
    private const ulong Tag = 0x4D485052;
    private static long Pos(int row, int field) => Header + (long)row * Stride + field;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--receiver")
            {
                if (args.Length > 2 && args[2] == "win32") NativeReceiver.Run(args[1]);
                else RunReceiver(args[1]);
                return 0;
            }
            return RunProbe(args).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { NativePlaybackWarmup.Shutdown(); }
    }

    private static void RunReceiver(string name)
    {
        using var memory = MemoryMappedFile.OpenExisting(name);
        using var view = memory.CreateViewAccessor();
        var text = new TextBlock { FontSize = 22, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) };
        var window = new Window { Title = "MacroHID 真实热键接收测试", Width = 800, Height = 300,
            Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = text };
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle).AddHook((IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (message == 0x100 && w.ToInt64() == 0x87) // WM_KEYDOWN / F24
                {
                    long received = Stopwatch.GetTimestamp();
                    int row = view.ReadInt32(0);
                    if (row >= 0 && row < view.ReadInt32(44))
                    {
                        long at = Pos(row, 72);
                        int previous = view.ReadInt32(at);
                        view.Write(at, previous + 1);
                        if (previous == 0)
                        {
                            view.Write(Pos(row, 48), received);
                            view.Write(4, view.ReadInt32(4) + 1);
                        }
                    }
                }
                return IntPtr.Zero;
            });
            view.Write(16, handle.ToInt64());
            view.Write(12, 1);
            Native.SetForegroundWindow(handle);
        };
        window.Closing += (_, _) => view.Write(24, 1);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        int doneTicks = 0;
        timer.Tick += (_, _) =>
        {
            int phase = view.ReadInt32(48), manual = view.ReadInt32(52), auto = view.ReadInt32(56);
            text.Text = phase switch
            {
                0 => "请点击此窗口，把它置于前台。正在预热测试，请先不要按 F8。",
                1 => $"请按下并松开 F8 共 {manual} 次，每次间隔约半秒。\n已接收 {view.ReadInt32(4)} 次。请保持此窗口为前台。",
                2 => $"物理热键测试已完成，正在自动测试 {auto} 次。\n已接收 {view.ReadInt32(4)} 次。请不要按 F8 或切换窗口。",
                3 => "测试已完成，结果已保存。此窗口即将自动关闭。",
                _ => "测试未完成，请查看记录的错误。此窗口即将关闭。"
            };
            if (view.ReadInt32(8) != 0 && ++doneTicks >= 30) window.Close();
        };
        timer.Start();
        new Application().Run(window);
    }

    private static async Task<int> RunProbe(string[] args)
    {
        string Option(string key, string fallback)
        { int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }
        int manual = int.Parse(Option("--physical", "30")), automatic = int.Parse(Option("--automatic", "2000"));
        int timeout = int.Parse(Option("--timeout", "180"));
        string output = Path.GetFullPath(Option("--output", "end-to-end.csv"));
        string receiverKind = Option("--receiver-kind", "wpf");
        if (receiverKind is not ("wpf" or "win32")) throw new ArgumentException("Unknown receiver kind");
        if (manual < 0 || automatic < 0 || manual + automatic < 1 || manual + automatic > 100000 || timeout < 1)
            throw new ArgumentException("Invalid sample counts or timeout.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string name = "Local\\MacroHID-E2E-" + Guid.NewGuid().ToString("N");
        using var memory = MemoryMappedFile.CreateNew(name, Header + (long)(manual + automatic) * Stride);
        using var view = memory.CreateViewAccessor();
        view.Write(0, -1); view.Write(44, manual + automatic); view.Write(52, manual); view.Write(56, automatic);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--receiver"); info.ArgumentList.Add(name); info.ArgumentList.Add(receiverKind);
        using var receiver = Process.Start(info) ?? throw new InvalidOperationException("Cannot start receiver");
        var document = McrxParser.Parse("{\"steps\":[{\"type\":\"key.down\",\"key\":\"F24\"},{\"type\":\"wait\",\"ms\":5},{\"type\":\"key.up\",\"key\":\"F24\"}]}");
        var options = new PlaybackExecutionOptions(PlaybackMode.FixedCount, 1, PixelEvaluationMode.Skip, false, PrecisionMode.UltraLowJitter);
        var sink = new SendInputMacroSink();
        var executor = new MacroPlaybackExecutor(sink);
        await using var controller = new MacroPlaybackController(document, executor, options);
        using var trace = new TraceRecorder(view);
        using var hook = new GlobalKeyboardHook();
        var completions = new TaskCompletionSource<PlaybackRunResult>?[manual + automatic];
        var routeGate = new object();
        int accepted = 0, automaticPhase = 0, physicalReleases = 0;
        long sourceTick = 0;
        TaskCompletionSource<int>? pending = null;
        var deadline = Stopwatch.StartNew();
        IntPtr target = IntPtr.Zero;
        void RequireTarget()
        {
            if (view.ReadInt32(24) != 0 || receiver.HasExited || Native.GetForegroundWindow() != target)
                throw new InvalidOperationException("Receiver closed or lost foreground focus; stopped before further injection.");
        }
        async Task Complete(int row, Task<PlaybackRunResult> task)
        {
            try
            {
                var result = await task.ConfigureAwait(false);
                view.Write(Pos(row, 80), result.Status == PlaybackRunStatus.Completed && !result.Cancelled ? 1 : 0);
                view.Write(Pos(row, 84), result.InputStats?.BackendName.StartsWith("native", StringComparison.Ordinal) == true ? 1 : 0);
                view.Write(Pos(row, 96), result.InputStats?.NativeSubmitDurationMicroseconds ?? 0);
                view.Write(Pos(row, 104), result.InputStats?.EngineWakeCostMicroseconds ?? 0);
                view.Write(Pos(row, 112), result.InputStats?.MaxLateMicroseconds ?? 0);
                completions[row]!.TrySetResult(result);
            }
            catch (Exception ex) { completions[row]!.TrySetException(ex); }
        }
        hook.TriggerPressed += (_, e) =>
        {
            lock (routeGate)
            {
                bool isAutomatic = Volatile.Read(ref automaticPhase) != 0;
                if (e.ProbeInjected != isAutomatic) return;
                if (target == IntPtr.Zero || Native.GetForegroundWindow() != target)
                { view.Write(40, view.ReadInt32(40) + 1); return; }
                if (accepted >= completions.Length || (!isAutomatic && accepted >= manual)) return;
                if (controller.Status is PlaybackStatus.Running or PlaybackStatus.Stopping
                    || (accepted > 0 && view.ReadInt64(Pos(accepted - 1, 48)) == 0))
                { view.Write(36, view.ReadInt32(36) + 1); return; }
                int row = accepted++;
                completions[row] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                view.Write(0, row); view.Write(32, accepted);
                view.Write(Pos(row, 0), isAutomatic ? Volatile.Read(ref sourceTick) : 0);
                view.Write(Pos(row, 8), e.CallbackQpc); view.Write(Pos(row, 76), e.ProbeInjected ? 1 : 0);
                _ = controller.TriggerPressedAsync(e.CallbackQpc);
                _ = Complete(row, controller.WhenIdleAsync());
                pending?.TrySetResult(row);
            }
        };
        hook.TriggerReleased += (_, _) =>
        {
            if (Volatile.Read(ref automaticPhase) == 0) Interlocked.Increment(ref physicalReleases);
        };
        hook.ProbeInputObserved += (key, down, tick) =>
        {
            int row = view.ReadInt32(0);
            if (key == 0x87 && down && row >= 0) view.Write(Pos(row, 88), tick);
        };
        hook.ProbeOutputForwarded += tick =>
        {
            int row = view.ReadInt32(0);
            if (row >= 0) view.Write(Pos(row, 120), tick);
        };
        async Task WaitForRow(int row)
        {
            var task = completions[row]?.Task ?? throw new InvalidOperationException("Missing triggered run");
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            var wait = Stopwatch.StartNew();
            while (view.ReadInt64(Pos(row, 48)) == 0 && wait.Elapsed.TotalSeconds < 3) await Task.Delay(1);
            if (view.ReadInt64(Pos(row, 48)) == 0 || view.ReadInt32(Pos(row, 72)) != 1
                || view.ReadInt32(Pos(row, 80)) != 1 || view.ReadInt32(Pos(row, 84)) != 1)
                throw new InvalidOperationException($"Missing, duplicate, failed or non-native sample {row}");
        }
        try
        {
            while (view.ReadInt32(12) == 0 && deadline.Elapsed.TotalSeconds < timeout) await Task.Delay(20);
            target = new IntPtr(view.ReadInt64(16));
            if (target == IntPtr.Zero) throw new InvalidOperationException("Receiver did not initialize");
            executor.Prepare(document, options);
            Console.WriteLine("WINDOW_READY: click the MacroHID receiver window; waiting for foreground.");
            while (Native.GetForegroundWindow() != target && deadline.Elapsed.TotalSeconds < timeout)
            { if (receiver.HasExited) throw new InvalidOperationException("Receiver closed"); await Task.Delay(50); }
            RequireTarget();
            // Warm the actual playback path against our foreground receiver.
            // These untriggered runs are excluded from both measured groups.
            for (int i = 0; i < 10; i++)
            { RequireTarget(); _ = controller.TriggerPressedAsync(); await controller.WhenIdleAsync(); }
            hook.Start(new HotkeyGesture(HidModifier.None, HidKey.F8));
            view.Write(48, 1);
            Console.WriteLine($"PHYSICAL_READY: press/release F8 {manual} times, about 0.5s apart; target pid={receiver.Id}, kind={receiverKind}.");
            while (Volatile.Read(ref accepted) < manual && deadline.Elapsed.TotalSeconds < timeout)
            { if (receiver.HasExited) throw new InvalidOperationException("Receiver closed"); await Task.Delay(20); }
            if (Volatile.Read(ref accepted) < manual) throw new TimeoutException("Physical sample collection timed out");
            for (int i = 0; i < manual; i++) await WaitForRow(i);
            while (Volatile.Read(ref physicalReleases) < manual && deadline.Elapsed.TotalSeconds < timeout) await Task.Delay(10);
            if (Volatile.Read(ref physicalReleases) < manual) throw new TimeoutException("Final physical hotkey is still held");
            Volatile.Write(ref automaticPhase, 1);
            hook.ProbeInjectedInputTag = new IntPtr((long)Tag);
            view.Write(48, 2);
            for (int i = 0; i < automatic; i++)
            {
                RequireTarget();
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref sourceTick, Stopwatch.GetTimestamp());
                Native.SendProbeHotkey(Tag);
                int row = await pending.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await WaitForRow(row);
                await Task.Delay(10);
            }
            view.Write(48, 3);
            Console.WriteLine($"PASS: physical={manual}, synthetic={automatic}, receiver_pid={receiver.Id}, CSV={output}");
            return 0;
        }
        catch { view.Write(48, 4); throw; }
        finally
        {
            hook.Stop();
            await controller.StopAsync();
            using var file = new StreamWriter(output, false, new UTF8Encoding(false));
            file.WriteLine("sample,source,source_qpc,hook_qpc,dispatch_qpc,worker_qpc,queued_qpc,submit_qpc,receiver_qpc,complete_qpc,frequency,run_id,deliveries,run_ok,native,output_hook_qpc,native_api_total_us,engine_wake_us,max_scheduler_late_us,output_forwarded_qpc");
            for (int i = 0; i < Volatile.Read(ref accepted); i++)
            {
                var ticks = Enumerable.Range(0, 8).Select(n => view.ReadInt64(Pos(i, n * 8))).ToArray();
                file.WriteLine($"{i},{(view.ReadInt32(Pos(i, 76)) == 0 ? "physical_manual" : "synthetic_tagged")},{string.Join(',', ticks)},{Stopwatch.Frequency},{view.ReadInt64(Pos(i, 64))},{view.ReadInt32(Pos(i, 72))},{view.ReadInt32(Pos(i, 80))},{view.ReadInt32(Pos(i, 84))},{view.ReadInt64(Pos(i, 88))},{view.ReadInt64(Pos(i, 96))},{view.ReadInt64(Pos(i, 104))},{view.ReadInt64(Pos(i, 112))},{view.ReadInt64(Pos(i, 120))}");
            }
            view.Write(8, 1);
            if (!await Task.Run(() => receiver.WaitForExit(5000))) receiver.Kill();
            Console.WriteLine($"accepted={accepted}, rejected_busy={view.ReadInt32(36)}, rejected_focus={view.ReadInt32(40)}");
        }
    }

    private sealed class TraceRecorder(MemoryMappedViewAccessor view) : EventListener
    {
        private readonly ConcurrentDictionary<long, int> rows = new();
        protected override void OnEventSourceCreated(EventSource source)
        { if (source.Name == "MacroHid-Playback") EnableEvents(source, EventLevel.Informational); }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId != 1 || args.Payload is null) return;
            long run = (long)args.Payload[0]!, tick = (long)args.Payload[2]!;
            var stage = (PlaybackTraceStage)(int)args.Payload[1]!;
            if (stage == PlaybackTraceStage.Hotkey)
            {
                int row = view.ReadInt32(0);
                if (row < 0) return;
                rows[run] = row; view.Write(Pos(row, 64), run);
            }
            if (!rows.TryGetValue(run, out int index)) return;
            int field = stage switch
            {
                PlaybackTraceStage.Dispatch => 16, PlaybackTraceStage.WorkerStart => 24,
                PlaybackTraceStage.NativeQueued => 32, PlaybackTraceStage.NativeFirstSubmit => 40,
                PlaybackTraceStage.CleanupComplete => 56, _ => -1
            };
            if (field >= 0) view.Write(Pos(index, field), tick);
        }
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [StructLayout(LayoutKind.Sequential)] private struct Keyboard
        { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit, Size = 32)] private struct Union
        { [FieldOffset(0)] public Keyboard Keyboard; }
        [StructLayout(LayoutKind.Sequential)] private struct Input
        { public uint Type; public Union Union; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        internal static void SendProbeHotkey(ulong tag)
        {
            Input[] inputs = [new() { Type = 1, Union = new() { Keyboard = new() { Key = 0x77, Extra = new UIntPtr(tag) } } },
                new() { Type = 1, Union = new() { Keyboard = new() { Key = 0x77, Flags = 2, Extra = new UIntPtr(tag) } } }];
            if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2) throw new InvalidOperationException("Hotkey SendInput failed");
        }
    }
}
