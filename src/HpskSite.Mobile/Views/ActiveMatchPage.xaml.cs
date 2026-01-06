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

    private void OnCompleteMatchClicked(object sender, EventArgs e)
    {
        if (_viewModel.CompleteMatchCommand.CanExecute(null))
        {
            _viewModel.CompleteMatchCommand.Execute(null);
        }
    }
}
