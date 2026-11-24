using System.IO;
using System.Text.Json.Nodes;
using MacroHid.Core;

internal static class SharedMacroLibraryTests
{
    public static void UsesFormalLibraryDirectory()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(roaming, "MacroHID", "MacroLibrary"), MacroLibraryStore.GetDefaultRoot());
    }

    public static void MutationsAreVisibleAcrossEditions() => InTemporaryLibrary(root =>
    {
        var formal = new MacroLibraryStore(root);
        var studio = new MacroLibraryStore(root, shareWithFormalEdition: true);
        var group = formal.CreateGroup("练习", "notepad.exe");
        var action = formal.CreateMacro("记录", "动作", groupId: group.Id);
        var directive = new ConditionalDirective(
            "find-prompt", "找到提示", -1, -1,
            new TextMatcher(ScreenRegion.FromRect(0, 0, 100, 36), "确认"),
            [new MacroCallStep(action.Id)]);
        var playback = PlaybackSettings.Default with { Mode = PlaybackMode.ToggleLoop, RunAsDaemon = true };
        var created = studio.CreateMacro(
            new MacroDocument(1, "确认提醒", playback, [], [directive], Kind: MacroKind.Condition),
            "规则", ["shared-confirmation"], group.Id);

        var formalSnapshot = formal.Load();
        Assert.Equal(2, formalSnapshot.Items.Count);
        Assert.Equal(group, formalSnapshot.Groups.Single(item => item.Id == group.Id));
        Assert.True(formalSnapshot.GroupFolders.Contains(new MacroLibraryFolder(group.Id, "规则")));
        Assert.Equal(created.Id, MacroLibraryResolver.Resolve(formalSnapshot.Items, "shared-confirmation", group.Id)?.Id);
        var reopened = formal.ReadMacro(created.Id);
        Assert.Equal(created.Id, reopened.Id);
        Assert.Equal(MacroKind.Condition, reopened.Kind);
        Assert.Equal(playback, reopened.Playback);
        Assert.Equal(action.Id, Assert.IsType<MacroCallStep>(reopened.EffectiveConditions.Single().ThenSteps.Single()).Macro);

        formal.SaveMacro(action.Id, formal.ReadMacro(action.Id) with { Name = "正式版编辑" });
        Assert.Equal("正式版编辑", studio.ReadMacro(action.Id).Name);
        var document = studio.ReadMacro(created.Id, out var revision);
        studio.SaveMacro(created.Id, document with { Name = "Studio 编辑" }, revision, out _);
        Assert.Equal("Studio 编辑", formal.ReadMacro(created.Id).Name);
        Assert.Equal(2, Directory.GetFiles(root, "*.mcrx").Length);
    });

    public static void KeepsFormalSelection() => InTemporaryLibrary(root =>
    {
        var formal = new MacroLibraryStore(root);
        var first = formal.CreateMacro("第一个宏");
        var formalSelection = formal.CreateMacro("正式版选中");
        var indexPath = Path.Combine(root, "library.json");
        var before = File.ReadAllBytes(indexPath);

        var studio = new MacroLibraryStore(root, shareWithFormalEdition: true);
        studio.SetSelected(first.Id);
        Assert.Equal(first.Id, studio.Load().SelectedMacroId);
        Assert.SequenceEqual(before, File.ReadAllBytes(indexPath));

        studio.CreateMacro("Studio 新建");
        Assert.Equal(formalSelection.Id, formal.Load().SelectedMacroId);
        studio.SetSelected(null);
        Assert.True(studio.Load().SelectedMacroId is null);
        Assert.Equal(formalSelection.Id, formal.Load().SelectedMacroId);
    });

    public static void SharedReadsDoNotRewriteLegacyIndex() => InTemporaryLibrary(root =>
    {
        var formal = new MacroLibraryStore(root);
        var item = formal.CreateMacro("旧版宏", "现有文件夹");
        formal.CreateFolder("空文件夹");
        var indexPath = Path.Combine(root, "library.json");
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
        index.Remove("Groups");
        index.Remove("GroupFolders");
        File.WriteAllText(indexPath, index.ToJsonString());
        var before = File.ReadAllBytes(indexPath);

        var studio = new MacroLibraryStore(root, shareWithFormalEdition: true);
        var snapshot = studio.Load();
        Assert.Equal(item.Id, snapshot.Items.Single().Id);
        Assert.True(snapshot.GroupFolders.Contains(new MacroLibraryFolder(MacroLibraryStore.GlobalGroupId, "空文件夹")));
        Assert.Equal(item.Name, studio.ReadMacro(item.Id, out _).Name);
        Assert.SequenceEqual(before, File.ReadAllBytes(indexPath));
    });

    public static void DetectsStaleStudioSave() => InTemporaryLibrary(root =>
    {
        var formal = new MacroLibraryStore(root);
        var studio = new MacroLibraryStore(root, shareWithFormalEdition: true);
        var item = formal.CreateMacro("原始宏");
        var original = studio.ReadMacro(item.Id, out var oldRevision);
        formal.SaveMacro(item.Id, original with { Name = "正式版编辑" });
        var indexPath = Path.Combine(root, "library.json");
        var documentPath = Path.Combine(root, item.FileName);
        var indexAfterFormalSave = File.ReadAllBytes(indexPath);
        var documentAfterFormalSave = File.ReadAllBytes(documentPath);

        Assert.Throws<MacroLibraryConflictException>(() =>
            studio.SaveMacro(item.Id, original with { Name = "过期的 Studio 编辑" }, oldRevision, out _));
        Assert.SequenceEqual(indexAfterFormalSave, File.ReadAllBytes(indexPath));
        Assert.SequenceEqual(documentAfterFormalSave, File.ReadAllBytes(documentPath));

        var refreshed = studio.ReadMacro(item.Id, out var currentRevision);
        studio.SaveMacro(item.Id, refreshed with { Name = "确认后的 Studio 编辑" }, currentRevision, out var savedRevision);
        studio.SaveMacro(item.Id, refreshed with { Name = "Studio 再次编辑" }, savedRevision, out var latestRevision);
        Assert.Equal("Studio 再次编辑", formal.ReadMacro(item.Id).Name);

        var onDiskEdit = McrxSerializer.Serialize(formal.ReadMacro(item.Id) with { Name = "单独修改了宏文件" });
        File.WriteAllText(documentPath, onDiskEdit);
        Assert.Throws<MacroLibraryConflictException>(() =>
            studio.SaveMacro(item.Id, refreshed with { Name = "再次尝试过期保存" }, latestRevision, out _));
        Assert.Equal(onDiskEdit, File.ReadAllText(documentPath));
    });

    private static void InTemporaryLibrary(Action<string> check)
    {
        var root = Directory.CreateTempSubdirectory("MacroHID-shared-library-tests-");
        try
        {
            check(root.FullName);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
