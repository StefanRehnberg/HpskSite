using Android.App;
using Android.Runtime;

namespace HpskSite.Mobile;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	public override void OnCreate()
	{
		base.OnCreate();

		// Catch native Android crashes
		AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
		{
			App.LogException("AndroidEnvironment.UnhandledException", args.Exception);
			args.Handled = true;
		};
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
