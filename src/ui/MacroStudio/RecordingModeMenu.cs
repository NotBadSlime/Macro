using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MacroHid.Core;

namespace MacroStudio;

internal static class RecordingModeMenu
{
    public static void Show(FrameworkElement placementTarget, Action<MacroRecordingMode> onSelected)
    {
        var menu = new ContextMenu();
        Add(menu, "RecordModeReplica", MacroRecordingMode.Replica, onSelected);
        Add(menu, "RecordModeFixedDelay", MacroRecordingMode.FixedDelay, onSelected);
        Add(menu, "RecordModeNoDelay", MacroRecordingMode.NoDelay, onSelected);
        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static void Add(
        ContextMenu menu,
        string resourceKey,
        MacroRecordingMode mode,
        Action<MacroRecordingMode> onSelected)
    {
        var item = new MenuItem { Header = LocalizationService.Get(resourceKey), Tag = mode };
        item.Click += (_, _) => onSelected(mode);
        menu.Items.Add(item);
    }
}
