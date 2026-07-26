using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MatchaLab.App.ViewModels;

namespace MatchaLab.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        SmoothScroll.Attach(SettingsScroll);
    }

    private AppViewModel? Vm => DataContext as AppViewModel;

    private void Back_Click(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as MainWindow)?.GoConnect();

    private void AddDomain_Click(object? sender, RoutedEventArgs e) => AddDomain();

    private void Domain_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddDomain();
    }

    private void AddDomain()
    {
        var text = DomainBox.Text;
        if (Vm is null || string.IsNullOrWhiteSpace(text)) return;
        Vm.AddDomain(text.Trim());
        DomainBox.Text = string.Empty;
    }

    private void RemoveDomain_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string domain }) Vm?.RemoveDomain(domain);
    }

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string id }) Vm?.SetTheme(id);
    }

    private void ChangeKey_Click(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as MainWindow)?.GoKeyEntry();

    private void TopUp_Click(object? sender, RoutedEventArgs e)
        => OpenUrl("https://matchavpn.space");

    private void Terms_Click(object? sender, RoutedEventArgs e)
        => OpenUrl("https://matchavpn.space/legal/terms");

    private void Privacy_Click(object? sender, RoutedEventArgs e)
        => OpenUrl("https://matchavpn.space/legal/privacy");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
