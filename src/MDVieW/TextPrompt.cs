using System.Windows;
using System.Windows.Controls;

namespace MDVieW;

internal static class TextPrompt
{
    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var dialog = new Window { Owner = owner, Title = title, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = 13 };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 10) });
        var text = new TextBox { Text = initial, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        var ok = new Button { Content = "挿入", IsDefault = true };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
        return dialog.ShowDialog() == true ? text.Text : null;
    }
}
