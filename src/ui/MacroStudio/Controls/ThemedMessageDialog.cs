using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroStudio.Services;

namespace MacroStudio.Controls;

/// <summary>
/// Themed replacement for <see cref="MessageBox"/>: same buttons and result values, drawn with the app's dialog chrome so
/// it follows the light/dark theme instead of popping a system-white box over a dark window.
/// </summary>
public sealed class ThemedMessageDialog : Window
{
    private MessageBoxResult result;

    private ThemedMessageDialog(string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        Title = string.IsNullOrWhiteSpace(caption) ? "MacroHID" : caption;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        result = DefaultResult(button);

        var glyph = new TextBlock
        {
            Text = IconGlyph(icon),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 22,
            Margin = new Thickness(0, 1, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = icon == MessageBoxImage.None ? Visibility.Collapsed : Visibility.Visible
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, IconBrushKey(icon));

        var text = new TextBox
        {
            // Read-only, borderless text box so the message can be selected and copied (error details, paths).
            Text = message,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 13
        };
        text.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        text.Template = PlainTextTemplate();

        var body = new DockPanel { Margin = new Thickness(0, 4, 0, 18), LastChildFill = true };
        DockPanel.SetDock(glyph, Dock.Left);
        body.Children.Add(glyph);
        body.Children.Add(text);

        var buttons = new StackPanel();
        buttons.SetResourceReference(StyleProperty, "DialogButtonRow");
        foreach (var (label, value, isDefault, isCancel) in Buttons(button))
        {
            var b = new Button { Content = label, MinWidth = 82, IsDefault = isDefault, IsCancel = isCancel };
            if (isDefault) b.SetResourceReference(StyleProperty, "PrimaryButton");
            b.Click += (_, _) => { result = value; DialogResult = value is not (MessageBoxResult.Cancel or MessageBoxResult.No); Close(); };
            buttons.Children.Add(b);
        }

        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;
        ThemedDialogChrome.Apply(this);
    }

    internal static ThemedMessageDialog Create(Window? owner, string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        var dialog = new ThemedMessageDialog(message, caption, button, icon);
        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            try { dialog.Owner = owner; } catch (InvalidOperationException) { }
        }
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog;
    }

    public static MessageBoxResult Show(Window? owner, string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        var dialog = Create(owner, message, caption, button, icon);
        dialog.ShowDialog();
        return dialog.result;
    }

    private static MessageBoxResult DefaultResult(MessageBoxButton button) => button switch
    {
        MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        MessageBoxButton.YesNo => MessageBoxResult.No,
        _ => MessageBoxResult.OK
    };

    private static IEnumerable<(string Label, MessageBoxResult Value, bool IsDefault, bool IsCancel)> Buttons(MessageBoxButton button)
    {
        string L(string key) => LocalizationService.Get(key);
        switch (button)
        {
            case MessageBoxButton.OKCancel:
                yield return (L("Cancel"), MessageBoxResult.Cancel, false, true);
                yield return (L("Confirm"), MessageBoxResult.OK, true, false);
                break;
            case MessageBoxButton.YesNo:
                yield return (L("DialogNo"), MessageBoxResult.No, false, true);
                yield return (L("DialogYes"), MessageBoxResult.Yes, true, false);
                break;
            case MessageBoxButton.YesNoCancel:
                yield return (L("Cancel"), MessageBoxResult.Cancel, false, true);
                yield return (L("DialogNo"), MessageBoxResult.No, false, false);
                yield return (L("DialogYes"), MessageBoxResult.Yes, true, false);
                break;
            default:
                yield return (L("Confirm"), MessageBoxResult.OK, true, true);
                break;
        }
    }

    private static string IconGlyph(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Error => "\uEA39",
        MessageBoxImage.Warning => "\uE7BA",
        MessageBoxImage.Question => "\uE9CE",
        _ => "\uE946"
    };

    private static string IconBrushKey(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Error => "Danger",
        MessageBoxImage.Warning => "AccentOrange",
        _ => "Accent"
    };

    private static ControlTemplate PlainTextTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(FocusableProperty, false);
        template.VisualTree = host;
        return template;
    }
}
