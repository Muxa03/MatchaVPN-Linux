using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MatchaLab.App.Views;

public partial class ServersView : UserControl
{
    public ServersView()
    {
        InitializeComponent();
        ServerList.Loaded += (_, _) =>
        {
            if (ServerList.Scroll is ScrollViewer sv) SmoothScroll.Attach(sv);
        };
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as MainWindow)?.GoConnect();
}
