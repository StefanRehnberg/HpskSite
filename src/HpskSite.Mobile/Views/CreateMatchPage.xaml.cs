using HpskSite.Mobile.ViewModels;

namespace HpskSite.Mobile.Views;

public partial class CreateMatchPage : ContentPage
{
    public CreateMatchPage(CreateMatchViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
