using System.Text;
using MacroHid.Core;
using MacroHid.Runtime;

internal static class UnlockerConfigTests
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Fails(Action action) { try { action(); } catch (InvalidOperationException) { return; } catch (IOException) { return; } throw new Exception("Expected failure"); }
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Macro 配置 测试 " + Guid.NewGuid().ToString("N"));
        string oldRoot = UnlockerConfiguration.Root;
        Directory.CreateDirectory(directory); UnlockerConfiguration.Root = Path.Combine(directory, "state");
        try
        {
            foreach (var encoding in new[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.Latin1 })
            {
                const string content = "; 中文说明\r\n[Settings]\r\nenableFps = 1\r\ntargetFps = 0151 ; keep\r\nother=151\r\n";
                byte[] before = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
                var ini = new FpsIni(before, FpsBackend.Ky3);
                Require(ini.Target == 151 && ini.Enabled, "parse encoding");
                byte[] expected = encoding.GetPreamble().Concat(encoding.GetBytes(content.Replace("0151 ;", "60 ;"))).ToArray();
                Require(ini.WithTarget(60).SequenceEqual(expected), "preserve bytes/BOM/comments/newlines");
            }
            Fails(() => _ = new FpsIni(Encoding.UTF8.GetBytes("[Settings]\ntargetFps=151\ntargetFps=60\nenableFps=1"), FpsBackend.Ky3).Target);
            Fails(() => _ = new FpsIni(Encoding.UTF8.GetBytes("[Settings]\ntargetFps=abc"), FpsBackend.Ky3).Target);
            Fails(() => _ = new FpsIni(Encoding.UTF8.GetBytes("[Other]\ntargetFps=151"), FpsBackend.Ky3).Target);
            string fufu = Path.Combine(directory, "config.ini");
            const string initial = "[FpsUnlock]\r\nValue = 1\r\n[TargetFps]\r\nValue = 10000\r\n[Other]\r\nValue = 7\r\n";
            File.WriteAllText(fufu, initial, new UTF8Encoding(false));
            using (var owner = new ConfigFpsChannel(fufu, FpsBackend.Fufu))
            {
                using var other = new ConfigFpsChannel(fufu, FpsBackend.Fufu);
                owner.Write(117); owner.Write(0); Require(owner.Read() == 999, "set/update");
                Fails(() => other.Write(60));
                owner.Restore(10000);
            }
            Require(File.ReadAllText(fufu) == initial, "exact restore and neighbors");
            using (var crashed = new ConfigFpsChannel(fufu, FpsBackend.Fufu)) { crashed.Write(117); crashed.Write(60); }
            Require(UnlockerConfiguration.RecoverPending().Count == 1, "recover record");
            Require(File.ReadAllText(fufu) == initial, "crash restores initial not last update");
            using (var owner = new ConfigFpsChannel(fufu, FpsBackend.Fufu))
            {
                owner.Write(117);
                File.WriteAllText(fufu, initial.Replace("10000", "90"));
                Fails(owner.Check); Fails(() => owner.Restore(10000));
            }
            Require(new FpsIni(File.ReadAllBytes(fufu), FpsBackend.Fufu).Target == 90, "external target preserved");
            Require(Directory.GetFiles(UnlockerConfiguration.Root, "*.conflict-*").Length == 1, "conflict archived");
            File.WriteAllText(fufu, initial);
            using (var crashed = new ConfigFpsChannel(fufu, FpsBackend.Fufu)) crashed.Write(117);
            File.WriteAllText(fufu, initial.Replace("10000", "90"));
            Require(UnlockerConfiguration.RecoverPending().Single().Contains("保留"), "crash external conflict");
            string ky3 = Path.Combine(directory, "dll_config.ini");
            File.WriteAllText(ky3, "[Settings]\nenableFps=1\ntargetFps=151\nother=45\n");
            using (var owner = new ConfigFpsChannel(ky3, FpsBackend.Ky3))
            {
                Fails(() => owner.Write(29)); owner.Write(60);
                DateTime stamp = File.GetLastWriteTimeUtc(ky3); owner.Write(60);
                Require(File.GetLastWriteTimeUtc(ky3) == stamp, "same target no disk write");
                File.AppendAllText(ky3, "newSetting=1\n"); owner.Restore(151);
                Require(File.ReadAllText(ky3).EndsWith("newSetting=1\n"), "preserve concurrent unrelated setting");
            }
            using (var owner = new ConfigFpsChannel(ky3, FpsBackend.Ky3))
            {
                using var acquired = new ManualResetEventSlim();
                var shortWriter = Task.Run(() =>
                {
                    using var file = new FileStream(ky3, FileMode.Open, FileAccess.Read, FileShare.None);
                    acquired.Set(); Thread.Sleep(60);
                });
                acquired.Wait(); owner.Write(60); shortWriter.GetAwaiter().GetResult();
                owner.Write(60);
                using (var locked = new FileStream(ky3, FileMode.Open, FileAccess.Read, FileShare.None)) Fails(() => owner.Restore(151));
                Require(Directory.GetFiles(UnlockerConfiguration.Root, "*.recovery.json").Length == 1, "failed restore retains recovery");
                owner.Restore(151);
            }
            GameMemoryFpsSession.ResetForTests();
            try
            {
                GameMemoryFpsSession.TestOpenChannel = () => new ConfigFpsChannel(ky3, FpsBackend.Ky3);
                using (var scope = new PlaybackEffectScope())
                {
                    Require(GameMemoryFpsSession.TryApply(new(60, YsrFpsAction.Start, FpsBackend.Ky3), out _), "real config session start");
                    using (var otherScope = new PlaybackEffectScope())
                    {
                        Require(!GameMemoryFpsSession.TryStop(out _), "scope prevents foreign restore");
                        Require(GameMemoryFpsSession.RestoreOnShutdown(out _), "shutdown restores across scopes");
                    }
                }
                Require(new FpsIni(File.ReadAllBytes(ky3), FpsBackend.Ky3).Target == 151, "shutdown actual config restore");
                using (var scope = new PlaybackEffectScope())
                    Require(GameMemoryFpsSession.TryApply(new(60, YsrFpsAction.Start, FpsBackend.Ky3), out _), "scope start");
                Require(new FpsIni(File.ReadAllBytes(ky3), FpsBackend.Ky3).Target == 151, "scope cleanup actual config restore");
                using var faulted = new ManualResetEventSlim();
                Action<FpsBackend, string> handler = (backend, _) => { if (backend == FpsBackend.Ky3) faulted.Set(); };
                UnlockerConfiguration.Faulted += handler;
                try
                {
                    using var scope = new PlaybackEffectScope();
                    Require(GameMemoryFpsSession.TryApply(new(60, YsrFpsAction.Start, FpsBackend.Ky3), out _), "monitor start");
                    ConfigFpsChannel.ReplaceTarget(ky3, FpsBackend.Ky3, 60, 90);
                    Require(faulted.Wait(TimeSpan.FromSeconds(5)), "background monitor reports conflict");
                    Require(GameMemoryFpsSession.LastError?.Contains("其他程序") == true, "monitor records conflict");
                    Require(GameMemoryFpsSession.TryStop(out _), "conflicting session released");
                    Require(new FpsIni(File.ReadAllBytes(ky3), FpsBackend.Ky3).Target == 90, "monitor never overwrites external target");
                }
                finally { UnlockerConfiguration.Faulted -= handler; }
            }
            finally { GameMemoryFpsSession.ResetForTests(); }
            string another = Path.Combine(directory, "second"); Directory.CreateDirectory(another);
            File.WriteAllText(Path.Combine(directory, UnlockerConfiguration.PluginName(FpsBackend.Ky3)), "fixture");
            File.Copy(ky3, Path.Combine(another, "dll_config.ini"));
            File.WriteAllText(Path.Combine(another, UnlockerConfiguration.PluginName(FpsBackend.Ky3)), "fixture");
            Require(UnlockerConfiguration.FindDirectories(FpsBackend.Ky3, new[] { directory, another }).Length == 2, "multiple candidates");
            Require(UnlockerConfiguration.FindDirectories(FpsBackend.Ky3, new[] { directory, directory }).Length == 1, "deduplicate");
            UnlockerConfiguration.Save(new(directory, another, true));
            Require(UnlockerConfiguration.Load() == new UnlockerSettings(directory, another, true), "local settings persistence");
            Fails(() => UnlockerConfiguration.Validate(FpsBackend.Ky3, directory));
            Fails(() => UnlockerConfiguration.Validate(FpsBackend.Fufu, directory));
            string? fixture = Environment.GetEnvironmentVariable("MACROHID_KY3_PATCH_FIXTURE");
            if (!string.IsNullOrEmpty(fixture)) PatchTests(fixture, directory);
        }
        finally
        {
            Ky3ReloadPatch.TestGameRunning = null; UnlockerConfiguration.Root = oldRoot;
            // Only the freshly created test directory is removed.
            if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(directory, true);
        }
    }
    private static void PatchTests(string fixture, string root)
    {
        byte[] original = File.ReadAllBytes(fixture), patched = Ky3ReloadPatch.Build(original);
        Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(patched)) == Ky3ReloadPatch.PatchedHash, "exact known patch");
        Require(original.Length == patched.Length, "size preserved");
        Fails(() => Ky3ReloadPatch.Build(patched));
        var altered = (byte[])original.Clone(); altered[^1] ^= 1; Fails(() => Ky3ReloadPatch.Build(altered));
        string dll = Path.Combine(root, "patch-test.dll"); File.WriteAllBytes(dll, original);
        Ky3ReloadPatch.TestGameRunning = () => true; Fails(() => Ky3ReloadPatch.Install(dll));
        Require(File.ReadAllBytes(dll).SequenceEqual(original), "game-running rejection");
        Ky3ReloadPatch.TestGameRunning = () => false;
        using (var locked = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read)) Fails(() => Ky3ReloadPatch.Install(dll));
        Require(File.ReadAllBytes(dll).SequenceEqual(original) && !File.Exists(dll + ".macro-original.bak"), "failed install unchanged");
        Ky3ReloadPatch.Install(dll); Ky3ReloadPatch.Install(dll);
        Require(Ky3ReloadPatch.Inspect(dll) == Ky3PatchState.Patched, "repeat install");
        Require(File.ReadAllBytes(dll + ".macro-original.bak").SequenceEqual(original), "backup exact");
        Ky3ReloadPatch.Restore(dll); Require(File.ReadAllBytes(dll).SequenceEqual(original), "restore DLL");
    }
}
