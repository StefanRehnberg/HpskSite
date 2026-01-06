using Foundation;
using Plugin.Firebase.CloudMessaging;
using UIKit;
using UserNotifications;

namespace HpskSite.Mobile;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate, IUNUserNotificationCenterDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        // Set notification center delegate for foreground notifications
        UNUserNotificationCenter.Current.Delegate = this;

        return base.FinishedLaunching(application, launchOptions);
    }

    // Handle notification when app is in foreground
    [Export("userNotificationCenter:willPresentNotification:withCompletionHandler:")]
    public void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
    {
        // Show notification even when app is in foreground
        completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound | UNNotificationPresentationOptions.Badge);
    }

    // Handle notification tap
    [Export("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:")]
    public void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
    {
        var userInfo = response.Notification.Request.Content.UserInfo;

        // Check if notification contains match code
        if (userInfo.TryGetValue(new NSString("matchCode"), out var matchCodeObj) && matchCodeObj is NSString matchCode)
        {
            // Navigate to match on main thread
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Shell.Current.GoToAsync($"//match?code={matchCode}");
            });
        }

        completionHandler();
    }
}
