using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MatchaLab.App.ViewModels;

namespace MatchaLab.App.Views;

public partial class MainWindow : Window
{
    private readonly ConnectView _connect = new();
    private readonly ServersView _servers = new();
    private readonly ProtocolView _protocol = new();
    private readonly SettingsView _settings = new();
    private readonly KeyView _key = new();
    private readonly SlideFadeTransition _fx = new();

    public Action? CloseRequested { get; set; }

    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsLinux())
        {
            CanResize = true;
            MinWidth = 320;
            MinHeight = 500;
        }
        Host.PageTransition = _fx;
        TryLoadIcon();
        Opened += (_, _) => OnOpenedAsync();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && DataContext is AppViewModel vm)
                vm.SetUiVisible(WindowState != WindowState.Minimized);
        };
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            CloseRequested?.Invoke();
        };
    }

    private void TryLoadIcon()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://MatchaLab/Assets/appicon.png"));
            var bmp = new Bitmap(s);
            Icon = new WindowIcon(bmp);
            LogoImage.Source = bmp;
        }
        catch { }
    }

    private async void OnOpenedAsync()
    {
        if (DataContext is not AppViewModel vm) return;
        vm.KeyChanged += UpdateShell;
        UpdateShell();
        await vm.InitializeAsync();
    }

    private void UpdateShell()
    {
        if (DataContext is not AppViewModel vm) return;
        Go(vm.HasKey ? _connect : _key, forward: true);
    }

    private void Go(object page, bool forward)
    {
        if (ReferenceEquals(Host.Content, page)) return;
        _fx.Forward = forward;
        Host.Content = page;
    }

    public void GoConnect() => Go(_connect, forward: false);
    public void GoServers() => Go(_servers, forward: true);
    public void GoProtocol() => Go(_protocol, forward: true);
    public void GoSettings() => Go(_settings, forward: true);
    public void GoSettingsBack() => Go(_settings, forward: false);
    public void GoKeyEntry() => Go(_key, forward: true);

    private void Chrome_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Min_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
