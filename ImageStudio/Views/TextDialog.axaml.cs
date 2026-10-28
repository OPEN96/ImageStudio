using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ImageStudio.Views;

public partial class TextDialog : Window
{
    public TextDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public TextDialog(string title, string initial) : this()
    {
        Title = title;
        HeaderText.Text = title;
        InputBox.Text = initial;
        ResultText = initial;
    }

    public string ResultText { get; private set; } = string.Empty;

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        ResultText = string.IsNullOrEmpty(text) ? ResultText : text;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
