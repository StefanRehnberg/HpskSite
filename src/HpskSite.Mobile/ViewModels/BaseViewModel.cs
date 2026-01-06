using CommunityToolkit.Mvvm.ComponentModel;

namespace HpskSite.Mobile.ViewModels;

/// <summary>
/// Base ViewModel with common functionality
/// </summary>
public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>
    /// Inverse of IsBusy for button enabling
    /// </summary>
    public bool IsNotBusy => !IsBusy;
}
