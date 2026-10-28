using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaColor = Avalonia.Media.Color;

namespace ImageStudio.Views;

public partial class TextToolDialog : Window
{
    public TextToolDialog()
    {
        InitializeComponent();
        Opened += (_, _) => ContentBox.Focus();
    }

    public TextToolDialog(Color foreground, double fontSize, bool bold, string fontFamily, string initial)
        : this()
    {
        ColorSwatch.Background = new SolidColorBrush(foreground);
        ColorText.Text = $"使用前景色 #{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}";
        SizeBox.Value = (decimal)Math.Clamp(fontSize, 8, 512);
        BoldBox.IsChecked = bold;
        ContentBox.Text = initial;

        SelectFont(fontFamily);
    }

    /// <summary>输入的文字内容。</summary>
    public string ResultText { get; private set; } = string.Empty;

    /// <summary>字号（像素）。</summary>
    public double ResultFontSize { get; private set; } = 48;

    public bool ResultBold { get; private set; }

    public string ResultFontFamily { get; private set; } = "Microsoft YaHei UI";

    private void SelectFont(string fontFamily)
    {
        foreach (var item in FontBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, fontFamily, StringComparison.OrdinalIgnoreCase))
            {
                FontBox.SelectedItem = item;
                return;
            }
        }

        FontBox.SelectedIndex = 0;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var text = ContentBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            Close(false);
            return;
        }

        ResultText = text;
        ResultFontSize = (double)(SizeBox.Value ?? 48);
        ResultBold = BoldBox.IsChecked == true;
        ResultFontFamily = (FontBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Microsoft YaHei UI";
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
