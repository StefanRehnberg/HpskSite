using HpskSite.Mobile.ViewModels;
using HpskSite.Shared.Constants;

namespace HpskSite.Mobile.Views;

public partial class CreateMatchPage : ContentPage
{
    public CreateMatchPage(CreateMatchViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnHandicapInfoTapped(object sender, EventArgs e)
    {
        await DisplayAlert(HandicapInfo.Title, HandicapInfo.GetFullTextForMobile(), "OK");
    }
}
