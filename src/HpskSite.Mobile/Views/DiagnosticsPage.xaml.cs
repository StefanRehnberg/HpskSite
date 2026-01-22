using HpskSite.Mobile.ViewModels;

namespace HpskSite.Mobile.Views;

public partial class DiagnosticsPage : ContentPage
{
    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
