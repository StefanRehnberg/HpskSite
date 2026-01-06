using CommunityToolkit.Maui;
using HpskSite.Mobile.Services;
using HpskSite.Mobile.ViewModels;
using HpskSite.Mobile.Views;
using Microsoft.Extensions.Logging;
using Plugin.Firebase.CloudMessaging;
using ZXing.Net.Maui.Controls;
#if ANDROID
using Plugin.Firebase.Core.Platforms.Android;
#elif IOS
using Plugin.Firebase.Core.Platforms.iOS;
#endif

namespace HpskSite.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseBarcodeReader()
            .RegisterFirebaseServices()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Register services
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<ISecureStorageService, SecureStorageService>();
        builder.Services.AddSingleton<IApiService>(sp =>
        {
            var secureStorage = sp.GetRequiredService<ISecureStorageService>();
            var apiService = new ApiService(secureStorage);
            // Configure base URL - change this for production
// Always use production URL (10.0.2.2 only works in emulator, not real devices)
            apiService.BaseUrl = "https://hpsktest.se";
            return apiService;
        });

        // Push notification services
        builder.Services.AddSingleton<IPushNotificationService, PushNotificationService>();
        builder.Services.AddSingleton<INotificationPreferencesService, NotificationPreferencesService>();

        // Auth service with lazy dependencies to avoid circular reference
        builder.Services.AddSingleton<IAuthService>(sp =>
        {
            var apiService = sp.GetRequiredService<IApiService>();
            var secureStorage = sp.GetRequiredService<ISecureStorageService>();
            var pushNotificationService = new Lazy<IPushNotificationService>(() => sp.GetRequiredService<IPushNotificationService>());
            var preferencesService = new Lazy<INotificationPreferencesService>(() => sp.GetRequiredService<INotificationPreferencesService>());
            return new AuthService(apiService, secureStorage, pushNotificationService, preferencesService);
        });

        builder.Services.AddSingleton<ISignalRService>(sp =>
        {
            var secureStorage = sp.GetRequiredService<ISecureStorageService>();
            var signalRService = new SignalRService(secureStorage);
// Always use production URL
            signalRService.BaseUrl = "https://hpsktest.se";
            return signalRService;
        });
        builder.Services.AddSingleton<IMatchService, MatchService>();
        builder.Services.AddSingleton<IStatsService, StatsService>();
        builder.Services.AddSingleton<ImageCompressionService>();

        // Register ViewModels
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<MatchListViewModel>();
        builder.Services.AddTransient<CreateMatchViewModel>();
        builder.Services.AddTransient<JoinMatchViewModel>();
        builder.Services.AddTransient<ActiveMatchViewModel>();
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        // Register Pages
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<MatchListPage>();
        builder.Services.AddTransient<CreateMatchPage>();
        builder.Services.AddTransient<JoinMatchPage>();
        builder.Services.AddTransient<QrScannerPage>();
        builder.Services.AddTransient<ActiveMatchPage>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<SettingsPage>();

#if DEBUG
        builder.Logging.AddDebug();

        // Add file logging for debugging
        var logPath = Path.Combine(FileSystem.AppDataDirectory, "app.log");
        builder.Logging.AddFileLogger(logPath);
        System.Diagnostics.Debug.WriteLine($"Log file: {logPath}");
#endif

        return builder.Build();
    }

    private static MauiAppBuilder RegisterFirebaseServices(this MauiAppBuilder builder)
    {
#if ANDROID
        builder.Services.AddSingleton(_ => CrossFirebaseCloudMessaging.Current);
#elif IOS
        builder.Services.AddSingleton(_ => CrossFirebaseCloudMessaging.Current);
#endif
        return builder;
    }
}
