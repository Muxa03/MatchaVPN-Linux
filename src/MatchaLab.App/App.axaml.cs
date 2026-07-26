using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MatchaLab.App.Services;
using MatchaLab.App.ViewModels;
using MatchaLab.App.Views;
using MatchaLab.Core;

namespace MatchaLab.App;

public partial class App : Application
{
    private AppTray? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var api = new ApiClient(http);
            var secret = new SecretStore();
            var sub = new SubscriptionStore(api, secret);
            var split = new SplitTunnelStore(api);
            ITunnelController tunnel =
                OperatingSystem.IsWindows()
                    ? new TunnelRouter(
                        new WindowsTunnelController(),
                        new SingBoxTunnelController())
                    : OperatingSystem.IsLinux()
                        ? new TunnelRouter(
                            new LinuxTunnelController(),
                            new LinuxSingBoxTunnelController())
                        : new StubTunnelController();
            var vm = new AppViewModel(api, sub, split, tunnel, settings);

            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            var tray = _tray = new AppTray(this, desktop, vm);
            tray.Install();
            SingleInstance.ShowRequested += tray.Show;

            if (desktop.Args?.Contains("--min") == true)
            {
                _ = vm.InitializeAsync();
            }
            else
            {
                var splash = new SplashWindow();
                splash.Show();
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    var win = tray.Window;
                    await splash.WaitAsync();
                    win.Show();
                    splash.Close();
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
