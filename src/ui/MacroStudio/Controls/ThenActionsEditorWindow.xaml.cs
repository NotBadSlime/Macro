using System.Windows;
using System.Windows.Controls;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class ThenActionsEditorWindow : Window
{
    private List<MacroStep> steps;

    public ThenActionsEditorWindow(IReadOnlyList<MacroStep> initialSteps, MacroEditorState? editorState = null)
    {
        InitializeComponent();
        steps = initialSteps.ToList();
        if (editorState is not null)
        {
            StepEditorPanel.Initialize(editorState);
        }

        StepEditorPanel.ApplyLocalization();
        StepEditorPanel.StepEdited += OnStepEdited;
        RefreshList();
    }

    public IReadOnlyList<MacroStep> EditedSteps => steps.ToList();

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || !Enum.TryParse<MacroActionTemplateKind>(tag, out var kind))
        {
            return;
        }

        var created = MacroActionTemplateFactory.CreateSteps(kind);
        var target = GetInsertionTarget();
        steps = MacroStepTreeEditor.InsertAtPath(steps, target.ParentPath, target.InsertIndex, created).ToList();
        RefreshList(target.ParentPath.Concat([target.InsertIndex]).ToArray());
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(-1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(1);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedPath();
        if (path.Count == 0) return;

        var step = MacroStepTreeEditor.GetAtPath(steps, path);
        var parentPath = path.Take(path.Count - 1).ToArray();
        var insertAt = path[^1] + 1;
        steps = MacroStepTreeEditor.InsertAtPath(steps, parentPath, insertAt, [step]).ToList();
        RefreshList(parentPath.Concat([insertAt]).ToArray());
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedPath();
        if (path.Count == 0) return;

        var parentPath = path.Take(path.Count - 1).ToArray();
        var deletedIndex = path[^1];
        steps = MacroStepTreeEditor.DeleteAtPath(steps, path).ToList();
        var childCount = GetChildCount(steps, parentPath);
        var nextPath = childCount == 0 ? parentPath : parentPath.Concat([Math.Min(deletedIndex, childCount - 1)]).ToArray();
        RefreshList(nextPath.Length == parentPath.Length ? null : nextPath);
    }

    private void ThenStepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var path = GetSelectedPath();
        if (path.Count == 0)
        {
            StepEditorPanel.ShowStep(null);
            SelectionHintText.Text = "选择一个动作后可修改属性。";
            return;
        }

        try
        {
            var current = MacroStepTreeEditor.GetAtPath(steps, path);
            StepEditorPanel.RefreshMacroTargetBox(current is MacroCallStep macro ? macro.Macro : null);
            StepEditorPanel.ShowStep(current);
            SelectionHintText.Text = $"正在编辑步骤 {string.Join(".", path.Select(i => i + 1))}";
        }
        catch (Exception ex)
        {
            StepEditorPanel.ShowStep(null);
            SelectionHintText.Text = ex.Message;
        }
    }

    private void OnStepEdited(MacroStep _)
    {
        ApplyEditedStep();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ApplyEditedStep()
    {
        var path = GetSelectedPath();
        if (path.Count == 0) return;

        try
        {
            var current = MacroStepTreeEditor.GetAtPath(steps, path);
            var edited = StepEditorPanel.BuildEditedStep(current);
            var replacement = MacroStepNormalizer.NormalizeSteps([edited]);
            if (replacement.Count == 0) return;

            var parentPath = path.Take(path.Count - 1).ToArray();
            var insertAt = path[^1];
            steps = replacement.Count == 1
                ? MacroStepTreeEditor.ReplaceAtPath(steps, path, replacement[0]).ToList()
                : MacroStepTreeEditor.InsertAtPath(MacroStepTreeEditor.DeleteAtPath(steps, path), parentPath, insertAt, replacement).ToList();

            RefreshList(parentPath.Concat([insertAt]).ToArray());
        }
        catch (Exception ex)
        {
            SelectionHintText.Text = ex.Message;
        }
    }

    private void MoveSelected(int offset)
    {
        var path = GetSelectedPath();
        if (path.Count == 0 || offset == 0) return;

        var parentPath = path.Take(path.Count - 1).ToArray();
        var childIndex = path[^1];
        var childCount = GetChildCount(steps, parentPath);
        if (offset < 0 && childIndex <= 0) return;
        if (offset > 0 && childIndex >= childCount - 1) return;

        var insertIndex = offset < 0 ? childIndex - 1 : childIndex + 2;
        steps = MacroStepTreeEditor.MoveAtPath(steps, path, parentPath, insertIndex).ToList();
        RefreshList(parentPath.Concat([childIndex + offset]).ToArray());
    }

    private StepInsertTarget GetInsertionTarget()
    {
        var path = GetSelectedPath();
        if (path.Count == 0)
        {
            return new StepInsertTarget([], steps.Count);
        }

        return new StepInsertTarget(path.Take(path.Count - 1).ToArray(), path[^1] + 1);
    }

    private IReadOnlyList<int> GetSelectedPath()
    {
        return ThenStepList.SelectedItem is StepDisplayItem { StepPath.Count: > 0 } item
            ? item.StepPath
            : [];
    }

    private void RefreshList(IReadOnlyList<int>? selectPath = null)
    {
        var items = StepDisplayItem.FlattenSteps(steps);
        ThenStepList.ItemsSource = items;
        EmptyThenActionsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (selectPath is not null)
        {
            var selected = items.FirstOrDefault(item => PathsEqual(item.StepPath, selectPath));
            if (selected is not null)
            {
                ThenStepList.SelectedItem = selected;
                return;
            }
        }

        if (items.Count == 0)
        {
            StepEditorPanel.ShowStep(null);
        }
    }

    private static int GetChildCount(IReadOnlyList<MacroStep> rootSteps, IReadOnlyList<int> parentPath)
    {
        if (parentPath.Count == 0)
        {
            return rootSteps.Count;
        }

        return MacroStepTreeEditor.GetAtPath(rootSteps, parentPath) switch
        {
            RepeatStep repeat => repeat.Steps.Count,
            PixelWhenStep pixel => pixel.ThenSteps.Count,
            _ => 0
        };
    }

    private static bool PathsEqual(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        return left.Count == right.Count && !left.Where((value, index) => right[index] != value).Any();
    }

    private sealed record StepInsertTarget(IReadOnlyList<int> ParentPath, int InsertIndex);
}
