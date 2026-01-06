using HpskSite.Mobile.Views;

namespace HpskSite.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Register routes for navigation
        Routing.RegisterRoute("createMatch", typeof(CreateMatchPage));
        Routing.RegisterRoute("joinMatch", typeof(JoinMatchPage));
        Routing.RegisterRoute("qrScanner", typeof(QrScannerPage));
        Routing.RegisterRoute("activeMatch", typeof(ActiveMatchPage));
    }
}
