using Android.App;
using Android.Content.PM;
using Android.OS;
using Firebase;
using Plugin.Firebase.CloudMessaging;

namespace HpskSite.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Initialize Firebase
        FirebaseApp.InitializeApp(this);

        // Create notification channel for match notifications (required for Android 8.0+)
        CreateNotificationChannel();

        // Handle Firebase Cloud Messaging
        FirebaseCloudMessagingImplementation.SmallIconRef = Resource.Drawable.ic_notification;
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                "match_notifications",
                "Match notiser",
                NotificationImportance.High)
            {
                Description = "Notiser om nya traningsmatcher"
            };

            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.CreateNotificationChannel(channel);
        }
    }
}
