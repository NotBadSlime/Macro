using System.Windows;

namespace MacroStudio.Controls;

/// <summary>Attached validation state for inputs. The shared TextBox template draws a Danger border while it is true.</summary>
public static class FieldState
{
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(FieldState), new FrameworkPropertyMetadata(false));

    public static bool GetHasError(DependencyObject element) => (bool)element.GetValue(HasErrorProperty);

    public static void SetHasError(DependencyObject element, bool value) => element.SetValue(HasErrorProperty, value);
}
