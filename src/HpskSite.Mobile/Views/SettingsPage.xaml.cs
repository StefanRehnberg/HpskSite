using HpskSite.Mobile.ViewModels;

namespace HpskSite.Mobile.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnDiagnosticsClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("diagnostics");
    }
}
