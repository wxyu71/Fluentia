using System.Windows;
using System.Windows.Media;
using Fluentia.Services;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace Fluentia;

public partial class App : Application
{
    private static Mutex? _mutex;

    public static event EventHandler? ThemeChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Fluentia_SingleInstance_Mutex";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(LocalizationService.Get("AppAlreadyRunningMessage"), LocalizationService.Get("AppName"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ApplySystemTheme();
        base.OnStartup(e);
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Dispatcher.BeginInvoke(ApplySystemTheme);
        }
    }

    private void ApplySystemTheme()
    {
        bool isLight = IsSystemLightTheme();
        var accent = isLight ? ColorFromHex("#FF007AFF") : ColorFromHex("#FF0A84FF");

        var bgDark = isLight ? ColorFromHex("#FFF5F5F7") : ColorFromHex("#FF000000");
        var bgPanel = isLight ? ColorFromHex("#FFFFFFFF") : ColorFromHex("#FF1C1C1E");
        var bgInput = isLight ? ColorFromHex("#FFF2F2F7") : ColorFromHex("#FF2C2C2E");
        var textPrimary = isLight ? ColorFromHex("#FF1C1C1E") : ColorFromHex("#FFF5F5F7");
        var textSecondary = isLight ? ColorFromHex("#FF6E6E73") : ColorFromHex("#FFAEAEB2");
        var textTertiary = isLight ? ColorFromHex("#FF8E8E93") : ColorFromHex("#FF8E8E93");
        var border = isLight ? ColorFromHex("#FFD2D2D7") : ColorFromHex("#FF3A3A3C");
        var divider = isLight ? ColorFromHex("#223C3C43") : ColorFromHex("#33EBEBF5");
        var secondaryHover = isLight ? ColorFromHex("#FFE8E8ED") : ColorFromHex("#FF36363A");
        var accentHover = isLight ? ColorFromHex("#FF2490FF") : ColorFromHex("#FF3395FF");
        var accentSoft = Color.FromArgb(isLight ? (byte)26 : (byte)44, accent.R, accent.G, accent.B);
        var overlay = isLight ? Color.FromArgb(94, 17, 24, 39) : Color.FromArgb(148, 0, 0, 0);

        UpdateResourcePair(Resources, "BgDark", "BgDarkBrush", bgDark);
        UpdateResourcePair(Resources, "BgPanel", "BgPanelBrush", bgPanel);
        UpdateResourcePair(Resources, "BgInput", "BgInputBrush", bgInput);
        UpdateResourcePair(Resources, "Accent", "AccentBrush", accent);
        UpdateResourcePair(Resources, "AccentSoft", "AccentSoftBrush", accentSoft);
        UpdateResourcePair(Resources, "TextPrimary", "TextPrimaryBrush", textPrimary);
        UpdateResourcePair(Resources, "TextSecondary", "TextSecondaryBrush", textSecondary);
        UpdateResourcePair(Resources, "TextTertiary", "TextTertiaryBrush", textTertiary);
        UpdateResourcePair(Resources, "BorderColor", "BorderBrush", border);
        UpdateResourcePair(Resources, "DividerColor", "DividerBrush", divider);
        UpdateResourcePair(Resources, "OverlayColor", "OverlayBrush", overlay);

        UpdateResourcePair(Resources, "AccentHover", "AccentHoverBrush", accentHover);
        UpdateResourcePair(Resources, "SecondaryHover", "SecondaryHoverBrush", secondaryHover);
        UpdateResourcePair(Resources, "Success", "SuccessBrush", isLight ? ColorFromHex("#FF219653") : ColorFromHex("#FF30D158"));
        UpdateResourcePair(Resources, "Danger", "DangerBrush", isLight ? ColorFromHex("#FFE24B42") : ColorFromHex("#FFFF453A"));

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void UpdateResourcePair(ResourceDictionary resources, string colorKey, string brushKey, Color color)
    {
        resources[colorKey] = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[brushKey] = brush;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 1;
        }
        catch
        {
            return false;
        }
    }

    private static Color ColorFromHex(string hex)
    {
        return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
