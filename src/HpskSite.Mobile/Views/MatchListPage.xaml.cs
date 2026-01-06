using HpskSite.Mobile.ViewModels;

namespace HpskSite.Mobile.Views;

public partial class MatchListPage : ContentPage
{
    private readonly MatchListViewModel _viewModel;

    public MatchListPage(MatchListViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

        // Track size changes to detect orientation
        SizeChanged += OnSizeChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UpdateOrientation();
        await _viewModel.LoadMatchesCommand.ExecuteAsync(null);
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        UpdateOrientation();
    }

    private void UpdateOrientation()
    {
        _viewModel.IsLandscape = Width > Height;
    }
}
