using HpskSite.Mobile.ViewModels;

namespace HpskSite.Mobile.Views;

public partial class JoinMatchPage : ContentPage
{
    public JoinMatchPage(JoinMatchViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
