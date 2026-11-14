using System.Text.Json;
using System.IO;
using MacroHid.Core;
using MacroHid.Converter;

internal static class GIMacrosReleaseTests
{
    internal static void Run()
    {
        var original = MacroConversionService.ImportToMcrx(new MacroImportRequest(
            """[["kd","ctrl",10],["kd","f2",10],["kd","ctrl",0],["ku","f2",648]]""", "chord.json"));
        var keys = original.Document.Steps.OfType<KeyStep>().ToArray();
        if (keys[0].Kind != KeyActionKind.Down || keys[2].Kind != KeyActionKind.Down
            || !original.Diagnostics.Any(d => d.Code == "gimacros.repeatedKeyDown"))
            throw new Exception("An invalid repeated down must be diagnosed, not silently rewritten.");
        var corrected = MacroConversionService.ImportToMcrx(new MacroImportRequest(
            """[["kd","ctrl",10],["kd","f2",10],["ku","ctrl",0],["ku","f2",648]]""", "chord-fixed.json"));
        keys = corrected.Document.Steps.OfType<KeyStep>().ToArray();
        if (keys[0].Kind != KeyActionKind.Down || keys[2].Kind != KeyActionKind.Up
            || corrected.Diagnostics.Any(d => d.Code == "gimacros.repeatedKeyDown"))
            throw new Exception("Explicit releases did not import correctly.");
        if (corrected.Document.Steps.OfType<WaitStep>().Sum(s => s.Duration.TotalMilliseconds) != 668)
            throw new Exception("Import changed chord timing.");
    }

    internal static void VerifyFile(string sourcePath)
    {
        var source = File.ReadAllText(sourcePath);
        var result = MacroConversionService.ImportToMcrx(new MacroImportRequest(source, sourcePath));
        using var json = JsonDocument.Parse(source);
        var sourceKeys = json.RootElement.EnumerateArray().Where(e => e[0].GetString() is "kd" or "ku").ToArray();
        var importedKeys = result.Document.Steps.OfType<KeyStep>().ToArray();
        if (sourceKeys.Length != importedKeys.Length) throw new Exception("Key event count changed.");
        for (var index = 0; index < sourceKeys.Length; index++)
        {
            var expected = sourceKeys[index][0].GetString() == "kd" ? KeyActionKind.Down : KeyActionKind.Up;
            if (importedKeys[index].Kind != expected) throw new Exception($"Key event {index + 1} changed direction.");
        }
        if (Math.Abs(json.RootElement.EnumerateArray().Sum(e => e[2].GetDouble())
            - result.Document.Steps.OfType<WaitStep>().Sum(w => w.Duration.TotalMilliseconds)) > 0.0001)
            throw new Exception("Macro timing changed.");
        var output = Path.ChangeExtension(sourcePath, ".mcrx");
        File.WriteAllText(output, McrxSerializer.Serialize(result.Document));
        Console.WriteLine($"PASS {Path.GetFileName(sourcePath)}: {importedKeys.Length} key events, " +
            $"{importedKeys.Count(k => k.Key == HidKey.LeftControl && k.Kind == KeyActionKind.Down)} Ctrl down, " +
            $"{importedKeys.Count(k => k.Key == HidKey.LeftControl && k.Kind == KeyActionKind.Up)} Ctrl up");
        foreach (var diagnostic in result.Diagnostics) Console.WriteLine(diagnostic.Message);
        Console.WriteLine("Output: " + Path.GetFullPath(output));
    }
}
