using System;
using Avalonia;

namespace BigWalkVRInstaller
{
    static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // a crash before the window exists would otherwise vanish, and this is a terminal-launchable app
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("BigWalkVRInstaller failed to start: " + ex);
                return 1;
            }
        }

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
