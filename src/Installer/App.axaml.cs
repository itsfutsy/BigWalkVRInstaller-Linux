using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BigWalkVRInstaller.Services;

namespace BigWalkVRInstaller
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // settings decide the window chrome, so they have to be read before the window exists
                var settings = AppSettings.Load();
                desktop.MainWindow = new MainWindow(settings);
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
