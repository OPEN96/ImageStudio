using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ImageStudio.Services;
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

        PopulateFonts(fontFamily);
    }

    /// <summary>输入的文字内容。</summary>
    public string ResultText { get; private set; } = string.Empty;

    /// <summary>字号（像素）。</summary>
    public double ResultFontSize { get; private set; } = 48;

    public bool ResultBold { get; private set; }

    public string ResultFontFamily { get; private set; } = FontCatalog.DefaultFamily;

    /// <summary>用系统里真实安装的字体填充下拉框，并尽量选中上次用的那个。</summary>
    private void PopulateFonts(string fontFamily)
    {
        var families = FontCatalog.Families;
        if (families.Count == 0)
            families = ["Microsoft YaHei UI", "PingFang SC", "Noto Sans CJK SC", "DejaVu Sans"];

        FontBox.ItemsSource = families;

        var match = families.FirstOrDefault(f => string.Equals(f, fontFamily, StringComparison.OrdinalIgnoreCase))
            ?? families.FirstOrDefault(f => string.Equals(f, FontCatalog.DefaultFamily, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            FontBox.SelectedItem = match;
        else
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
        ResultFontFamily = FontBox.SelectedItem as string ?? FontCatalog.DefaultFamily;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
