using System.Windows;
using System.Windows.Input;
using MacroStudio.Controls;

namespace MacroStudio;

public partial class MainWindow
{
    private void CommandSearchButton_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    internal void OpenCommandPalette()
    {
        if (CommandPalette.IsVisible) return;

        commandPaletteReturnFocus = Keyboard.FocusedElement;
        StudioWorkspace.IsHitTestVisible = false;
        CommandPalette.Open([
            new("\ue8e5", L("OpenMacroFile"), "file open import .mcrx 打开 開啟", () => ClickWorkspaceButton(OpenButton)),
            new("\ue792", L("SaveMacroAs"), "save file export 保存 儲存", () => ClickWorkspaceButton(SaveButton)),
            new("\ue8b7", L("WorkspacePanelLibrary"), "library database macros 宏庫 宏库", () => ShowPalettePanel("library")),
            new("\ue8fd", L("WorkspacePanelSequence"), "steps sequence editor 步骤 步驟", () => ShowPalettePanel("sequence")),
            new("\ue713", L("StudioStepProperties"), "properties inspector attributes 属性 屬性", () => ShowPalettePanel("properties")),
            new("\ue9d9", L("WorkspacePanelConditions"), "conditions triggers 条件 條件", () => SetConditionEditorTab(true)),
            new("\ue8a7", L("WorkspacePanelActions"), "actions templates 动作 動作", () => ShowPalettePanel("actions")),
            new("\ue768", L("WorkspacePanelPlayback"), "playback run hotkeys 播放 触发鍵", () => ShowPalettePanel("playback")),
            new("\ue943", L("AdvancedJson"), "source code json", () => ShowPalettePanel("json")),
            new("\ue769", L("Stop"), "stop macro 停止", OnStopPlayback),
            new("\ue916", L(listeningPaused ? "ResumeListening" : "PauseListening"),
                "listen hotkeys pause resume 监听 監聽", () => ClickWorkspaceButton(PauseListeningButton)),
            new("\ue706", L("ToggleTheme"), "theme appearance light dark 主题 主題 外观 外觀", () => ClickWorkspaceButton(ThemeToggleButton)),
            new("\ue7c3", L("GlobalCommandTitle"), "terminal cli MacroCtlUI", () => GlobalCommandSettings_Click(this, new RoutedEventArgs())),
            new("\ue9d5", L("OpenLogs"), "log diagnostics 日志 記錄", () => ClickWorkspaceButton(OpenLogsButton)),
        ], animate: !isWorkspacePreview);
    }

    private void ShowPalettePanel(string id)
    {
        if (id == "sequence") SetConditionEditorTab(false);
        ToggleWorkspacePanelVisibility(id, true);
        if (workspacePanels.TryGetValue(id, out var panel))
        {
            panel.Element.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    private static void ClickWorkspaceButton(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private void RestoreCommandPaletteFocus()
    {
        StudioWorkspace.IsHitTestVisible = true;
        if (commandPaletteReturnFocus is not null && Keyboard.Focus(commandPaletteReturnFocus) is not null)
        {
            commandPaletteReturnFocus = null;
            return;
        }

        commandPaletteReturnFocus = null;
        CommandSearchButton.Focus();
    }
}
