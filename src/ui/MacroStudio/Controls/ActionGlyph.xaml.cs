using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MacroStudio.Controls;

public partial class ActionGlyph : UserControl
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(ActionGlyph));

    public ActionGlyph()
    {
        InitializeComponent();
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
