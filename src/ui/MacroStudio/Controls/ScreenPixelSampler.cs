using System.Windows;
using System.Windows.Threading;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public static class ScreenPixelSampler
{
    public static bool TryCaptureForPicking(out DesktopScreenSnapshot? snapshot)
    {
        snapshot = null;
        return DesktopScreenSnapshot.TryCaptureVirtualScreen(out snapshot) && snapshot is not null;
    }

    public static bool TryReadPixel(DesktopScreenSnapshot snapshot, int x, int y, out RgbColor color) =>
        snapshot.TryGetPixel(x, y, out color);

    public static bool TryReadPixel(int x, int y, out RgbColor color)
    {
        color = new RgbColor(0, 0, 0);
        if (!TryCaptureForPicking(out var snapshot) || snapshot is null)
        {
            return false;
        }

        using (snapshot)
        {
            return snapshot.TryGetPixel(x, y, out color);
        }
    }

    public static async Task<(bool Ok, int X, int Y, RgbColor Color)> PickScreenPixelAsync(Window? owner)
    {
        var restoreState = owner?.WindowState;
        var minimizedOwner = false;
        try
        {
            if (owner is not null)
            {
                owner.WindowState = WindowState.Minimized;
                minimizedOwner = true;
                await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(150);
            }

            if (!TryCaptureForPicking(out var snapshot) || snapshot is null)
            {
                return (false, 0, 0, new RgbColor(0, 0, 0));
            }

            using (snapshot)
            {
                var picker = new ScreenCoordinatePickerWindow();
                if (DialogOwnerService.ShowDialogSafe(picker, owner) != true)
                {
                    return (false, 0, 0, new RgbColor(0, 0, 0));
                }

                if (!snapshot.TryGetPixel(picker.SelectedX, picker.SelectedY, out var color))
                {
                    return (false, picker.SelectedX, picker.SelectedY, new RgbColor(0, 0, 0));
                }

                return (true, picker.SelectedX, picker.SelectedY, color);
            }
        }
        finally
        {
            if (minimizedOwner && owner is not null && restoreState is { } state)
            {
                owner.WindowState = state;
            }
        }
    }
}
