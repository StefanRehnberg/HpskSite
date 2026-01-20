using HpskSite.Mobile.ViewModels;

namespace HpskSite.Mobile.Views;

public partial class ActiveMatchPage : ContentPage
{
    private readonly ActiveMatchViewModel _viewModel;

    public ActiveMatchPage(ActiveMatchViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    private async void OnCompleteMatchClicked(object sender, EventArgs e)
    {
        bool confirm = await DisplayAlert(
            "Avsluta match",
            "Är du säker på att du vill avsluta matchen? Detta kan inte ångras.",
            "Ja, avsluta",
            "Avbryt");

        if (confirm && _viewModel.CompleteMatchCommand.CanExecute(null))
        {
            _viewModel.CompleteMatchCommand.Execute(null);
        }
    }
}
