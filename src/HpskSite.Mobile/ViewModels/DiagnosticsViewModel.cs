using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HpskSite.Mobile.ViewModels;

public partial class DiagnosticsViewModel : BaseViewModel
{
    private readonly string _logFilePath;

    public DiagnosticsViewModel()
    {
        Title = "Diagnostik";
        _logFilePath = Path.Combine(FileSystem.AppDataDirectory, "crash.log");
        LoadLogs();
    }

    [ObservableProperty]
    private string _logContent = string.Empty;

    [ObservableProperty]
    private string _logFileSize = string.Empty;

    [ObservableProperty]
    private bool _hasLogs;

    private void LoadLogs()
    {
        try
        {
            if (File.Exists(_logFilePath))
            {
                var fileInfo = new FileInfo(_logFilePath);
                LogFileSize = FormatFileSize(fileInfo.Length);

                // Read last 100KB max to avoid memory issues
                const long maxReadSize = 100 * 1024;
                if (fileInfo.Length > maxReadSize)
                {
                    using var stream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    stream.Seek(-maxReadSize, SeekOrigin.End);
                    using var reader = new StreamReader(stream);
                    // Skip partial first line
                    reader.ReadLine();
                    LogContent = "... (truncated) ...\n" + reader.ReadToEnd();
                }
                else
                {
                    LogContent = File.ReadAllText(_logFilePath);
                }

                HasLogs = !string.IsNullOrWhiteSpace(LogContent);
            }
            else
            {
                LogContent = "Ingen loggfil hittades.";
                LogFileSize = "0 B";
                HasLogs = false;
            }
        }
        catch (Exception ex)
        {
            LogContent = $"Kunde inte lasa loggar: {ex.Message}";
            HasLogs = false;
        }
    }

    [RelayCommand]
    private void RefreshLogs()
    {
        LoadLogs();
    }

    [RelayCommand]
    private async Task ShareLogsAsync()
    {
        if (!File.Exists(_logFilePath))
        {
            await Shell.Current.DisplayAlert("Fel", "Ingen loggfil att dela.", "OK");
            return;
        }

        try
        {
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Dela kraschloggar",
                File = new ShareFile(_logFilePath)
            });
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Fel", $"Kunde inte dela loggar: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        if (string.IsNullOrWhiteSpace(LogContent))
        {
            await Shell.Current.DisplayAlert("Fel", "Inga loggar att kopiera.", "OK");
            return;
        }

        try
        {
            await Clipboard.Default.SetTextAsync(LogContent);
            await Shell.Current.DisplayAlert("Kopierat", "Loggarna har kopierats till urklipp.", "OK");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Fel", $"Kunde inte kopiera: {ex.Message}", "OK");
        }
    }

    [RelayCommand]
    private async Task ClearLogsAsync()
    {
        var confirm = await Shell.Current.DisplayAlert(
            "Rensa loggar",
            "Ar du saker pa att du vill rensa alla loggar?",
            "Ja", "Avbryt");

        if (!confirm)
            return;

        try
        {
            if (File.Exists(_logFilePath))
            {
                File.Delete(_logFilePath);
            }
            LoadLogs();
            await Shell.Current.DisplayAlert("Klart", "Loggarna har rensats.", "OK");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Fel", $"Kunde inte rensa loggar: {ex.Message}", "OK");
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
