using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MatchaLab.App.ViewModels;
using MatchaLab.App.Views;

namespace MatchaLab.App.Services;

public sealed class AppTray
{
    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly AppViewModel _vm;
    private MainWindow? _window;
    private TrayIcon? _icon;

    public AppTray(Application app, IClassicDesktopStyleApplicationLifetime desktop, AppViewModel vm)
    {
        _app = app;
        _desktop = desktop;
        _vm = vm;
    }

    public MainWindow Window
    {
        get
        {
            if (_window is not null) return _window;
            _window = new MainWindow { DataContext = _vm };
            _window.CloseRequested = Hide;
            return _window;
        }
    }

    public void Install()
    {
        try
        {
            var open = new NativeMenuItem("Открыть");
            open.Click += (_, _) => Show();
            var quit = new NativeMenuItem("Выход");
            quit.Click += (_, _) => Quit();

            var menu = new NativeMenu();
            menu.Add(open);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(quit);

            _icon = new TrayIcon { ToolTipText = "MatchaLab VPN", IsVisible = true, Menu = menu };
            try
            {
                using var s = AssetLoader.Open(new Uri("avares://MatchaLab/Assets/appicon.png"));
                _icon.Icon = new WindowIcon(new Bitmap(s));
            }
            catch { }
            _icon.Clicked += (_, _) => Show();
            TrayIcon.SetIcons(_app, new TrayIcons { _icon });
        }
        catch
        {
            _icon = null;
        }
    }

    public void Show() => Dispatcher.UIThread.Post(() =>
    {
        var w = Window;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        _vm.SetUiVisible(true);
    });

    private void Hide()
    {
        _window?.Hide();
        _vm.SetUiVisible(false);
    }

    private void Quit()
    {
        if (_window is not null) _window.AllowClose = true;
        _icon?.Dispose();
        _desktop.Shutdown();
    }
}
