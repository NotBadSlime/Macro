using System.Windows;
using System.Windows.Controls;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class McrxJsonPanel : UserControl
{
    private bool updatingText;

    public event Action<string>? EditorTextChanged;
    public event Action? ApplyJsonRequested;

    public McrxJsonPanel()
    {
        InitializeComponent();
    }

    public string EditorText
    {
        get => MacroEditor.Text;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(MacroEditor.Text, normalized, StringComparison.Ordinal))
            {
                return;
            }

            updatingText = true;
            MacroEditor.Text = normalized;
            updatingText = false;
        }
    }

    public void ApplyLocalization()
    {
        JsonTitleText.Text = LocalizationService.Get("AdvancedJson");
        ApplyJsonButton.Content = LocalizationService.Get("Apply");
    }

    private void MacroEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingText)
        {
            return;
        }

        EditorTextChanged?.Invoke(MacroEditor.Text);
    }

    private void ApplyJson_Click(object sender, RoutedEventArgs e)
    {
        ApplyJsonRequested?.Invoke();
    }
}
