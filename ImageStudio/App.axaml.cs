using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ImageStudio.Views;

namespace ImageStudio;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 先解析平台字体（Linux 上并没有微软雅黑），再创建窗口
            Services.FontCatalog.EnsureLoaded();

            var window = new MainWindow();
            desktop.MainWindow = window;

            // 支持通过命令行 / 双击关联文件的方式直接打开图片
            var startupFile = desktop.Args?.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(startupFile))
                window.OpenPath(startupFile);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
