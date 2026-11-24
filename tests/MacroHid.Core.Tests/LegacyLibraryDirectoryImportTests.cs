using System.IO;
using System.Text.Json.Nodes;
using MacroHid.Core;

internal static class LegacyLibraryDirectoryImportTests
{
    public static void PreservesLibraryAndStudioEdits()
    {
        var root = Directory.CreateTempSubdirectory("MacroHID-library-tests-");
        try
        {
            var legacyPath = Path.Combine(root.FullName, "Legacy");
            var studioPath = Path.Combine(root.FullName, "Studio");
            var legacy = new MacroLibraryStore(legacyPath);
            var studio = new MacroLibraryStore(studioPath);
            var group = legacy.CreateGroup("练习", "notepad.exe");
            legacy.CreateFolder("空文件夹", group.Id);
            var action = legacy.CreateMacro(
                new MacroDocument(1, "记录", [new WaitStep(TimeSpan.FromMilliseconds(15))]),
                "步骤", ["shared-action"], group.Id);
            var gate = new ConditionalDirective(
                "find-prompt", "找到提示", -1, -1,
                new TextMatcher(ScreenRegion.FromRect(0, 0, 100, 36), "确认"),
                [new MacroCallStep(action.Id)]);
            var playback = PlaybackSettings.Default with { Mode = PlaybackMode.ToggleLoop, RunAsDaemon = true };
            var condition = legacy.CreateMacro(
                new MacroDocument(1, "确认提醒", playback, [], [gate], Kind: MacroKind.Condition),
                "规则", groupId: group.Id);
            condition = legacy.SetMacroLocked(condition.Id, true);
            var studioOriginal = studio.CreateMacro("Studio 示例");
            var sourceIndex = Path.Combine(legacyPath, "library.json");
            var originalIndex = File.ReadAllBytes(sourceIndex);

            var imported = studio.ImportLegacyLibraryIfNeeded(legacyPath) ?? throw new InvalidOperationException("Expected automatic import.");

            Assert.Equal(2, imported.ImportedCount);
            Assert.Equal(0, imported.SkippedCount);
            Assert.Equal(1, imported.ImportedGroupCount);
            Assert.Equal(3, imported.ImportedFolderCount);
            var snapshot = studio.Load();
            Assert.Equal(studioOriginal.Id, snapshot.SelectedMacroId);
            Assert.Equal(3, snapshot.Items.Count);
            Assert.Equal(group, snapshot.Groups.Single(item => item.Id == group.Id));
            Assert.True(snapshot.GroupFolders.Contains(new MacroLibraryFolder(group.Id, "空文件夹")));
            Assert.Equal(action.Id, MacroLibraryResolver.Resolve(snapshot.Items, "shared-action", group.Id)?.Id);
            var copiedAction = snapshot.Items.Single(item => item.Id == action.Id);
            Assert.Equal(action.Folder, copiedAction.Folder);
            Assert.Equal(action.GroupId, copiedAction.GroupId);
            Assert.Equal(action.UpdatedAt, copiedAction.UpdatedAt);
            Assert.True(snapshot.Items.Single(item => item.Id == condition.Id).IsLocked);
            var copiedCondition = studio.ReadMacro(condition.Id);
            Assert.Equal(MacroKind.Condition, copiedCondition.Kind);
            Assert.Equal(condition.Id, copiedCondition.Id);
            Assert.Equal(playback, copiedCondition.Playback);
            Assert.Equal(action.Id, Assert.IsType<MacroCallStep>(copiedCondition.EffectiveConditions.Single().ThenSteps.Single()).Macro);
            var sourceFile = Path.Combine(legacyPath, condition.FileName);
            var targetFile = Path.Combine(studioPath, condition.FileName);
            Assert.SequenceEqual(File.ReadAllBytes(sourceFile), File.ReadAllBytes(targetFile));

            studio.SaveMacro(action.Id, studio.ReadMacro(action.Id) with { Name = "Studio 修改过的动作" });
            studio.UpdateGroup(group.Id, "Studio 修改过的分组", "calc.exe");
            Assert.True(studio.ImportLegacyLibraryIfNeeded(legacyPath) is null);
            Assert.Equal("Studio 修改过的动作", studio.ReadMacro(action.Id).Name);
            Assert.Equal("calc.exe", studio.Load().Groups.Single(item => item.Id == group.Id).ProcessFilter);
            Assert.Equal("记录", legacy.ReadMacro(action.Id).Name);
            Assert.SequenceEqual(originalIndex, File.ReadAllBytes(sourceIndex));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    public static void KeepsExistingImportedCopies()
    {
        var root = Directory.CreateTempSubdirectory("MacroHID-library-tests-");
        try
        {
            var legacyPath = Path.Combine(root.FullName, "Legacy");
            var studioPath = Path.Combine(root.FullName, "Studio");
            var legacy = new MacroLibraryStore(legacyPath);
            var group = legacy.CreateGroup("练习", "notepad.exe");
            var older = legacy.CreateMacro("旧版宏", groupId: group.Id);
            Directory.CreateDirectory(studioPath);
            foreach (var file in Directory.GetFiles(legacyPath))
            {
                File.Copy(file, Path.Combine(studioPath, Path.GetFileName(file)));
            }

            var studio = new MacroLibraryStore(studioPath);
            studio.RenameMacro(older.Id, "本地编辑过的旧版宏");
            studio.UpdateGroup(group.Id, "本地分组", "calc.exe");
            var first = studio.ImportLegacyLibraryIfNeeded(legacyPath) ?? throw new InvalidOperationException("Expected automatic import.");

            Assert.Equal(0, first.ImportedCount);
            Assert.Equal(1, first.SkippedCount);
            Assert.Equal("本地编辑过的旧版宏", studio.ReadMacro(older.Id).Name);
            Assert.Equal("calc.exe", studio.Load().Groups.Single(item => item.Id == group.Id).ProcessFilter);
            studio.DeleteMacro(older.Id);
            Assert.True(new MacroLibraryStore(studioPath).ImportLegacyLibraryIfNeeded(legacyPath) is null);
            Assert.Equal("旧版宏", legacy.ReadMacro(older.Id).Name);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    public static void AutomaticImportRunsOnce()
    {
        var root = Directory.CreateTempSubdirectory("MacroHID-library-tests-");
        try
        {
            var legacyPath = Path.Combine(root.FullName, "Legacy");
            var studioPath = Path.Combine(root.FullName, "Studio");
            var legacy = new MacroLibraryStore(legacyPath);
            var studio = new MacroLibraryStore(studioPath);
            var studioOriginal = studio.CreateMacro("Studio 独有宏");

            Assert.True(studio.ImportLegacyLibraryIfNeeded(legacyPath) is null);
            Directory.CreateDirectory(legacyPath);
            var cataloguePath = Path.Combine(legacyPath, "library.json");
            File.WriteAllText(cataloguePath, "{\"Items\":[]}");
            Assert.True(studio.ImportLegacyLibraryIfNeeded(legacyPath) is null);
            var older = legacy.CreateMacro("旧版宏", folder: "旧分组文件夹");
            legacy.CreateFolder("旧空文件夹");
            var oldestCatalogue = JsonNode.Parse(File.ReadAllText(cataloguePath))!.AsObject();
            oldestCatalogue.Remove("Groups");
            oldestCatalogue.Remove("GroupFolders");
            File.WriteAllText(cataloguePath, oldestCatalogue.ToJsonString());
            var oldCatalogueBytes = File.ReadAllBytes(cataloguePath);
            var first = studio.ImportLegacyLibraryIfNeeded(legacyPath) ?? throw new InvalidOperationException("Expected automatic import.");
            Assert.Equal(1, first.ImportedCount);
            Assert.SequenceEqual(oldCatalogueBytes, File.ReadAllBytes(cataloguePath));
            Assert.Equal(studioOriginal.Id, studio.Load().SelectedMacroId);
            Assert.Equal(2, studio.Load().Items.Count);
            Assert.True(studio.Load().GroupFolders.Contains(new MacroLibraryFolder(MacroLibraryStore.GlobalGroupId, "旧空文件夹")));

            studio.DeleteMacro(older.Id);
            var addedLater = legacy.CreateMacro("后来保存到旧版的宏");
            var reopened = new MacroLibraryStore(studioPath);
            Assert.True(reopened.ImportLegacyLibraryIfNeeded(legacyPath) is null);
            Assert.True(reopened.TryGetItem(older.Id) is null);
            Assert.True(reopened.TryGetItem(addedLater.Id) is null);
            Assert.Equal(studioOriginal.Id, reopened.Load().Items.Single().Id);
            Assert.Equal(2, legacy.Load().Items.Count);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    public static void RejectsInvalidSourceWithoutChanges()
    {
        var root = Directory.CreateTempSubdirectory("MacroHID-library-tests-");
        try
        {
            var legacyPath = Path.Combine(root.FullName, "Legacy");
            var studioPath = Path.Combine(root.FullName, "Studio");
            var legacy = new MacroLibraryStore(legacyPath);
            var studio = new MacroLibraryStore(studioPath);
            legacy.CreateMacro("可导入");
            var unsafeItem = legacy.CreateMacro("路径不合法");
            studio.CreateMacro("Studio 示例");
            var studioIndex = Path.Combine(studioPath, "library.json");
            var originalIndex = File.ReadAllBytes(studioIndex);
            var originalFiles = Directory.GetFiles(studioPath).Order().ToArray();
            var sourceIndex = Path.Combine(legacyPath, "library.json");
            var sourceCatalogue = JsonNode.Parse(File.ReadAllText(sourceIndex))!;
            var indexEntry = sourceCatalogue["Items"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == unsafeItem.Id)!;
            var outsideFile = Path.Combine(root.FullName, "Outside.mcrx");
            File.Copy(Path.Combine(legacyPath, unsafeItem.FileName), outsideFile);
            indexEntry["FileName"] = "../Outside.mcrx";
            File.WriteAllText(sourceIndex, sourceCatalogue.ToJsonString());
            var sourceBefore = File.ReadAllBytes(sourceIndex);

            Assert.Throws<InvalidDataException>(() => studio.ImportLegacyLibraryIfNeeded(legacyPath));
            Assert.Throws<InvalidDataException>(() => studio.ImportLegacyLibraryIfNeeded(studioPath));
            Assert.True(studio.ImportLegacyLibraryIfNeeded(root.FullName) is null);
            Assert.SequenceEqual(sourceBefore, File.ReadAllBytes(sourceIndex));
            Assert.SequenceEqual(originalIndex, File.ReadAllBytes(studioIndex));
            Assert.True(originalFiles.SequenceEqual(Directory.GetFiles(studioPath).Order()));

            indexEntry["FileName"] = unsafeItem.FileName;
            File.WriteAllText(sourceIndex, sourceCatalogue.ToJsonString());
            File.WriteAllText(Path.Combine(legacyPath, unsafeItem.FileName), "{ malformed JSON");
            Assert.Throws<InvalidDataException>(() => studio.ImportLegacyLibraryIfNeeded(legacyPath));
            Assert.SequenceEqual(originalIndex, File.ReadAllBytes(studioIndex));
            Assert.True(originalFiles.SequenceEqual(Directory.GetFiles(studioPath).Order()));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    public static void HandlesFileNameCollisions()
    {
        var root = Directory.CreateTempSubdirectory("MacroHID-library-tests-");
        try
        {
            var legacyPath = Path.Combine(root.FullName, "Legacy");
            var studioPath = Path.Combine(root.FullName, "Studio");
            var legacy = new MacroLibraryStore(legacyPath);
            var studio = new MacroLibraryStore(studioPath);
            var importedItem = legacy.CreateMacro("旧版宏");
            var studioItem = studio.CreateMacro("Studio 原有宏");
            var studioFile = Path.Combine(studioPath, studioItem.FileName);
            var originalStudioFile = File.ReadAllBytes(studioFile);
            var sourceIndex = Path.Combine(legacyPath, "library.json");
            var sourceCatalogue = JsonNode.Parse(File.ReadAllText(sourceIndex))!;
            sourceCatalogue["Items"]![0]!["FileName"] = studioItem.FileName;
            File.WriteAllText(sourceIndex, sourceCatalogue.ToJsonString());
            File.Move(Path.Combine(legacyPath, importedItem.FileName), Path.Combine(legacyPath, studioItem.FileName));

            var result = studio.ImportLegacyLibraryIfNeeded(legacyPath) ?? throw new InvalidOperationException("Expected automatic import.");

            Assert.Equal(1, result.ImportedCount);
            Assert.Equal("旧版宏", studio.ReadMacro(importedItem.Id).Name);
            Assert.False(studio.TryGetItem(importedItem.Id)?.FileName == studioItem.FileName);
            Assert.SequenceEqual(originalStudioFile, File.ReadAllBytes(studioFile));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
