using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LanTodo.Windows;

internal static class SettingsTheme
{
    internal static Border Card(UIElement child) => new()
    {
        Child=child, Background=Brushes.White, BorderBrush=new SolidColorBrush(Color.FromRgb(215,225,233)),
        BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),Padding=new Thickness(20),Margin=new Thickness(0,0,0,14)
    };
    internal static TextBlock Heading(string title) => new() {Text=title,FontSize=24,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)};
    internal static TextBlock Hint(string value) => new() {Text=value,Foreground=Brushes.SlateGray,FontSize=13,Margin=new Thickness(0,0,0,18)};
    internal static void Danger(Button button)
    { button.Background=new SolidColorBrush(Color.FromRgb(255,240,242));button.Foreground=new SolidColorBrush(Color.FromRgb(179,64,82)); }
}
