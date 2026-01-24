using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HpskSite.Mobile.Models;
using HpskSite.Mobile.Services;
using HpskSite.Shared.Models;
// MatchSpectator is in Services namespace

namespace HpskSite.Mobile.ViewModels;

/// <summary>
/// ViewModel for active match page (score entry)
/// </summary>
[QueryProperty(nameof(MatchCode), "code")]
[QueryProperty(nameof(IsSpectatorMode), "spectator")]
public partial class ActiveMatchViewModel : BaseViewModel
{
    private readonly IMatchService _matchService;
    private readonly ISignalRService _signalRService;
    private readonly IAuthService _authService;
    private readonly ImageCompressionService _imageCompressionService;
    private readonly IApiService _apiService;

    public ActiveMatchViewModel(
        IMatchService matchService,
        ISignalRService signalRService,
        IAuthService authService,
        ImageCompressionService imageCompressionService,
        IApiService apiService)
    {
        _matchService = matchService;
        _signalRService = signalRService;
        _authService = authService;
        _imageCompressionService = imageCompressionService;
        _apiService = apiService;
        Title = "Match";

        // Subscribe to SignalR events (using correct event names that match server)
        _signalRService.ScoreUpdated += OnScoreUpdated;
        _signalRService.ParticipantJoined += OnParticipantJoined;
        _signalRService.ParticipantLeft += OnParticipantLeft;
        _signalRService.MatchCompleted += OnMatchCompleted;
        _signalRService.SpectatorListUpdated += OnSpectatorListUpdated;
        _signalRService.JoinRequestReceived += OnJoinRequestReceived;
        _signalRService.JoinRequestAccepted += OnJoinRequestAccepted;
        _signalRService.JoinRequestBlocked += OnJoinRequestBlocked;
        _signalRService.ReactionUpdated += OnReactionUpdated;
        _signalRService.SettingsUpdated += OnSettingsUpdated;
        _signalRService.TeamScoreUpdated += OnTeamScoreUpdated;
    }

    private string _matchCode = string.Empty;
    public string MatchCode
    {
        get => _matchCode;
        set
        {
            if (SetProperty(ref _matchCode, value))
            {
                // Don't auto-load here - wait for TryLoadIfReady
                // which ensures both MatchCode and IsSpectatorMode are set
                TryLoadIfReady();
            }
        }
    }

    // Spectator mode properties
    private string? _isSpectatorMode;
    private bool _spectatorModeSet; // Track if spectator param was processed
    public string? IsSpectatorMode
    {
        get => _isSpectatorMode;
        set
        {
            _isSpectatorMode = value;
            IsSpectator = value?.ToLower() == "true";
            _spectatorModeSet = true;
            System.Diagnostics.Debug.WriteLine($"IsSpectatorMode set to: {value}, IsSpectator={IsSpectator}");
            TryLoadIfReady();
        }
    }

    private bool _hasStartedLoading;

    /// <summary>
    /// Attempts to load match data once both MatchCode is set and spectator mode is determined.
    /// Shell navigation may set query properties in any order, so we wait for both.
    /// </summary>
    private void TryLoadIfReady()
    {
        // Need MatchCode to be set
        if (string.IsNullOrEmpty(MatchCode))
            return;

        // Need to know if we're in spectator mode (wait for the parameter to be processed)
        // If spectator param is not in URL, _spectatorModeSet will remain false and IsSpectator defaults to false
        // We use a small delay to allow Shell to set all properties
        if (!_hasStartedLoading)
        {
            _hasStartedLoading = true;
            // Small delay to ensure all query params are set
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Delay(50); // Brief delay for Shell to finish setting properties
                System.Diagnostics.Debug.WriteLine($"TryLoadIfReady: Loading match {MatchCode}, IsSpectator={IsSpectator}, _spectatorModeSet={_spectatorModeSet}");
                _ = LoadMatchAsync();
            });
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanJoinFromSpectator))]
    private bool _isSpectator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanJoinFromSpectator))]
    private bool _canJoin;

    /// <summary>
    /// True if user is spectator and can join this match
    /// </summary>
    public bool CanJoinFromSpectator => IsSpectator && CanJoin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QrCodeUrl))]
    [NotifyPropertyChangedFor(nameof(ShareLink))]
    private TrainingMatch? _match;

    [ObservableProperty]
    private TrainingMatchParticipant? _currentParticipant;

    [ObservableProperty]
    private ObservableCollection<TrainingMatchParticipant> _participants = new();

    [ObservableProperty]
    private ObservableCollection<MatchSpectator> _spectators = new();

    /// <summary>
    /// Current rankings for all participants (updated when scores change)
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ParticipantRanking> _rankings = new();

    /// <summary>
    /// Teams in the match (for team matches)
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TrainingMatchTeam> _teams = new();

    /// <summary>
    /// Whether this is a team match
    /// </summary>
    [ObservableProperty]
    private bool _isTeamMatch;

    /// <summary>
    /// Whether team scores view is enabled (vs individual view)
    /// </summary>
    [ObservableProperty]
    private bool _showTeamScores;

    /// <summary>
    /// Team rankings (sorted by adjusted team score)
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TrainingMatchTeam> _teamRankings = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSeriesNumber))]
    private int _currentSeriesIndex;

    /// <summary>
    /// 1-based series number for display (CurrentSeriesIndex + 1)
    /// </summary>
    public int CurrentSeriesNumber => CurrentSeriesIndex + 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShot1Selected))]
    [NotifyPropertyChangedFor(nameof(IsShot2Selected))]
    [NotifyPropertyChangedFor(nameof(IsShot3Selected))]
    [NotifyPropertyChangedFor(nameof(IsShot4Selected))]
    [NotifyPropertyChangedFor(nameof(IsShot5Selected))]
    [NotifyPropertyChangedFor(nameof(CurrentShotNumber))]
    private int _currentShotIndex;

    /// <summary>
    /// 1-based shot number for display (CurrentShotIndex + 1)
    /// </summary>
    public int CurrentShotNumber => CurrentShotIndex + 1;

    [ObservableProperty]
    private int _currentScore;

    [ObservableProperty]
    private bool _isX;

    // Shot tracking for current series (5 shots)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeriesTotal))]
    [NotifyPropertyChangedFor(nameof(SeriesXCount))]
    private string _shot1 = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeriesTotal))]
    [NotifyPropertyChangedFor(nameof(SeriesXCount))]
    private string _shot2 = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeriesTotal))]
    [NotifyPropertyChangedFor(nameof(SeriesXCount))]
    private string _shot3 = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeriesTotal))]
    [NotifyPropertyChangedFor(nameof(SeriesXCount))]
    private string _shot4 = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeriesTotal))]
    [NotifyPropertyChangedFor(nameof(SeriesXCount))]
    private string _shot5 = "-";

    // Selection states for shot circles
    public bool IsShot1Selected => CurrentShotIndex == 0;
    public bool IsShot2Selected => CurrentShotIndex == 1;
    public bool IsShot3Selected => CurrentShotIndex == 2;
    public bool IsShot4Selected => CurrentShotIndex == 3;
    public bool IsShot5Selected => CurrentShotIndex == 4;

    // Calculated series total
    public int SeriesTotal => GetShotValue(Shot1) + GetShotValue(Shot2) + GetShotValue(Shot3) + GetShotValue(Shot4) + GetShotValue(Shot5);

    // Count of X shots in current series
    public int SeriesXCount => (Shot1 == "X" ? 1 : 0) + (Shot2 == "X" ? 1 : 0) + (Shot3 == "X" ? 1 : 0) + (Shot4 == "X" ? 1 : 0) + (Shot5 == "X" ? 1 : 0);

    private int GetShotValue(string shot)
    {
        if (shot == "-" || string.IsNullOrEmpty(shot)) return 0;
        if (shot == "X") return 10;
        return int.TryParse(shot, out int val) ? val : 0;
    }

    private void SetCurrentShot(string value)
    {
        switch (CurrentShotIndex)
        {
            case 0: Shot1 = value; break;
            case 1: Shot2 = value; break;
            case 2: Shot3 = value; break;
            case 3: Shot4 = value; break;
            case 4: Shot5 = value; break;
        }
    }

    private string GetCurrentShot()
    {
        return CurrentShotIndex switch
        {
            0 => Shot1,
            1 => Shot2,
            2 => Shot3,
            3 => Shot4,
            4 => Shot5,
            _ => "-"
        };
    }

    private void ClearAllShots()
    {
        Shot1 = Shot2 = Shot3 = Shot4 = Shot5 = "-";
        CurrentShotIndex = 0;
        CurrentScore = 0;
        IsX = false;
    }

    [ObservableProperty]
    private bool _isMatchHost;

    [ObservableProperty]
    private bool _isMatchActive;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    /// <summary>
    /// Whether the score entry panel is open
    /// </summary>
    [ObservableProperty]
    private bool _isScoreEntryOpen;

    /// <summary>
    /// Whether the spectators modal is open
    /// </summary>
    [ObservableProperty]
    private bool _isSpectatorsModalOpen;

    /// <summary>
    /// Whether the settings modal is open (host only)
    /// </summary>
    [ObservableProperty]
    private bool _isSettingsModalOpen;

    /// <summary>
    /// Whether the share modal is open (for non-host participants/spectators)
    /// </summary>
    [ObservableProperty]
    private bool _isShareModalOpen;

    /// <summary>
    /// Max series count input value for settings modal
    /// </summary>
    [ObservableProperty]
    private string? _maxSeriesCountInput;

    /// <summary>
    /// Whether settings are being saved
    /// </summary>
    [ObservableProperty]
    private bool _isSavingSettings;

    /// <summary>
    /// Whether the join QR code is visible in the settings modal
    /// </summary>
    [ObservableProperty]
    private bool _isJoinQrVisible;

    /// <summary>
    /// QR code URL for sharing the match
    /// </summary>
    public string QrCodeUrl => Match != null
        ? $"{_apiService.BaseUrl}/umbraco/surface/TrainingMatch/GetJoinQrCode?matchCode={Match.MatchCode}"
        : string.Empty;

    /// <summary>
    /// Share link for the match
    /// </summary>
    public string ShareLink => Match != null
        ? $"{_apiService.BaseUrl}/traningsmatch/?join={Match.MatchCode}"
        : string.Empty;

    /// <summary>
    /// Whether the photo prompt is visible after saving a series
    /// </summary>
    [ObservableProperty]
    private bool _isPhotoPromptVisible;

    /// <summary>
    /// Whether a photo is currently being uploaded
    /// </summary>
    [ObservableProperty]
    private bool _isUploadingPhoto;

    /// <summary>
    /// The series number that was just saved (for photo upload)
    /// </summary>
    private int _savedSeriesNumber;

    /// <summary>
    /// Whether we're in edit mode (editing an existing series)
    /// </summary>
    [ObservableProperty]
    private bool _isEditMode;

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCaptureNewPhoto));
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // When IsBusy changes, also update CanCaptureNewPhoto
        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(CanCaptureNewPhoto));
        }
    }

    /// <summary>
    /// The series number being edited (1-based)
    /// </summary>
    [ObservableProperty]
    private int _editingSeriesNumber;

    /// <summary>
    /// The target photo URL for the series being edited
    /// </summary>
    [ObservableProperty]
    private string? _editingSeriesPhotoUrl;

    partial void OnEditingSeriesPhotoUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(CanCaptureNewPhoto));
    }

    /// <summary>
    /// Whether the "Save + Camera" button should be enabled.
    /// Disabled when editing a series that already has a photo, or when busy.
    /// </summary>
    public bool CanCaptureNewPhoto => IsNotBusy && (!IsEditMode || string.IsNullOrEmpty(EditingSeriesPhotoUrl));

    /// <summary>
    /// Maximum series count for building the scoreboard rows.
    /// Respects Match.MaxSeriesCount setting if set, otherwise shows all series (min 6).
    /// </summary>
    public int MaxSeriesCount
    {
        get
        {
            int actualMaxSeries = Participants.Count > 0
                ? Math.Max(6, Participants.Max(p => p.Scores?.Count ?? 0))
                : 6;

            // If match has MaxSeriesCount setting, limit display to that
            if (Match?.MaxSeriesCount.HasValue == true)
            {
                return Math.Min(actualMaxSeries, Match.MaxSeriesCount.Value);
            }

            return actualMaxSeries;
        }
    }

    // Photo Viewer Modal Properties
    /// <summary>
    /// Whether the photo viewer modal is open
    /// </summary>
    [ObservableProperty]
    private bool _isPhotoViewerOpen;

    /// <summary>
    /// The photo URL being viewed (may be null when viewing a score without photo)
    /// </summary>
    [ObservableProperty]
    private string? _viewingPhotoUrl;

    partial void OnViewingPhotoUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(HasViewingPhoto));
    }

    /// <summary>
    /// Whether the series being viewed has a photo
    /// </summary>
    public bool HasViewingPhoto => !string.IsNullOrEmpty(ViewingPhotoUrl);

    /// <summary>
    /// The series score being viewed (for display when no photo)
    /// </summary>
    [ObservableProperty]
    private int _viewingSeriesScore;

    /// <summary>
    /// The X count for the series being viewed
    /// </summary>
    [ObservableProperty]
    private int _viewingSeriesXCount;

    /// <summary>
    /// The individual shots for the series being viewed
    /// </summary>
    [ObservableProperty]
    private List<string>? _viewingSeriesShots;

    /// <summary>
    /// Display string for individual shots (e.g., "10 - X - 9 - 10 - X")
    /// </summary>
    public string ViewingShotsDisplay => ViewingSeriesShots != null && ViewingSeriesShots.Count > 0
        ? string.Join(" - ", ViewingSeriesShots)
        : "";

    /// <summary>
    /// The shooter's name for the photo being viewed
    /// </summary>
    [ObservableProperty]
    private string? _viewingPhotoShooterName;

    /// <summary>
    /// The series number for the photo being viewed
    /// </summary>
    [ObservableProperty]
    private int _viewingPhotoSeriesNumber;

    /// <summary>
    /// The member ID of the shooter whose photo is being viewed
    /// </summary>
    [ObservableProperty]
    private int _viewingPhotoMemberId;

    partial void OnViewingPhotoMemberIdChanged(int value)
    {
        OnPropertyChanged(nameof(IsCurrentUserPhotoOwner));
    }

    /// <summary>
    /// Reactions on the photo being viewed
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<PhotoReaction> _viewingPhotoReactions = new();

    /// <summary>
    /// The current user's reaction on the photo (if any)
    /// </summary>
    [ObservableProperty]
    private string? _currentUserReaction;

    /// <summary>
    /// Whether a reaction is currently being sent
    /// </summary>
    [ObservableProperty]
    private bool _isSendingReaction;

    [RelayCommand]
    private void OpenScoreEntry()
    {
        // Reset to new entry mode
        IsEditMode = false;
        EditingSeriesNumber = 0;
        EditingSeriesPhotoUrl = null;

        // Clear all shot values for fresh entry
        Shot1 = null;
        Shot2 = null;
        Shot3 = null;
        Shot4 = null;
        Shot5 = null;
        CurrentShotIndex = 0;

        // Reset CurrentSeriesIndex to next available series (after existing scores)
        if (CurrentParticipant?.Scores != null)
        {
            CurrentSeriesIndex = CurrentParticipant.Scores.Count;
        }
        else
        {
            CurrentSeriesIndex = 0;
        }

        IsScoreEntryOpen = true;
        HasError = false;
    }

    /// <summary>
    /// Open score entry in edit mode for a specific series
    /// </summary>
    [RelayCommand]
    private void OpenEditScore(ScoreboardCell cell)
    {
        if (cell == null || !cell.CanEdit)
            return;

        System.Diagnostics.Debug.WriteLine($"OpenEditScore: Editing series {cell.SeriesNumber}, HasPhoto={cell.HasPhoto}, TargetPhotoUrl={cell.TargetPhotoUrl ?? "null"}");

        // Set edit mode
        IsEditMode = true;
        EditingSeriesNumber = cell.SeriesNumber;
        EditingSeriesPhotoUrl = cell.TargetPhotoUrl;
        System.Diagnostics.Debug.WriteLine($"OpenEditScore: EditingSeriesPhotoUrl set to {EditingSeriesPhotoUrl ?? "null"}");

        // Set the current series index to the one being edited (0-based)
        CurrentSeriesIndex = cell.SeriesNumber - 1;

        // Populate the shot circles with existing data
        ClearAllShots();
        if (cell.Shots != null && cell.Shots.Count > 0)
        {
            for (int i = 0; i < cell.Shots.Count && i < 5; i++)
            {
                SetShotAtIndex(i, cell.Shots[i]);
            }
        }

        IsScoreEntryOpen = true;
        HasError = false;
    }

    /// <summary>
    /// Helper method to set a shot at a specific index
    /// </summary>
    private void SetShotAtIndex(int index, string value)
    {
        switch (index)
        {
            case 0: Shot1 = value; break;
            case 1: Shot2 = value; break;
            case 2: Shot3 = value; break;
            case 3: Shot4 = value; break;
            case 4: Shot5 = value; break;
        }
    }

    [RelayCommand]
    private void CloseScoreEntry()
    {
        IsScoreEntryOpen = false;
        IsPhotoPromptVisible = false;
        IsEditMode = false;
        EditingSeriesNumber = 0;
        EditingSeriesPhotoUrl = null;

        // Clear shot values to prevent them appearing in next entry
        Shot1 = null;
        Shot2 = null;
        Shot3 = null;
        Shot4 = null;
        Shot5 = null;
        CurrentShotIndex = 0;

        // Reset CurrentSeriesIndex to next available series (after existing scores)
        // This prevents accidentally overwriting a series if edit was cancelled
        if (CurrentParticipant?.Scores != null)
        {
            CurrentSeriesIndex = CurrentParticipant.Scores.Count;
        }
        else
        {
            CurrentSeriesIndex = 0;
        }
    }

    [RelayCommand]
    private void OpenSpectatorsModal()
    {
        IsSpectatorsModalOpen = true;
    }

    [RelayCommand]
    private void CloseSpectatorsModal()
    {
        IsSpectatorsModalOpen = false;
    }

    /// <summary>
    /// Open the settings modal (host only)
    /// </summary>
    [RelayCommand]
    private void OpenSettingsModal()
    {
        if (!IsMatchHost)
            return;

        // Initialize the input with current value
        MaxSeriesCountInput = Match?.MaxSeriesCount?.ToString() ?? "";
        IsJoinQrVisible = false; // Start with QR hidden
        IsSettingsModalOpen = true;
    }

    /// <summary>
    /// Close the settings modal
    /// </summary>
    [RelayCommand]
    private void CloseSettingsModal()
    {
        IsSettingsModalOpen = false;
    }

    /// <summary>
    /// Toggle the join QR code visibility in settings modal
    /// </summary>
    [RelayCommand]
    private void ToggleJoinQr()
    {
        IsJoinQrVisible = !IsJoinQrVisible;
    }

    /// <summary>
    /// Open the share modal (for non-host participants/spectators)
    /// </summary>
    [RelayCommand]
    private void OpenShareModal()
    {
        IsShareModalOpen = true;
    }

    /// <summary>
    /// Close the share modal
    /// </summary>
    [RelayCommand]
    private void CloseShareModal()
    {
        IsShareModalOpen = false;
    }

    /// <summary>
    /// Save match settings (max series count)
    /// </summary>
    [RelayCommand]
    private async Task SaveMatchSettingsAsync()
    {
        if (IsSavingSettings || !IsMatchHost || Match == null)
            return;

        try
        {
            IsSavingSettings = true;
            HasError = false;

            // Parse the input
            int? maxSeriesCount = null;
            if (!string.IsNullOrWhiteSpace(MaxSeriesCountInput) && int.TryParse(MaxSeriesCountInput, out int parsed) && parsed > 0)
            {
                maxSeriesCount = parsed;
            }

            var result = await _matchService.UpdateMatchSettingsAsync(MatchCode, maxSeriesCount);

            if (result.Success)
            {
                // Update local match data
                if (Match != null)
                {
                    Match.MaxSeriesCount = maxSeriesCount;
                }

                // Close modal and reload data
                IsSettingsModalOpen = false;
                await LoadMatchDataAsync();
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte spara inställningar";
                HasError = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsSavingSettings = false;
        }
    }

    #region Guest Management

    [ObservableProperty]
    private bool _isAddGuestModalOpen;

    [ObservableProperty]
    private bool _isGuestQrModalOpen;

    [ObservableProperty]
    private string _guestDisplayName = string.Empty;

    [ObservableProperty]
    private string? _guestHandicapClass;

    [ObservableProperty]
    private bool _isAddingGuest;

    [ObservableProperty]
    private bool _hasGuestError;

    [ObservableProperty]
    private string _guestErrorMessage = string.Empty;

    [ObservableProperty]
    private string _addedGuestName = string.Empty;

    [ObservableProperty]
    private string _guestClaimQrUrl = string.Empty;

    /// <summary>
    /// Collection of guest participants in the match (filtered from Participants)
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TrainingMatchParticipant> _guestParticipants = new();

    /// <summary>
    /// Whether there are any guest participants to show
    /// </summary>
    public bool HasGuestParticipants => GuestParticipants.Count > 0;

    /// <summary>
    /// Whether a guest operation is in progress
    /// </summary>
    [ObservableProperty]
    private bool _isGuestOperationBusy;

    /// <summary>
    /// Open the add guest modal (host only)
    /// </summary>
    [RelayCommand]
    private void OpenAddGuestModal()
    {
        if (!IsMatchHost)
            return;

        // Reset form
        GuestDisplayName = string.Empty;
        GuestHandicapClass = null;
        HasGuestError = false;
        GuestErrorMessage = string.Empty;
        IsAddGuestModalOpen = true;
    }

    /// <summary>
    /// Close the add guest modal
    /// </summary>
    [RelayCommand]
    private void CloseAddGuestModal()
    {
        IsAddGuestModalOpen = false;
    }

    /// <summary>
    /// Close the guest QR code modal
    /// </summary>
    [RelayCommand]
    private void CloseGuestQrModal()
    {
        IsGuestQrModalOpen = false;
        // Reload match to show the new guest
        _ = LoadMatchDataAsync();
    }

    /// <summary>
    /// Submit the add guest form
    /// </summary>
    [RelayCommand]
    private async Task SubmitAddGuestAsync()
    {
        if (IsAddingGuest || !IsMatchHost || Match == null)
            return;

        // Validate
        if (string.IsNullOrWhiteSpace(GuestDisplayName))
        {
            HasGuestError = true;
            GuestErrorMessage = "Ange gästens namn";
            return;
        }

        if (Match.IsHandicapEnabled && string.IsNullOrWhiteSpace(GuestHandicapClass))
        {
            HasGuestError = true;
            GuestErrorMessage = "Välj handikappklass för gästen";
            return;
        }

        try
        {
            IsAddingGuest = true;
            HasGuestError = false;

            var request = new Services.AddGuestRequest
            {
                DisplayName = GuestDisplayName.Trim(),
                HandicapClass = GuestHandicapClass
            };

            var result = await _matchService.AddGuestAsync(MatchCode, request);

            if (result.Success && result.Data != null)
            {
                // Close the add guest modal and settings modal
                IsAddGuestModalOpen = false;
                IsSettingsModalOpen = false;

                // Show the QR code modal
                AddedGuestName = result.Data.DisplayName;
                // Generate QR code URL using our server with separate code and token parameters
                // ClaimUrl format: https://domain/match/{code}/guest/{token}
                var claimUrl = result.Data.ClaimUrl;
                var token = claimUrl.Split('/').Last(); // Extract token from URL
                var cacheBuster = DateTime.UtcNow.Ticks;
                System.Diagnostics.Debug.WriteLine($"AddGuest: ClaimUrl from API = '{claimUrl}'");
                System.Diagnostics.Debug.WriteLine($"AddGuest: Extracted token = '{token}'");
                GuestClaimQrUrl = $"{_apiService.BaseUrl}/umbraco/surface/TrainingMatch/GetGuestClaimQrCode?code={MatchCode}&token={token}&_={cacheBuster}";
                System.Diagnostics.Debug.WriteLine($"AddGuest: GuestClaimQrUrl = '{GuestClaimQrUrl}'");
                IsGuestQrModalOpen = true;
            }
            else
            {
                HasGuestError = true;
                GuestErrorMessage = result.Message ?? "Kunde inte lägga till gästen";
            }
        }
        catch (Exception ex)
        {
            HasGuestError = true;
            GuestErrorMessage = ex.Message;
        }
        finally
        {
            IsAddingGuest = false;
        }
    }

    /// <summary>
    /// Show QR code for an existing guest (regenerates claim token if expired)
    /// </summary>
    [RelayCommand]
    private async Task ShowGuestQrAsync(TrainingMatchParticipant guest)
    {
        if (IsGuestOperationBusy || !IsMatchHost || Match == null || guest == null || !guest.GuestParticipantId.HasValue)
            return;

        try
        {
            IsGuestOperationBusy = true;
            HasGuestError = false;

            var result = await _matchService.RegenerateGuestQrAsync(MatchCode, guest.GuestParticipantId.Value);

            if (result.Success && result.Data != null)
            {
                // Close settings modal and show the QR code modal with regenerated token
                IsSettingsModalOpen = false;
                AddedGuestName = guest.DisplayName;
                // Extract token from ClaimUrl (format: https://domain/match/{code}/guest/{token})
                var token = result.Data.ClaimUrl.Split('/').Last();
                var cacheBuster = DateTime.UtcNow.Ticks;
                GuestClaimQrUrl = $"{_apiService.BaseUrl}/umbraco/surface/TrainingMatch/GetGuestClaimQrCode?code={MatchCode}&token={token}&_={cacheBuster}";
                System.Diagnostics.Debug.WriteLine($"ShowGuestQr: GuestClaimQrUrl = '{GuestClaimQrUrl}'");
                IsGuestQrModalOpen = true;
            }
            else
            {
                await Shell.Current.DisplayAlert("Fel", result.Message ?? "Kunde inte visa QR-koden", "OK");
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Fel", ex.Message, "OK");
        }
        finally
        {
            IsGuestOperationBusy = false;
        }
    }

    /// <summary>
    /// Remove a guest from the match
    /// </summary>
    [RelayCommand]
    private async Task RemoveGuestAsync(TrainingMatchParticipant guest)
    {
        if (IsGuestOperationBusy || !IsMatchHost || Match == null || guest == null || !guest.GuestParticipantId.HasValue)
            return;

        // Confirm deletion
        var confirm = await Shell.Current.DisplayAlert(
            "Ta bort gäst",
            $"Är du säker på att du vill ta bort {guest.DisplayName} från matchen?",
            "Ta bort",
            "Avbryt");

        if (!confirm)
            return;

        try
        {
            IsGuestOperationBusy = true;

            var result = await _matchService.RemoveGuestAsync(MatchCode, guest.GuestParticipantId.Value);

            if (result.Success)
            {
                // Remove from local collection
                GuestParticipants.Remove(guest);
                OnPropertyChanged(nameof(HasGuestParticipants));

                // Reload match data to update all views
                await LoadMatchDataAsync();
            }
            else
            {
                await Shell.Current.DisplayAlert("Fel", result.Message ?? "Kunde inte ta bort gästen", "OK");
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Fel", ex.Message, "OK");
        }
        finally
        {
            IsGuestOperationBusy = false;
        }
    }

    /// <summary>
    /// Populate GuestParticipants from the Participants collection
    /// </summary>
    private void UpdateGuestParticipants()
    {
        GuestParticipants.Clear();
        foreach (var participant in Participants.Where(p => p.IsGuest))
        {
            GuestParticipants.Add(participant);
        }
        OnPropertyChanged(nameof(HasGuestParticipants));
    }

    #endregion

    #region Member Claim (Forgot Password at Range)

    [ObservableProperty]
    private bool _isAddMemberModalOpen;

    [ObservableProperty]
    private bool _isMemberQrModalOpen;

    [ObservableProperty]
    private string _memberSearchText = string.Empty;

    [ObservableProperty]
    private bool _isSearchingMembers;

    [ObservableProperty]
    private ObservableCollection<MemberSearchResult> _memberSearchResults = new();

    [ObservableProperty]
    private MemberSearchResult? _selectedMember;

    [ObservableProperty]
    private bool _isCreatingMemberClaim;

    [ObservableProperty]
    private string _memberClaimQrUrl = string.Empty;

    [ObservableProperty]
    private string _addedMemberName = string.Empty;

    [ObservableProperty]
    private string? _addedMemberClub;

    [ObservableProperty]
    private bool _hasMemberClaimError;

    [ObservableProperty]
    private string _memberClaimErrorMessage = string.Empty;

    public bool HasSelectedMember => SelectedMember != null;

    /// <summary>
    /// Open the add member modal
    /// </summary>
    [RelayCommand]
    private void OpenAddMemberModal()
    {
        if (!IsMatchHost)
            return;

        // Reset form
        MemberSearchText = string.Empty;
        MemberSearchResults.Clear();
        SelectedMember = null;
        HasMemberClaimError = false;
        MemberClaimErrorMessage = string.Empty;
        IsAddMemberModalOpen = true;
    }

    /// <summary>
    /// Close the add member modal
    /// </summary>
    [RelayCommand]
    private void CloseAddMemberModal()
    {
        IsAddMemberModalOpen = false;
    }

    /// <summary>
    /// Close the member QR code modal
    /// </summary>
    [RelayCommand]
    private void CloseMemberQrModal()
    {
        IsMemberQrModalOpen = false;
    }

    /// <summary>
    /// Search for members by name
    /// </summary>
    [RelayCommand]
    private async Task SearchMembersAsync()
    {
        if (string.IsNullOrWhiteSpace(MemberSearchText) || MemberSearchText.Length < 2)
        {
            MemberSearchResults.Clear();
            return;
        }

        if (IsSearchingMembers)
            return;

        try
        {
            IsSearchingMembers = true;
            var result = await _matchService.SearchMembersAsync(MemberSearchText.Trim());

            MemberSearchResults.Clear();
            if (result.Success && result.Data != null)
            {
                foreach (var member in result.Data)
                {
                    MemberSearchResults.Add(member);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error searching members: {ex.Message}");
        }
        finally
        {
            IsSearchingMembers = false;
        }
    }

    /// <summary>
    /// Select a member from search results
    /// </summary>
    [RelayCommand]
    private void SelectMember(MemberSearchResult member)
    {
        SelectedMember = member;
        MemberSearchResults.Clear();
        MemberSearchText = string.Empty;
        OnPropertyChanged(nameof(HasSelectedMember));
    }

    /// <summary>
    /// Clear the selected member
    /// </summary>
    [RelayCommand]
    private void ClearSelectedMember()
    {
        SelectedMember = null;
        OnPropertyChanged(nameof(HasSelectedMember));
    }

    /// <summary>
    /// Create a member claim QR code
    /// </summary>
    [RelayCommand]
    private async Task CreateMemberClaimAsync()
    {
        if (IsCreatingMemberClaim || !IsMatchHost || Match == null || SelectedMember == null)
            return;

        try
        {
            IsCreatingMemberClaim = true;
            HasMemberClaimError = false;

            var result = await _matchService.CreateMemberClaimAsync(MatchCode, SelectedMember.Id);

            if (result.Success && result.Data != null)
            {
                // Close the add member modal
                IsAddMemberModalOpen = false;
                IsSettingsModalOpen = false;

                // Show the QR code modal
                AddedMemberName = result.Data.DisplayName;
                AddedMemberClub = result.Data.ClubName;

                // Generate QR code URL using server
                var apiService = _apiService as ApiService;
                var baseUrl = apiService?.BaseUrl?.TrimEnd('/') ?? "";
                // The claim URL is the full URL that users scan with their phone
                MemberClaimQrUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=200x200&data={Uri.EscapeDataString(result.Data.ClaimUrl)}";

                IsMemberQrModalOpen = true;
            }
            else
            {
                HasMemberClaimError = true;
                MemberClaimErrorMessage = result.Message ?? "Kunde inte skapa QR-kod";
            }
        }
        catch (Exception ex)
        {
            HasMemberClaimError = true;
            MemberClaimErrorMessage = $"Fel: {ex.Message}";
        }
        finally
        {
            IsCreatingMemberClaim = false;
        }
    }

    #endregion

    /// <summary>
    /// Copy match code to clipboard (for sharing)
    /// </summary>
    [RelayCommand]
    private async Task CopyMatchCodeAsync()
    {
        if (Match == null)
            return;

        await Clipboard.Default.SetTextAsync(Match.MatchCode);
        await Shell.Current.DisplayAlert("Kopierat", $"Matchkod {Match.MatchCode} kopierad till urklipp", "OK");
    }

    /// <summary>
    /// Copy share link to clipboard
    /// </summary>
    [RelayCommand]
    private async Task CopyShareLinkAsync()
    {
        if (Match == null)
            return;

        await Clipboard.Default.SetTextAsync(ShareLink);
        await Shell.Current.DisplayAlert("Kopierat", "Länk kopierad till urklipp", "OK");
    }

    /// <summary>
    /// Open reaction modal for a cell with a score, or edit mode for own scores without photo
    /// </summary>
    [RelayCommand]
    private void OpenPhotoViewer(ScoreboardCell cell)
    {
        if (cell == null)
            return;

        // Open reaction modal if:
        // - Cell has a score AND (has photo OR is another user's cell)
        // This allows viewing/reacting to any series with a score, showing photo or score display
        if (cell.HasScore && (cell.HasPhoto || !cell.IsCurrentUserCell))
        {
            System.Diagnostics.Debug.WriteLine($"OpenPhotoViewer: Opening for member {cell.MemberId}, series {cell.SeriesNumber}, HasPhoto={cell.HasPhoto}");

            // Find the participant to get their name
            var participant = Participants.FirstOrDefault(p => p.MemberId == cell.MemberId);
            var shooterName = participant?.DisplayName ?? "Okänd";

            // Set photo URL (may be null if no photo)
            ViewingPhotoUrl = cell.TargetPhotoUrl;
            ViewingPhotoShooterName = shooterName;
            ViewingPhotoSeriesNumber = cell.SeriesNumber;
            ViewingPhotoMemberId = cell.MemberId;

            // Set score display properties
            ViewingSeriesScore = cell.HasScore && int.TryParse(cell.ScoreText, out var score) ? score : 0;
            ViewingSeriesXCount = cell.HasXCount && int.TryParse(cell.XCountText?.Replace("x", ""), out var xCount) ? xCount : 0;
            ViewingSeriesShots = cell.Shots;
            OnPropertyChanged(nameof(ViewingShotsDisplay));

            // Get reactions from the score
            ViewingPhotoReactions.Clear();
            if (participant?.Scores != null)
            {
                var scoreData = participant.Scores.FirstOrDefault(s => s.SeriesNumber == cell.SeriesNumber);
                if (scoreData?.Reactions != null)
                {
                    foreach (var reaction in scoreData.Reactions)
                    {
                        ViewingPhotoReactions.Add(reaction);
                    }
                }
            }

            // Find current user's reaction
            var currentUserId = _authService.CurrentUser?.MemberId;
            CurrentUserReaction = ViewingPhotoReactions.FirstOrDefault(r => r.MemberId == currentUserId)?.Emoji;

            IsPhotoViewerOpen = true;
        }
        // If no photo but can edit (current user's cell), open edit mode
        else if (cell.CanEdit)
        {
            OpenEditScore(cell);
        }
    }

    /// <summary>
    /// Close the photo viewer modal
    /// </summary>
    [RelayCommand]
    private void ClosePhotoViewer()
    {
        IsPhotoViewerOpen = false;
        ViewingPhotoUrl = null;
        ViewingPhotoShooterName = null;
        ViewingPhotoSeriesNumber = 0;
        ViewingPhotoMemberId = 0;
        ViewingPhotoReactions.Clear();
        CurrentUserReaction = null;
        // Reset score display properties
        ViewingSeriesScore = 0;
        ViewingSeriesXCount = 0;
        ViewingSeriesShots = null;
    }

    /// <summary>
    /// Edit the score from the photo viewer (for own scores)
    /// </summary>
    [RelayCommand]
    private void EditFromPhotoViewer()
    {
        var currentUserId = _authService.CurrentUser?.MemberId;
        if (!currentUserId.HasValue || ViewingPhotoMemberId != currentUserId.Value)
            return;

        // Find the cell to open edit mode
        var participant = Participants.FirstOrDefault(p => p.MemberId == ViewingPhotoMemberId);
        if (participant?.Scores == null)
            return;

        var score = participant.Scores.FirstOrDefault(s => s.SeriesNumber == ViewingPhotoSeriesNumber);
        if (score == null)
            return;

        // Create a cell for the edit command
        var cell = new ScoreboardCell
        {
            SeriesNumber = ViewingPhotoSeriesNumber,
            MemberId = ViewingPhotoMemberId,
            IsCurrentUserCell = true,
            CanEdit = true,
            TargetPhotoUrl = score.TargetPhotoUrl,
            Shots = score.Shots,
            EntryMethod = score.EntryMethod
        };

        // Close photo viewer and open edit mode
        IsPhotoViewerOpen = false;
        OpenEditScore(cell);
    }

    /// <summary>
    /// Check if current user owns the photo being viewed
    /// </summary>
    public bool IsCurrentUserPhotoOwner =>
        _authService.CurrentUser?.MemberId > 0 &&
        ViewingPhotoMemberId == _authService.CurrentUser.MemberId &&
        IsMatchActive &&
        !IsSpectator;

    /// <summary>
    /// Add or toggle a reaction to the currently viewed photo
    /// </summary>
    [RelayCommand]
    private async Task AddReactionAsync(string emoji)
    {
        if (IsSendingReaction || ViewingPhotoMemberId == 0 || string.IsNullOrEmpty(emoji))
            return;

        System.Diagnostics.Debug.WriteLine($"AddReactionAsync: Adding {emoji} to member {ViewingPhotoMemberId}, series {ViewingPhotoSeriesNumber}");

        try
        {
            IsSendingReaction = true;

            var result = await _matchService.AddReactionAsync(
                MatchCode,
                ViewingPhotoSeriesNumber,
                ViewingPhotoMemberId,
                emoji);

            if (result.Success && result.Data != null)
            {
                // Update local reactions
                ViewingPhotoReactions.Clear();
                foreach (var reaction in result.Data)
                {
                    ViewingPhotoReactions.Add(reaction);
                }

                // Update current user's reaction
                var currentUserId = _authService.CurrentUser?.MemberId;
                CurrentUserReaction = ViewingPhotoReactions.FirstOrDefault(r => r.MemberId == currentUserId)?.Emoji;

                System.Diagnostics.Debug.WriteLine($"AddReactionAsync: Success! Reactions count={result.Data.Count}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"AddReactionAsync: Failed - {result.Message}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddReactionAsync: Exception - {ex.Message}");
        }
        finally
        {
            IsSendingReaction = false;
        }
    }

    [RelayCommand]
    private async Task LoadMatchAsync()
    {
        if (IsBusy || string.IsNullOrEmpty(MatchCode))
            return;

        try
        {
            IsBusy = true;
            HasError = false;
            await LoadMatchDataAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Internal method to load match data without IsBusy checks
    /// </summary>
    private async Task LoadMatchDataAsync()
    {
        if (IsSpectator)
        {
            // Load as spectator (read-only view)
            var spectatorResult = await _matchService.ViewMatchAsSpectatorAsync(MatchCode);

            if (spectatorResult.Success && spectatorResult.Data != null)
            {
                Match = spectatorResult.Data.Match;
                Title = $"Match: {Match.MatchCode} (Visning)";
                IsMatchActive = Match.IsActive;
                CanJoin = spectatorResult.Data.CanJoin;

                // Calculate equalized series count BEFORE adding to collection
                // (TrainingMatchParticipant doesn't implement INotifyPropertyChanged,
                // so we must set this before bindings evaluate)
                var participantsWithScores = Match.Participants
                    .Where(p => p.Scores != null && p.Scores.Count > 0)
                    .ToList();
                var minSeriesCount = participantsWithScores.Count > 0
                    ? participantsWithScores.Min(p => p.Scores.Count)
                    : 0;

                Participants.Clear();
                foreach (var participant in Match.Participants)
                {
                    // Calculate effective series limit for this participant
                    // If MaxSeriesCount is set, use it as limit; otherwise use minimum among all participants
                    var actualSeriesCount = participant.Scores?.Count ?? 0;
                    int? effectiveLimit;
                    if (Match.MaxSeriesCount.HasValue)
                    {
                        // Max is set - each participant limited to max or their actual, whichever is less
                        effectiveLimit = Math.Min(Match.MaxSeriesCount.Value, actualSeriesCount);
                    }
                    else
                    {
                        // No max - use current minimum behavior
                        effectiveLimit = minSeriesCount > 0 ? Math.Min(minSeriesCount, actualSeriesCount) : (int?)null;
                    }
                    participant.EqualizedSeriesCount = effectiveLimit > 0 ? effectiveLimit : null;
                    Participants.Add(participant);
                }

                // Spectators are not participants - clear participant state
                CurrentParticipant = null;
                IsMatchHost = false;
                CurrentSeriesIndex = 0;

                // Join SignalR group for this match (for live updates)
                await _signalRService.JoinMatchAsync(MatchCode);

                // Register as spectator so others can see us viewing
                var currentUser = _authService.CurrentUser;
                if (currentUser != null)
                {
                    await _signalRService.RegisterSpectatorAsync(
                        MatchCode,
                        currentUser.MemberId,
                        currentUser.DisplayName,
                        currentUser.ProfilePictureUrl ?? "",
                        "" // ClubName - not available in UserInfo
                    );
                    System.Diagnostics.Debug.WriteLine($"LoadMatchDataAsync (spectator): Registered as spectator for match {MatchCode}");
                }

                // Update team data if this is a team match
                IsTeamMatch = Match.IsTeamMatch;
                Teams.Clear();
                if (Match.Teams != null)
                {
                    foreach (var team in Match.Teams)
                    {
                        Teams.Add(team);
                    }
                }
                UpdateTeamRankings();

                // Update scoreboard rows for dynamic display
                UpdateScoreboardRows();

                // Update guest participants list (for settings modal)
                UpdateGuestParticipants();

                System.Diagnostics.Debug.WriteLine($"LoadMatchDataAsync (spectator): Loaded match {MatchCode}, CanJoin={CanJoin}, IsTeamMatch={IsTeamMatch}");
            }
            else
            {
                ErrorMessage = spectatorResult.Message ?? "Kunde inte ladda match";
                HasError = true;
            }
        }
        else
        {
            // Load as participant
            var result = await _matchService.GetMatchAsync(MatchCode);

            if (result.Success && result.Data != null)
            {
                Match = result.Data;
                Title = $"Match: {Match.MatchCode}";
                IsMatchActive = Match.IsActive;

                // Calculate equalized series count BEFORE adding to collection
                // (TrainingMatchParticipant doesn't implement INotifyPropertyChanged,
                // so we must set this before bindings evaluate)
                var participantsWithScores = Match.Participants
                    .Where(p => p.Scores != null && p.Scores.Count > 0)
                    .ToList();
                var minSeriesCount = participantsWithScores.Count > 0
                    ? participantsWithScores.Min(p => p.Scores.Count)
                    : 0;

                Participants.Clear();
                foreach (var participant in Match.Participants)
                {
                    // Calculate effective series limit for this participant
                    // If MaxSeriesCount is set, use it as limit; otherwise use minimum among all participants
                    var actualSeriesCount = participant.Scores?.Count ?? 0;
                    int? effectiveLimit;
                    if (Match.MaxSeriesCount.HasValue)
                    {
                        // Max is set - each participant limited to max or their actual, whichever is less
                        effectiveLimit = Math.Min(Match.MaxSeriesCount.Value, actualSeriesCount);
                    }
                    else
                    {
                        // No max - use current minimum behavior
                        effectiveLimit = minSeriesCount > 0 ? Math.Min(minSeriesCount, actualSeriesCount) : (int?)null;
                    }
                    participant.EqualizedSeriesCount = effectiveLimit > 0 ? effectiveLimit : null;
                    Participants.Add(participant);
                }

                // Find current user's participant
                var userId = _authService.CurrentUser?.MemberId;
                if (userId.HasValue)
                {
                    CurrentParticipant = Participants.FirstOrDefault(p => p.MemberId == userId.Value);
                    IsMatchHost = Match.HostMemberId == userId.Value;

                    // Set CurrentSeriesIndex to continue from where user left off
                    if (CurrentParticipant?.Scores != null && CurrentParticipant.Scores.Count > 0)
                    {
                        CurrentSeriesIndex = CurrentParticipant.Scores.Count;
                        System.Diagnostics.Debug.WriteLine($"LoadMatchDataAsync: User has {CurrentParticipant.Scores.Count} series, starting at series {CurrentSeriesIndex + 1}");
                    }
                    else
                    {
                        CurrentSeriesIndex = 0;
                        System.Diagnostics.Debug.WriteLine("LoadMatchDataAsync: User has no scores, starting at series 1");
                    }
                }

                // Join SignalR group for this match
                await _signalRService.JoinMatchAsync(MatchCode);

                // If user is match host, also join organizer group to receive join requests
                if (IsMatchHost)
                {
                    await _signalRService.JoinOrganizerGroupAsync(MatchCode);
                    System.Diagnostics.Debug.WriteLine($"LoadMatchDataAsync: Joined organizer group for match {MatchCode}");
                }

                // Update team data if this is a team match
                IsTeamMatch = Match.IsTeamMatch;
                Teams.Clear();
                if (Match.Teams != null)
                {
                    foreach (var team in Match.Teams)
                    {
                        Teams.Add(team);
                    }
                }
                UpdateTeamRankings();

                // Update scoreboard rows for dynamic display
                UpdateScoreboardRows();

                // Update guest participants list (for settings modal)
                UpdateGuestParticipants();
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte ladda match";
                HasError = true;
            }
        }
    }

    /// <summary>
    /// Clear the currently selected shot
    /// </summary>
    [RelayCommand]
    private void ClearCurrentShot()
    {
        switch (CurrentShotIndex)
        {
            case 0: Shot1 = "-"; break;
            case 1: Shot2 = "-"; break;
            case 2: Shot3 = "-"; break;
            case 3: Shot4 = "-"; break;
            case 4: Shot5 = "-"; break;
        }
        OnPropertyChanged(nameof(SeriesTotal));
        OnPropertyChanged(nameof(SeriesXCount));
    }

    /// <summary>
    /// Save score without taking a photo
    /// </summary>
    [RelayCommand]
    private async Task SaveScoreOnlyAsync()
    {
        await SaveScoreInternalAsync(capturePhoto: false);
    }

    /// <summary>
    /// Save score and then capture a photo
    /// </summary>
    [RelayCommand]
    private async Task SaveScoreWithPhotoAsync()
    {
        await SaveScoreInternalAsync(capturePhoto: true);
    }

    /// <summary>
    /// Internal save score method with optional photo capture
    /// </summary>
    private async Task SaveScoreInternalAsync(bool capturePhoto)
    {
        // Debug: Log that save was clicked
        System.Diagnostics.Debug.WriteLine($"SaveScoreInternalAsync called. IsBusy={IsBusy}, capturePhoto={capturePhoto}, CurrentParticipant={CurrentParticipant?.DisplayName ?? "null"}");

        if (IsBusy)
        {
            System.Diagnostics.Debug.WriteLine("SaveScoreInternalAsync: Returning early because IsBusy=true");
            return;
        }

        if (CurrentParticipant == null)
        {
            var userId = _authService.CurrentUser?.MemberId;
            ErrorMessage = $"Du är inte registrerad som deltagare (MemberId: {userId})";
            HasError = true;
            System.Diagnostics.Debug.WriteLine($"SaveScoreInternalAsync: CurrentParticipant is null. User MemberId={userId}");
            return;
        }

        // Validate at least one shot is entered
        if (Shot1 == "-" && Shot2 == "-" && Shot3 == "-" && Shot4 == "-" && Shot5 == "-")
        {
            ErrorMessage = "Ange minst ett skott";
            HasError = true;
            System.Diagnostics.Debug.WriteLine("SaveScoreInternalAsync: No shots entered");
            return;
        }

        try
        {
            IsBusy = true;
            HasError = false;
            System.Diagnostics.Debug.WriteLine($"SaveScoreInternalAsync: Saving series {CurrentSeriesIndex + 1}, Total={SeriesTotal}, XCount={SeriesXCount}");

            // Build the shots list from shot circles (exclude empty "-")
            var shots = new List<string> { Shot1, Shot2, Shot3, Shot4, Shot5 }
                .Where(s => s != "-")
                .ToList();

            var request = new SaveScoreRequest
            {
                SeriesNumber = CurrentSeriesIndex + 1,
                Total = SeriesTotal,
                XCount = SeriesXCount,
                Shots = shots,
                EntryMethod = "ShotByShot"
            };

            var result = await _matchService.SaveScoreAsync(MatchCode, request);
            System.Diagnostics.Debug.WriteLine($"SaveScoreInternalAsync: API result Success={result.Success}, Message={result.Message}");

            if (result.Success)
            {
                System.Diagnostics.Debug.WriteLine($"SaveScoreInternalAsync: Success! IsEditMode={IsEditMode}, capturePhoto={capturePhoto}");
                // Update local data
                UpdateLocalScore(CurrentParticipant.Id, request.SeriesNumber, 0, SeriesTotal);

                // Store the saved series number for photo upload
                _savedSeriesNumber = request.SeriesNumber;

                if (capturePhoto)
                {
                    // User wants to capture a photo - do it now
                    IsBusy = false; // Allow UI to update before camera
                    await CaptureAndUploadPhotoAsync();
                }
                else
                {
                    // Just save without photo - advance to next series or close
                    if (IsEditMode)
                    {
                        // Edit mode: close panel
                        IsScoreEntryOpen = false;
                        IsEditMode = false;
                        EditingSeriesNumber = 0;
                        EditingSeriesPhotoUrl = null;
                    }
                    else
                    {
                        // New entry mode: advance to next series
                        AdvanceToNextSeries();
                    }
                }

                // Reload match data to get updated scores (do this in background)
                _ = LoadMatchDataAsync();
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte spara poäng";
                HasError = true;
                System.Diagnostics.Debug.WriteLine($"SaveScoreInternalAsync: Failed - {ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fel: {ex.Message}";
            HasError = true;
            System.Diagnostics.Debug.WriteLine($"SaveScoreInternalAsync: Exception - {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Capture photo and upload it after saving score
    /// </summary>
    private async Task CaptureAndUploadPhotoAsync()
    {
        System.Diagnostics.Debug.WriteLine($"CaptureAndUploadPhotoAsync: Starting capture for series {_savedSeriesNumber}");

        try
        {
            // Check camera permission
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Camera>();
                if (status != PermissionStatus.Granted)
                {
                    await Shell.Current.DisplayAlert("Kamerabehörighet",
                        "Kamerabehörighet krävs för att ta bilder. Gå till Inställningar för att aktivera.", "OK");
                    AdvanceOrCloseAfterSave();
                    return;
                }
            }

            var photo = await MediaPicker.Default.CapturePhotoAsync();

            if (photo == null)
            {
                System.Diagnostics.Debug.WriteLine("CaptureAndUploadPhotoAsync: User cancelled photo capture");
                AdvanceOrCloseAfterSave();
                return;
            }

            System.Diagnostics.Debug.WriteLine($"CaptureAndUploadPhotoAsync: Photo captured at {photo.FullPath}");

            IsUploadingPhoto = true;

            // Compress the image before uploading
            byte[] imageData;
            try
            {
                using var sourceStream = await photo.OpenReadAsync();
                imageData = await _imageCompressionService.CompressImageAsync(sourceStream);
                System.Diagnostics.Debug.WriteLine($"CaptureAndUploadPhotoAsync: Image compressed to {imageData.Length} bytes");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CaptureAndUploadPhotoAsync: Compression failed, using original - {ex.Message}");
                using var sourceStream = await photo.OpenReadAsync();
                using var memStream = new MemoryStream();
                await sourceStream.CopyToAsync(memStream);
                imageData = memStream.ToArray();
            }

            // Upload the photo
            var result = await _matchService.UploadSeriesPhotoAsync(MatchCode, _savedSeriesNumber, imageData);

            if (result.Success)
            {
                System.Diagnostics.Debug.WriteLine($"CaptureAndUploadPhotoAsync: Upload success! URL={result.Data?.PhotoUrl}");
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte ladda upp bilden";
                HasError = true;
                System.Diagnostics.Debug.WriteLine($"CaptureAndUploadPhotoAsync: Upload failed - {ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Fel vid fotohantering: {ex.Message}";
            HasError = true;
            System.Diagnostics.Debug.WriteLine($"CaptureAndUploadPhotoAsync: Exception - {ex}");
        }
        finally
        {
            IsUploadingPhoto = false;
            AdvanceOrCloseAfterSave();
        }
    }

    /// <summary>
    /// Advance to next series or close panel after saving (helper method)
    /// </summary>
    private void AdvanceOrCloseAfterSave()
    {
        if (IsEditMode)
        {
            // Edit mode: close panel
            IsScoreEntryOpen = false;
            IsEditMode = false;
            EditingSeriesNumber = 0;
            EditingSeriesPhotoUrl = null;
        }
        else
        {
            // New entry mode: advance to next series
            AdvanceToNextSeries();
        }
    }

    /// <summary>
    /// Advance to the next series after saving (closes the panel)
    /// </summary>
    private void AdvanceToNextSeries()
    {
        // Clear shots for next series
        Shot1 = Shot2 = Shot3 = Shot4 = Shot5 = "-";
        CurrentShotIndex = 0;

        // Advance series index
        CurrentSeriesIndex++;

        OnPropertyChanged(nameof(SeriesTotal));
        OnPropertyChanged(nameof(SeriesXCount));

        // Close the score entry panel after saving
        IsScoreEntryOpen = false;
    }

    [RelayCommand]
    private async Task SaveScoreAsync()
    {
        // Legacy method - redirect to save only
        await SaveScoreOnlyAsync();
    }

    [RelayCommand]
    private void SetScore(string scoreStr)
    {
        if (int.TryParse(scoreStr, out int score))
        {
            CurrentScore = score;
            IsX = false;
            // Update shot circle
            SetCurrentShot(scoreStr);
            // Auto-advance to next empty shot
            AutoAdvanceToNextShot();
        }
    }

    [RelayCommand]
    private void SetX()
    {
        CurrentScore = 10;
        IsX = true;
        // Update shot circle with X
        SetCurrentShot("X");
        // Auto-advance to next empty shot
        AutoAdvanceToNextShot();
    }

    [RelayCommand]
    private void SelectShot(int index)
    {
        if (index >= 0 && index < 5)
        {
            CurrentShotIndex = index;
            // Load the existing value if any
            var shot = GetCurrentShot();
            if (shot == "X")
            {
                CurrentScore = 10;
                IsX = true;
            }
            else if (int.TryParse(shot, out int val))
            {
                CurrentScore = val;
                IsX = false;
            }
            else
            {
                CurrentScore = 0;
                IsX = false;
            }
        }
    }

    [RelayCommand]
    private void ClearShots()
    {
        ClearAllShots();
    }

    private void AutoAdvanceToNextShot()
    {
        // Find the next empty shot slot (or stay at current if all filled)
        for (int i = 0; i < 5; i++)
        {
            int nextIndex = (CurrentShotIndex + 1 + i) % 5;
            var shot = nextIndex switch
            {
                0 => Shot1,
                1 => Shot2,
                2 => Shot3,
                3 => Shot4,
                4 => Shot5,
                _ => "-"
            };
            if (shot == "-")
            {
                CurrentShotIndex = nextIndex;
                CurrentScore = 0;
                IsX = false;
                return;
            }
        }
        // All shots filled - stay at current or move to first
        if (CurrentShotIndex < 4)
            CurrentShotIndex++;
    }

    [RelayCommand]
    private async Task CompleteMatchAsync()
    {
        if (IsBusy || !IsMatchHost)
            return;

        try
        {
            IsBusy = true;

            var result = await _matchService.CompleteMatchAsync(MatchCode);

            if (result.Success)
            {
                await Shell.Current.GoToAsync("//main/matches");
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte avsluta match";
                HasError = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LeaveMatchAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;

            await _signalRService.LeaveMatchAsync(MatchCode);
            await _matchService.LeaveMatchAsync(MatchCode);
            await Shell.Current.GoToAsync("//main/matches");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Join the match from spectator mode (only visible when CanJoinFromSpectator is true)
    /// </summary>
    [RelayCommand]
    private async Task JoinFromSpectatorAsync()
    {
        if (IsBusy || !IsSpectator || !CanJoin || Match == null)
            return;

        try
        {
            IsBusy = true;
            HasError = false;

            if (Match.IsOpen)
            {
                // Open match - join directly
                var result = await _matchService.JoinMatchAsync(MatchCode);
                if (result.Success)
                {
                    // Switch from spectator to participant mode
                    IsSpectator = false;
                    CanJoin = false;
                    await LoadMatchDataAsync();
                }
                else
                {
                    ErrorMessage = result.Message ?? "Kunde inte gå med i matchen";
                    HasError = true;
                }
            }
            else
            {
                // Private match - send join request
                var result = await _matchService.RequestJoinMatchAsync(MatchCode);
                if (result.Success)
                {
                    await Shell.Current.DisplayAlert("Förfrågan skickad",
                        "Din förfrågan har skickats till matchvärden", "OK");
                }
                else
                {
                    ErrorMessage = result.Message ?? "Kunde inte skicka förfrågan";
                    HasError = true;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateLocalScore(int participantId, int seriesNumber, int shotNumber, int score)
    {
        var participant = Participants.FirstOrDefault(p => p.Id == participantId);
        if (participant == null)
            return;

        var series = participant.Scores.FirstOrDefault(s => s.SeriesNumber == seriesNumber);
        if (series == null)
        {
            series = new TrainingMatchScore { SeriesNumber = seriesNumber };
            participant.Scores.Add(series);
        }

        // Add or update shot score - simplified for now
        // In a real implementation, we would update the Shots list and recalculate Total

        // Refresh scoreboard rows to reflect the updated score
        UpdateScoreboardRows();
    }

    private void AdvanceToNextShot()
    {
        if (Match == null)
            return;

        CurrentShotIndex++;
        if (CurrentShotIndex >= Match.ShotsPerSeries)
        {
            CurrentShotIndex = 0;
            CurrentSeriesIndex++;
        }
    }

    private void OnScoreUpdated(object? sender, ScoreUpdate update)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // Reload match data to get updated scores
            System.Diagnostics.Debug.WriteLine($"SignalR: ScoreUpdated - MemberId={update.MemberId}, Series={update.SeriesNumber}");
            await LoadMatchDataAsync();
        });
    }

    private void OnParticipantJoined(object? sender, int memberId)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR: ParticipantJoined - MemberId={memberId}");
            await LoadMatchDataAsync();
        });
    }

    private void OnParticipantLeft(object? sender, int memberId)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR: ParticipantLeft - MemberId={memberId}");
            await LoadMatchDataAsync();
        });
    }

    private void OnMatchCompleted(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            System.Diagnostics.Debug.WriteLine("SignalR: MatchCompleted");
            IsMatchActive = false;
        });
    }

    private void OnSpectatorListUpdated(object? sender, List<MatchSpectator> spectators)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Get current user's member ID to exclude from spectators list
            var currentUserId = _authService.CurrentUser?.MemberId;

            // Filter out the current user - they shouldn't see themselves as a spectator
            var otherSpectators = spectators
                .Where(s => !currentUserId.HasValue || s.MemberId != currentUserId.Value)
                .ToList();

            System.Diagnostics.Debug.WriteLine($"SignalR: SpectatorListUpdated - Total={spectators.Count}, Showing={otherSpectators.Count} (excluded self)");
            Spectators.Clear();
            foreach (var s in otherSpectators)
            {
                Spectators.Add(s);
            }
        });
    }

    private void OnJoinRequestReceived(object? sender, JoinRequest request)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR: JoinRequestReceived - RequestId={request.RequestId}, MemberId={request.MemberId}, Name={request.MemberName}, MatchCode={request.MatchCode}");
            System.Diagnostics.Debug.WriteLine($"SignalR: JoinRequest raw object - {System.Text.Json.JsonSerializer.Serialize(request)}");

            // Only show to match host
            if (!IsMatchHost)
            {
                System.Diagnostics.Debug.WriteLine("Not match host, ignoring join request");
                return;
            }

            // Show dialog to accept or block
            var action = await Shell.Current.DisplayActionSheet(
                $"{request.MemberName} vill gå med i matchen",
                "Avbryt",
                null,
                "Godkänn",
                "Neka");

            if (action == "Godkänn")
            {
                await RespondToJoinRequestAsync(request.RequestId, "Accept");
            }
            else if (action == "Neka")
            {
                await RespondToJoinRequestAsync(request.RequestId, "Block");
            }
        });
    }

    private void OnJoinRequestAccepted(object? sender, string matchCode)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR: JoinRequestAccepted - MatchCode={matchCode}");

            // Check if this is for current match and current user was the requester (spectator)
            if (matchCode == MatchCode && IsSpectator)
            {
                await Shell.Current.DisplayAlert("Förfrågan godkänd",
                    "Du har blivit godkänd att delta i matchen!", "OK");

                // Switch from spectator to participant mode
                IsSpectator = false;
                CanJoin = false;
                await LoadMatchDataAsync();
            }
        });
    }

    private void OnJoinRequestBlocked(object? sender, string matchCode)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR: JoinRequestBlocked - MatchCode={matchCode}");

            // Check if this is for current match and current user was the requester (spectator)
            if (matchCode == MatchCode && IsSpectator)
            {
                await Shell.Current.DisplayAlert("Förfrågan nekad",
                    "Matchvärden har nekat din förfrågan att gå med.", "OK");
            }
        });
    }

    private void OnReactionUpdated(object? sender, ReactionUpdate update)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR: ReactionUpdated - TargetMemberId={update.TargetMemberId}, Series={update.SeriesNumber}, Reactions={update.Reactions?.Count ?? 0}");

            // Update the participant's score with new reactions
            var participant = Participants.FirstOrDefault(p => p.MemberId == update.TargetMemberId);
            if (participant?.Scores != null)
            {
                var score = participant.Scores.FirstOrDefault(s => s.SeriesNumber == update.SeriesNumber);
                if (score != null)
                {
                    score.Reactions = update.Reactions;
                }
            }

            // Refresh scoreboard to update reaction indicators on cells
            UpdateScoreboardRows();

            // If viewing this series, update the displayed reactions
            if (IsPhotoViewerOpen &&
                ViewingPhotoMemberId == update.TargetMemberId &&
                ViewingPhotoSeriesNumber == update.SeriesNumber)
            {
                ViewingPhotoReactions.Clear();
                if (update.Reactions != null)
                {
                    foreach (var reaction in update.Reactions)
                    {
                        ViewingPhotoReactions.Add(reaction);
                    }
                }

                // Update current user's reaction
                var currentUserId = _authService.CurrentUser?.MemberId;
                CurrentUserReaction = ViewingPhotoReactions.FirstOrDefault(r => r.MemberId == currentUserId)?.Emoji;
            }
        });
    }

    private void OnSettingsUpdated(object? sender, SettingsUpdate update)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            System.Diagnostics.Debug.WriteLine($"SignalR: SettingsUpdated - MaxSeriesCount={update.MaxSeriesCount}");

            // Reload match data to get updated settings and recalculate totals
            await LoadMatchDataAsync();
        });
    }

    private void OnTeamScoreUpdated(object? sender, object teamScores)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            System.Diagnostics.Debug.WriteLine("SignalR: TeamScoreUpdated");

            // Reload match data to get updated team scores
            await LoadMatchDataAsync();
        });
    }

    /// <summary>
    /// Toggle between individual and team score views
    /// </summary>
    [RelayCommand]
    private void ToggleTeamScoresView()
    {
        ShowTeamScores = !ShowTeamScores;
    }

    /// <summary>
    /// Updates team rankings based on current team scores
    /// </summary>
    private void UpdateTeamRankings()
    {
        if (!IsTeamMatch || Teams == null || Teams.Count == 0)
        {
            TeamRankings.Clear();
            return;
        }

        var sortedTeams = Teams
            .OrderByDescending(t => t.AdjustedTeamScore)
            .ThenByDescending(t => t.TotalXCount)
            .ToList();

        // Assign ranks
        for (int i = 0; i < sortedTeams.Count; i++)
        {
            sortedTeams[i].Rank = i + 1;
        }

        TeamRankings.Clear();
        foreach (var team in sortedTeams)
        {
            TeamRankings.Add(team);
        }
    }

    private async Task RespondToJoinRequestAsync(int requestId, string action)
    {
        try
        {
            var result = await _matchService.RespondToJoinRequestAsync(requestId, action);
            if (result.Success)
            {
                System.Diagnostics.Debug.WriteLine($"Successfully responded to join request: {action}");
            }
            else
            {
                ErrorMessage = result.Message ?? "Kunde inte svara på förfrågan";
                HasError = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
    }

    /// <summary>
    /// Collection of scoreboard rows for dynamic display with subtotals
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ScoreboardRow> _scoreboardRows = new();

    /// <summary>
    /// Updates the scoreboard rows based on current participant scores.
    /// Adds subtotal rows after series 6, 7, 10, and 12.
    /// Also updates participant rankings.
    /// </summary>
    public void UpdateScoreboardRows()
    {
        ScoreboardRows.Clear();

        // Update rankings based on current scores
        UpdateParticipantRankings();

        // Determine max series from participants (minimum 6)
        int maxSeries = MaxSeriesCount;

        // Series indices that should have a subtotal row AFTER them (0-based)
        // After series 6 (index 5), 7 (index 6), 10 (index 9), 12 (index 11)
        var subtotalAfterIndices = new HashSet<int> { 5, 6, 9, 11 };

        for (int i = 0; i < maxSeries; i++)
        {
            // Add score row for this series
            var scoreRow = new ScoreboardRow
            {
                SeriesIndex = i,
                RowType = ScoreboardRowType.Score
            };
            PopulateRowCells(scoreRow, i);
            ScoreboardRows.Add(scoreRow);

            // Add subtotal row if applicable and we haven't exceeded the series
            if (subtotalAfterIndices.Contains(i))
            {
                var subtotalRow = new ScoreboardRow
                {
                    SeriesIndex = i,
                    RowType = ScoreboardRowType.Subtotal,
                    SubtotalUpToSeries = i + 1  // 1-based count of series included
                };
                PopulateSubtotalCells(subtotalRow, i + 1);
                ScoreboardRows.Add(subtotalRow);
            }
        }
    }

    /// <summary>
    /// Populates cells for a score row (one cell per participant)
    /// </summary>
    private void PopulateRowCells(ScoreboardRow row, int seriesIndex)
    {
        row.Cells.Clear();
        var currentUserId = _authService.CurrentUser?.MemberId;

        foreach (var participant in Participants)
        {
            var cell = new ScoreboardCell
            {
                SeriesNumber = seriesIndex + 1, // 1-based
                MemberId = participant.MemberId ?? 0, // Use 0 for guests
                IsCurrentUserCell = currentUserId.HasValue && participant.MemberId.HasValue && participant.MemberId.Value == currentUserId.Value
            };

            if (participant.Scores != null && seriesIndex < participant.Scores.Count)
            {
                var score = participant.Scores[seriesIndex];
                // Cap at 50 (max possible per series)
                cell.ScoreText = Math.Min(score.Total, 50).ToString();
                cell.HasScore = true;
                cell.BackgroundColorHex = "#1e5631"; // Dark green for scores

                if (score.XCount > 0)
                {
                    cell.XCountText = $"{score.XCount}x";
                    cell.HasXCount = true;
                }

                // Store additional data for editing and viewing
                cell.TargetPhotoUrl = score.TargetPhotoUrl;
                cell.Shots = score.Shots;
                cell.EntryMethod = score.EntryMethod;
                cell.Reactions = score.Reactions;

                // Debug logging for photo URL
                if (!string.IsNullOrEmpty(score.TargetPhotoUrl))
                {
                    System.Diagnostics.Debug.WriteLine($"[Scoreboard] Member {participant.MemberId} Series {seriesIndex + 1} has photo: {score.TargetPhotoUrl}");
                }

                // Can edit if: current user's cell + match is active
                cell.CanEdit = cell.IsCurrentUserCell && IsMatchActive && !IsSpectator;
            }
            else
            {
                cell.ScoreText = "-";
                cell.HasScore = false;
                cell.BackgroundColorHex = "#374151"; // Gray for empty
                cell.CanEdit = false;
            }

            row.Cells.Add(cell);
        }
    }

    /// <summary>
    /// Populates cells for a subtotal row (sum up to seriesCount)
    /// </summary>
    private void PopulateSubtotalCells(ScoreboardRow row, int seriesCount)
    {
        row.Cells.Clear();
        foreach (var participant in Participants)
        {
            var cell = new ScoreboardCell();
            int total = 0;
            int totalX = 0;

            if (participant.Scores != null)
            {
                int actualCount = Math.Min(seriesCount, participant.Scores.Count);
                for (int i = 0; i < actualCount; i++)
                {
                    total += Math.Min(participant.Scores[i].Total, 50);
                    totalX += participant.Scores[i].XCount;
                }
            }

            cell.ScoreText = total > 0 ? total.ToString() : "-";
            cell.HasScore = total > 0;
            cell.BackgroundColorHex = "#1a365d"; // Dark blue for subtotals

            if (totalX > 0)
            {
                cell.XCountText = $"{totalX}x";
                cell.HasXCount = true;
            }

            row.Cells.Add(cell);
        }
    }

    /// <summary>
    /// Updates participant rankings based on current adjusted total scores.
    /// Rankings are sorted by AdjustedTotalScore descending.
    /// </summary>
    private void UpdateParticipantRankings()
    {
        var sortedParticipants = Participants
            .OrderByDescending(p => p.AdjustedTotalScore)
            .ToList();

        // Create new collection to ensure bindings re-evaluate
        var newRankings = new ObservableCollection<ParticipantRanking>();
        for (int i = 0; i < sortedParticipants.Count; i++)
        {
            var p = sortedParticipants[i];
            var participantId = p.MemberId ?? p.GuestParticipantId ?? 0;
            newRankings.Add(new ParticipantRanking
            {
                ParticipantId = participantId,
                Ranking = i + 1
            });
        }

        // Replace the entire collection to trigger property change notification
        Rankings = newRankings;
    }

    /// <summary>
    /// Gets the ranking for a specific participant (by MemberId or GuestParticipantId)
    /// </summary>
    public ParticipantRanking? GetRankingForParticipant(int? memberId, int? guestParticipantId)
    {
        var participantId = memberId ?? guestParticipantId ?? 0;
        return Rankings.FirstOrDefault(r => r.ParticipantId == participantId);
    }
}
