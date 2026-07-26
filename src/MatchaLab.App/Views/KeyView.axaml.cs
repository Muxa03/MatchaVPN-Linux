using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MatchaLab.App.ViewModels;

namespace MatchaLab.App.Views;

public partial class KeyView : UserControl
{
    public KeyView() => InitializeComponent();

    private void Activate_Click(object? sender, RoutedEventArgs e) => Submit();

    private void Back_Click(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as MainWindow)?.GoSettingsBack();

    private void Bot_Click(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://t.me/MatchaVPN_bot") { UseShellExecute = true }); }
        catch { }
    }

    private void Key_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Submit();
    }

    private void Submit()
    {
        if (DataContext is AppViewModel vm && !string.IsNullOrWhiteSpace(KeyBox.Text))
            vm.SetKey(KeyBox.Text!.Trim());
    }
}
