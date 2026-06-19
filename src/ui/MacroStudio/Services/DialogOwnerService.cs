using System.Windows;
using Microsoft.Win32;

namespace MacroStudio.Services;

public static class DialogOwnerService
{
    public static Window? TryGetShownOwner(DependencyObject? source)
    {
        var owner = source is Window window ? window : source is not null ? Window.GetWindow(source) : Application.Current?.MainWindow;
        return IsShown(owner) ? owner : null;
    }

    public static void AssignOwnerIfShown(Window dialog, DependencyObject? source)
    {
        if (dialog.Owner is not null)
            return;

        var owner = TryGetShownOwner(source);
        if (owner is null || ReferenceEquals(owner, dialog))
            return;

        try
        {
            dialog.Owner = owner;
        }
        catch (InvalidOperationException)
        {
            // A dialog can be constructed before its intended owner has entered a valid shown state.
            // In that case showing it unowned is safer than surfacing a UI error dialog.
        }
    }

    public static bool? ShowDialogSafe(CommonDialog dialog, DependencyObject? source)
    {
        var owner = TryGetShownOwner(source);
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    public static bool? ShowDialogSafe(Window dialog, DependencyObject? source)
    {
        AssignOwnerIfShown(dialog, source);
        return dialog.ShowDialog();
    }

    public static MessageBoxResult MessageBoxSafe(
        DependencyObject? source,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        var owner = TryGetShownOwner(source);
        return owner is null
            ? MessageBox.Show(messageBoxText, caption, button, icon)
            : MessageBox.Show(owner, messageBoxText, caption, button, icon);
    }

    private static bool IsShown(Window? owner)
    {
        return owner is { IsLoaded: true, IsVisible: true };
    }
}
