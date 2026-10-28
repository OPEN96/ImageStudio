using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ImageStudio.Views;

public partial class SizeDialog : Window
{
    public SizeDialog()
    {
        InitializeComponent();
    }

    public SizeDialog(string title, int width, int height, string okText) : this()
    {
        Title = title;
        HeaderText.Text = title;
        OkButton.Content = okText;
        WidthBox.Value = width;
        HeightBox.Value = height;
        HintText.Text = $"当前尺寸：{width} × {height} px";
        ResultWidth = width;
        ResultHeight = height;
    }

    public int ResultWidth { get; private set; }

    public int ResultHeight { get; private set; }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        ResultWidth = (int)(WidthBox.Value ?? 1);
        ResultHeight = (int)(HeightBox.Value ?? 1);
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
