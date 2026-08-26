using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class RgbColorPickerDialog : Window
{
    private bool updating;

    public RgbColorPickerDialog(Color initialColor)
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        RedSlider.ValueChanged += Slider_ValueChanged;
        GreenSlider.ValueChanged += Slider_ValueChanged;
        BlueSlider.ValueChanged += Slider_ValueChanged;
        ApplyLocalization();
        SetColor(initialColor);
    }

    public Color SelectedColor { get; private set; }

    private void ApplyLocalization()
    {
        Title = LocalizationService.Get("ChooseColor");
        ConfirmButton.Content = LocalizationService.Get("Confirm");
        CancelButton.Content = LocalizationService.Get("Cancel");
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating)
        {
            return;
        }

        SelectedColor = Color.FromRgb(
            (byte)Math.Round(RedSlider.Value),
            (byte)Math.Round(GreenSlider.Value),
            (byte)Math.Round(BlueSlider.Value));
        UpdatePreview();
    }

    private void SetColor(Color color)
    {
        updating = true;
        SelectedColor = Color.FromRgb(color.R, color.G, color.B);
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        updating = false;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        ColorPreview.Background = new SolidColorBrush(SelectedColor);
        RedValue.Text = SelectedColor.R.ToString();
        GreenValue.Text = SelectedColor.G.ToString();
        BlueValue.Text = SelectedColor.B.ToString();
        HexValueBox.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
    }

    private void ApplyHexValue()
    {
        try
        {
            if (ColorConverter.ConvertFromString(HexValueBox.Text.Trim()) is Color color)
            {
                SetColor(color);
                return;
            }
        }
        catch
        {
        }

        UpdatePreview();
    }

    private void HexValueBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyHexValue();

    private void HexValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyHexValue();
        e.Handled = true;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyHexValue();
        DialogResult = true;
    }
}
