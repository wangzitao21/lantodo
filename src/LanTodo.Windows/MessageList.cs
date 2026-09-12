using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LanTodo.Core;

namespace LanTodo.Windows;

public sealed record MessageRow(string Id, string Stamp, object Value);

// Native recycling keeps off-screen cards, images and event handlers out of the visual tree.
public sealed class MessageList : ListBox
{
    private readonly ObservableCollection<MessageRow> rows = new();
    public Func<object, FrameworkElement>? RenderRow { get; set; }
    public MessageList()
    {
        BorderThickness = new Thickness(0); Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetCanContentScroll(this, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter }));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.IsTabStopProperty, false));
        ItemContainerStyle = style;
        ItemsSource = rows;
    }
    public void UpdateRows(IEnumerable<MessageRow> next)
    {
        int index = 0;
        foreach (var row in next)
        {
            if (index < rows.Count && rows[index].Id == row.Id)
            { if (rows[index].Stamp != row.Stamp) rows[index] = row; }
            else
            {
                int existing = -1;
                for (int j = index + 1; j < rows.Count; j++) if (rows[j].Id == row.Id) { existing = j; break; }
                if (existing >= 0) { rows.Move(existing, index); if (rows[index].Stamp != row.Stamp) rows[index] = row; }
                else rows.Insert(index, row);
            }
            index++;
        }
        while (rows.Count > index) rows.RemoveAt(rows.Count - 1);
    }
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        ((ListBoxItem)element).Content = RenderRow!(((MessageRow)item).Value);
    }
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        ((ListBoxItem)element).Content = null;
        base.ClearContainerForItemOverride(element, item);
    }
}
