using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MatchaLab.App.Views;

public partial class ProtocolView : UserControl
{
    public ProtocolView() => InitializeComponent();

    private void Back_Click(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as MainWindow)?.GoConnect();

    private void Pick_Click(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as MainWindow)?.GoConnect();
}
