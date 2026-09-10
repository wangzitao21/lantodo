using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LanTodo.Windows;

public sealed class DateSelector : UserControl
{
    private readonly Button trigger;
    private readonly Popup popup;
    private readonly StackPanel calendar = new();
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime? selected;
    private string placeholder = "全部日期";
    public string Placeholder { get => placeholder; set { placeholder = value; UpdateLabel(); } }
    public event EventHandler? SelectedDateChanged;
    public DateTime? SelectedDate
    {
        get => selected;
        set
        {
            if (selected == value?.Date) return;
            selected = value?.Date;
            if (selected is { } date) month = new(date.Year, date.Month, 1);
            UpdateLabel(); SelectedDateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public DateSelector()
    {
        trigger = new Button { Height = 40, Margin = new Thickness(0), Padding = new Thickness(12,0,12,0), Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(211,223,230)), BorderThickness = new Thickness(1) };
        Content = trigger;
        popup = new Popup { PlacementTarget = trigger, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 6 };
        popup.Child = new Border { Width = 294, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(211,223,230)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(14), Child = calendar };
        trigger.Click += (_, _) => { RenderCalendar(); popup.IsOpen = !popup.IsOpen; };
        Loaded += (_, _) => UpdateLabel();
        Unloaded += (_, _) => popup.IsOpen = false;
        UpdateLabel();
    }
    private void UpdateLabel() => trigger.Content = (selected?.ToString("yyyy-MM-dd") ?? Placeholder) + "  ▾";
    private Button SmallButton(string text, Action action) { var b = new Button { Content = text, Height = 34, Margin = new Thickness(2), Padding = new Thickness(0) }; b.Click += (_, _) => action(); return b; }
    private void RenderCalendar()
    {
        calendar.Children.Clear();
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new GridLength(36) }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = new GridLength(36) });
        var prev = SmallButton("‹", () => { month = month.AddMonths(-1); RenderCalendar(); }); header.Children.Add(prev);
        var label = new TextBlock { Text = month.ToString("yyyy 年 M 月"), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label,1); header.Children.Add(label);
        var next = SmallButton("›", () => { month = month.AddMonths(1); RenderCalendar(); }); Grid.SetColumn(next,2); header.Children.Add(next); calendar.Children.Add(header);
        var days = new UniformGrid { Columns = 7, Margin = new Thickness(0,10,0,8) };
        foreach (var day in new[] { "一","二","三","四","五","六","日" }) days.Children.Add(new TextBlock { Text = day, Foreground = Brushes.SlateGray, FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0,5,0,8) });
        var start = month.AddDays(-(((int)month.DayOfWeek+6)%7));
        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var day = SmallButton(date.Day.ToString(), () => { SelectedDate = date; popup.IsOpen = false; });
            day.Background = date == selected ? new SolidColorBrush(Color.FromRgb(22,125,141)) : Brushes.Transparent;
            day.Foreground = date == selected ? Brushes.White : date.Month == month.Month ? new SolidColorBrush(Color.FromRgb(31,41,55)) : Brushes.LightSlateGray;
            if (date == DateTime.Today) { day.BorderThickness = new Thickness(1); day.BorderBrush = new SolidColorBrush(Color.FromRgb(22,125,141)); }
            days.Children.Add(day);
        }
        calendar.Children.Add(days);
        var shortcuts = new UniformGrid { Columns = 2 };
        shortcuts.Children.Add(SmallButton("今天", () => { SelectedDate = DateTime.Today; popup.IsOpen = false; }));
        shortcuts.Children.Add(SmallButton("清除日期", () => { SelectedDate = null; popup.IsOpen = false; })); calendar.Children.Add(shortcuts);
    }
}
