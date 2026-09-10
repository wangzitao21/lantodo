using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LanTodo.Windows;

public sealed class ModernDialog : Window
{
    private MessageBoxResult result = MessageBoxResult.No;
    public ModernDialog(string message, string title, bool confirm)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = title; Width = 460; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(215,225,233)), Child = panel };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,16) });
        panel.Children.Add(new TextBlock { Text = message, Foreground = Brushes.SlateGray, LineHeight = 24, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,24,0,0) };
        panel.Children.Add(buttons);
        if (confirm)
        {
            var cancel = new Button { Content = "取消", IsCancel = true, Background = new SolidColorBrush(Color.FromRgb(241,245,249)), Foreground = Brushes.SlateGray };
            cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        }
        bool destructive = title.Contains("删除") || title.Contains("授权");
        var accept = new Button { Content = confirm ? (destructive ? "确认" + (title.Contains("删除") ? "删除" : "取消授权") : "确认继续") : "知道了", IsDefault = !destructive, Margin = new Thickness(0,4,0,4), FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(destructive ? Color.FromRgb(191,64,76) : Color.FromRgb(22,125,141)), Foreground = Brushes.White };
        accept.Click += (_, _) => { result = confirm ? MessageBoxResult.Yes : MessageBoxResult.OK; Close(); }; buttons.Children.Add(accept);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new ModernDialog(message, title, buttons == MessageBoxButton.YesNo);
        if (owner.IsVisible) dialog.Owner = owner; else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog(); return dialog.result;
    }
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new ModernDialog(message, title, buttons == MessageBoxButton.YesNo) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        dialog.ShowDialog(); return dialog.result;
    }
}
