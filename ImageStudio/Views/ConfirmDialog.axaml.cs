using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ImageStudio.Views;

/// <summary>未保存修改的三种处理方式。</summary>
public enum ConfirmResult
{
    Save,
    Discard,
    Cancel
}

/// <summary>“要保存改动吗？”三按钮确认框。</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message) : this()
    {
        Title = title;
        HeaderText.Text = title;
        MessageText.Text = message;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Close(ConfirmResult.Save);

    private void OnDiscardClick(object? sender, RoutedEventArgs e) => Close(ConfirmResult.Discard);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(ConfirmResult.Cancel);
}
