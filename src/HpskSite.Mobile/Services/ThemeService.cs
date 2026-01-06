namespace HpskSite.Mobile.Services;

/// <summary>
/// Service to manage app theme (light/dark mode)
/// </summary>
public class ThemeService : IThemeService
{
    private const string ThemeKey = "app_theme";

    public ThemeMode CurrentTheme
    {
        get
        {
            var stored = Preferences.Get(ThemeKey, (int)ThemeMode.System);
            return (ThemeMode)stored;
        }
        set
        {
            Preferences.Set(ThemeKey, (int)value);
            ApplyTheme(value);
        }
    }

    public void ApplyTheme(ThemeMode mode)
    {
        Application.Current!.UserAppTheme = mode switch
        {
            ThemeMode.Light => AppTheme.Light,
            ThemeMode.Dark => AppTheme.Dark,
            _ => AppTheme.Unspecified // Follow system
        };
    }

    public void Initialize()
    {
        ApplyTheme(CurrentTheme);
    }
}

public interface IThemeService
{
    ThemeMode CurrentTheme { get; set; }
    void ApplyTheme(ThemeMode mode);
    void Initialize();
}

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}
