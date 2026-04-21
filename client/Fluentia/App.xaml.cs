using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace Fluentia;

public partial class App : Application
{
    private static Mutex? _mutex;

    public static event EventHandler? ThemeChanged;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetColorizationColor(out uint colorization, out bool opaqueBlend);

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "Fluentia_SingleInstance_Mutex";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Fluentia is already running.", "Fluentia",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        ApplySystemTheme();
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
        var accent = GetSystemAccentColor();

        var bgDark = isLight ? ColorFromHex("#FFF6F7FB") : ColorFromHex("#FF0B0E14");
        var bgPanel = isLight ? ColorFromHex("#FFFFFFFF") : Mix(ColorFromHex("#FF111723"), accent, 0.08);
        var bgInput = isLight ? Mix(ColorFromHex("#FFFFFFFF"), accent, 0.09) : Mix(ColorFromHex("#FF161F2B"), accent, 0.10);
        var textPrimary = isLight ? ColorFromHex("#FF171C24") : ColorFromHex("#FFF5F7FA");
        var textSecondary = isLight ? ColorFromHex("#FF5B6675") : ColorFromHex("#FFACB6C5");
        var textTertiary = isLight ? ColorFromHex("#FF8993A1") : ColorFromHex("#FF7B8798");
        var border = isLight ? Mix(ColorFromHex("#FFD6DCE6"), accent, 0.12) : ColorFromHex("#FF273244");
        var divider = isLight ? ColorFromHex("#1E304050") : ColorFromHex("#20304050");
        var secondaryHover = isLight ? Mix(bgInput, accent, 0.12) : Mix(bgInput, accent, 0.16);
        var accentHover = isLight ? Mix(accent, Colors.White, 0.18) : Mix(accent, Colors.White, 0.12);
        var accentSoft = Color.FromArgb(isLight ? (byte)40 : (byte)56, accent.R, accent.G, accent.B);
        var overlay = Color.FromArgb(isLight ? (byte)90 : (byte)138, 5, 7, 11);

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

    private static Color GetSystemAccentColor()
    {
        try
        {
            if (DwmGetColorizationColor(out uint argb, out _) == 0)
            {
                return Color.FromRgb(
                    (byte)((argb >> 16) & 0xFF),
                    (byte)((argb >> 8) & 0xFF),
                    (byte)(argb & 0xFF));
            }
        }
        catch
        {
            // Fall back to the Fluentia default accent.
        }

        return ColorFromHex("#FF0A84FF");
    }

    private static Color Mix(Color from, Color to, double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        return Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * ratio),
            (byte)(from.R + (to.R - from.R) * ratio),
            (byte)(from.G + (to.G - from.G) * ratio),
            (byte)(from.B + (to.B - from.B) * ratio));
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
