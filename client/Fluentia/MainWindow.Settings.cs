using System.Windows;
using Fluentia.Services;
using Microsoft.Win32;

namespace Fluentia;

public partial class MainWindow
{
    private void LoadSettings()
    {
        var loaded = _settingsStore.Load(ResolveSessionExpiry);

        _fileSavePath = loaded.FileSavePath;
        _closeToTray = loaded.CloseToTray;
        _launchAtStartup = IsLaunchAtStartupEnabled() || loaded.LaunchAtStartup;
        _regexFilterMarkdown = loaded.RegexFilterMarkdown;
        _sessionCreatedAt = loaded.SessionCreatedAt;
        _sessionExpiresAt = loaded.SessionExpiresAt;
        _persistedSessionLost = loaded.PersistedSessionLost;
        DebugLogger.Enabled = loaded.DebugLogging;

        if (!string.IsNullOrWhiteSpace(loaded.Language))
        {
            LocalizationService.SetLanguagePreference(loaded.Language);
        }

        ServerUrlBox.Text = loaded.ServerUrl ?? string.Empty;

        if (loaded.PersistedSession != null)
        {
            _roomManager.RestorePersistedSession(loaded.PersistedSession);
        }

        SavePathBox.Text = _fileSavePath;
        CloseToTrayToggle.IsChecked = _closeToTray;
        LaunchAtStartupToggle.IsChecked = _launchAtStartup;
        DebugLoggingToggle.IsChecked = DebugLogger.Enabled;
        UpdateLanguageSelectorSelection();

        if (loaded.ShouldPersistAfterLoad)
        {
            PersistSettings();
        }
    }

    private void PersistSettings()
    {
        _settingsStore.Save(new DesktopSettingsSaveRequest(
            _fileSavePath,
            ServerUrlBox.Text.Trim(),
            _closeToTray,
            _launchAtStartup,
            LocalizationService.CurrentLanguageSetting,
            _regexFilterMarkdown,
            _sessionCreatedAt,
            _sessionExpiresAt,
            _roomManager.ExportPersistedSession(),
            DebugLogger.Enabled));
    }

    private void ShowPersistedSessionLostPrompt()
    {
        SetStatus(L("StatusSavedSessionLost"), false);
        System.Windows.MessageBox.Show(
            L("SavedSessionLostBody"),
            L("SavedSessionLostTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        _persistedSessionLost = false;
    }

    private DateTime ResolveSessionExpiry(DateTime fallbackCreatedAt)
    {
        return _roomManager.SessionExpiresAtUtc?.LocalDateTime ?? fallbackCreatedAt.AddDays(_roomManager.SessionMaxAgeDays);
    }

    private bool IsLaunchAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
            return key?.GetValue(StartupRegistryValue) is string existing && !string.IsNullOrWhiteSpace(existing);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IsLaunchAtStartupEnabled failed: {ex.Message}");
            return false;
        }
    }
}