using HpskSite.Mobile.Services;

namespace HpskSite.Mobile;

public partial class App : Application
{
	private static string _logPath = string.Empty;

	public App(IThemeService themeService)
	{
		InitializeComponent();

		// Setup global exception handling
		SetupExceptionHandling();

		// Apply saved theme preference
		themeService.Initialize();
	}

	private void SetupExceptionHandling()
	{
		_logPath = Path.Combine(FileSystem.AppDataDirectory, "app.log");

		// Catch all unhandled exceptions
		AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
		{
			LogException("AppDomain.UnhandledException", args.ExceptionObject as Exception);
		};

		// Catch unobserved task exceptions
		TaskScheduler.UnobservedTaskException += (sender, args) =>
		{
			LogException("TaskScheduler.UnobservedTaskException", args.Exception);
			args.SetObserved();
		};

		// Catch MAUI unhandled exceptions
		Microsoft.Maui.Handlers.ElementHandler.ElementMapper.AppendToMapping("ExceptionHandler", (handler, view) => { });

#if WINDOWS
		Microsoft.UI.Xaml.Application.Current.UnhandledException += (sender, args) =>
		{
			LogException("Windows.UnhandledException", args.Exception);
			args.Handled = true;
		};
#endif
	}

	public static void LogException(string source, Exception? ex)
	{
		if (ex == null) return;

		try
		{
			var message = $@"
================================================================================
[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED EXCEPTION
Source: {source}
Type: {ex.GetType().FullName}
Message: {ex.Message}
StackTrace:
{ex.StackTrace}
";
			if (ex.InnerException != null)
			{
				message += $@"
--- Inner Exception ---
Type: {ex.InnerException.GetType().FullName}
Message: {ex.InnerException.Message}
StackTrace:
{ex.InnerException.StackTrace}
";
			}

			File.AppendAllText(_logPath, message);
			System.Diagnostics.Debug.WriteLine(message);
		}
		catch
		{
			// Ignore logging errors
		}
	}

	public static void Log(string message)
	{
		try
		{
			var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
			File.AppendAllText(_logPath, logEntry);
			System.Diagnostics.Debug.WriteLine(logEntry);
		}
		catch { }
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());

#if DEBUG && WINDOWS
		// Galaxy S21/Pixel 7 logical size
		window.Width = 411;
		window.Height = 914;
#endif

		return window;
	}
}