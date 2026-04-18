using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Fluentia;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance check
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

        // Apply system theme on startup
        ApplySystemTheme();

        // Listen for system theme changes
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General)
            {
                Dispatcher.BeginInvoke(ApplySystemTheme);
            }
        };
    }

    private void ApplySystemTheme()
    {
        bool isLight = IsSystemLightTheme();
        var res = Resources;

        if (isLight)
        {
            // Light theme — Apple-inspired light
            res["BgDark"] = ColorFromHex("#FFF2F2F7");
            res["BgPanel"] = ColorFromHex("#FFFFFFFF");
            res["BgInput"] = ColorFromHex("#FFE5E5EA");
            res["Accent"] = ColorFromHex("#FF007AFF");
            res["AccentHover"] = ColorFromHex("#FF0A84FF");
            res["TextPrimary"] = ColorFromHex("#FF1C1C1E");
            res["TextSecondary"] = ColorFromHex("#FF8E8E93");
            res["BorderColor"] = ColorFromHex("#FFC7C7CC");
            res["Success"] = ColorFromHex("#FF34C759");
            res["Danger"] = ColorFromHex("#FFFF3B30");
            res["SecondaryHover"] = ColorFromHex("#FFD1D1D6");

            res["BgDarkBrush"] = new SolidColorBrush(ColorFromHex("#FFF2F2F7"));
            res["BgPanelBrush"] = new SolidColorBrush(ColorFromHex("#FFFFFFFF"));
            res["BgInputBrush"] = new SolidColorBrush(ColorFromHex("#FFE5E5EA"));
            res["AccentBrush"] = new SolidColorBrush(ColorFromHex("#FF007AFF"));
            res["TextPrimaryBrush"] = new SolidColorBrush(ColorFromHex("#FF1C1C1E"));
            res["TextSecondaryBrush"] = new SolidColorBrush(ColorFromHex("#FF8E8E93"));
            res["BorderBrush"] = new SolidColorBrush(ColorFromHex("#FFC7C7CC"));
        }
        else
        {
            // Dark theme — Apple-inspired dark (original)
            res["BgDark"] = ColorFromHex("#FF000000");
            res["BgPanel"] = ColorFromHex("#FF1C1C1E");
            res["BgInput"] = ColorFromHex("#FF2C2C2E");
            res["Accent"] = ColorFromHex("#FF0A84FF");
            res["AccentHover"] = ColorFromHex("#FF409CFF");
            res["TextPrimary"] = ColorFromHex("#FFF5F5F7");
            res["TextSecondary"] = ColorFromHex("#FF86868B");
            res["BorderColor"] = ColorFromHex("#FF38383A");
            res["Success"] = ColorFromHex("#FF30D158");
            res["Danger"] = ColorFromHex("#FFFF453A");
            res["SecondaryHover"] = ColorFromHex("#FF3A3A3C");

            res["BgDarkBrush"] = new SolidColorBrush(ColorFromHex("#FF000000"));
            res["BgPanelBrush"] = new SolidColorBrush(ColorFromHex("#FF1C1C1E"));
            res["BgInputBrush"] = new SolidColorBrush(ColorFromHex("#FF2C2C2E"));
            res["AccentBrush"] = new SolidColorBrush(ColorFromHex("#FF0A84FF"));
            res["TextPrimaryBrush"] = new SolidColorBrush(ColorFromHex("#FFF5F5F7"));
            res["TextSecondaryBrush"] = new SolidColorBrush(ColorFromHex("#FF86868B"));
            res["BorderBrush"] = new SolidColorBrush(ColorFromHex("#FF38383A"));
        }
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 1;
        }
        catch { return false; } // default to dark
    }

    private static System.Windows.Media.Color ColorFromHex(string hex)
    {
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
