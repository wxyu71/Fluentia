using System.Globalization;
using System.IO;
using System.Text.Json;
using Fluentia.Models;

namespace Fluentia.Services;

public sealed record DesktopSettingsLoadResult(
    string FileSavePath,
    string ServerUrl,
    bool CloseToTray,
    bool LaunchAtStartup,
    string? Language,
    string RegexFilterMarkdown,
    DateTime SessionCreatedAt,
    DateTime SessionExpiresAt,
    bool PersistedSessionLost,
    bool ShouldPersistAfterLoad,
    PersistedDesktopSession? PersistedSession);

public sealed record DesktopSettingsSaveRequest(
    string FileSavePath,
    string ServerUrl,
    bool CloseToTray,
    bool LaunchAtStartup,
    string? Language,
    string RegexFilterMarkdown,
    DateTime SessionCreatedAt,
    DateTime SessionExpiresAt,
    PersistedDesktopSession? PersistedSession);

public sealed class DesktopSettingsStore
{
    private readonly string _settingsFile;
    private readonly string _sessionBackupFile;

    public DesktopSettingsStore(string settingsFile, string sessionBackupFile)
    {
        _settingsFile = settingsFile;
        _sessionBackupFile = sessionBackupFile;
    }

    public DesktopSettingsLoadResult Load(Func<DateTime, DateTime> resolveSessionExpiry)
    {
        var migrateLegacySession = false;
        var hadProtectedSession = false;
        var recoveredFromBackup = false;
        var persistedSessionLost = false;
        var fileSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var serverUrl = string.Empty;
        var closeToTray = true;
        var launchAtStartup = false;
        var regexFilterMarkdown = string.Empty;
        string? language = null;
        DateTime sessionCreatedAt = default;
        DateTime sessionExpiresAt = default;
        PersistedDesktopSession? restoredSession = null;
        var restoredFromPrimary = false;

        try
        {
            if (File.Exists(_settingsFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_settingsFile));

                if (doc.RootElement.TryGetProperty("savePath", out var savePathElement))
                {
                    var value = savePathElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                    {
                        fileSavePath = value;
                    }
                }

                if (doc.RootElement.TryGetProperty("serverUrl", out var serverUrlElement))
                {
                    serverUrl = serverUrlElement.GetString() ?? string.Empty;
                }

                if (doc.RootElement.TryGetProperty("closeToTray", out var closeToTrayElement))
                {
                    closeToTray = closeToTrayElement.GetBoolean();
                }

                if (doc.RootElement.TryGetProperty("launchAtStartup", out var startupElement))
                {
                    launchAtStartup = startupElement.GetBoolean();
                }

                if (doc.RootElement.TryGetProperty("language", out var languageElement))
                {
                    language = languageElement.GetString();
                }

                if (doc.RootElement.TryGetProperty("regexFilterMarkdown", out var regexMarkdownElement))
                {
                    regexFilterMarkdown = regexMarkdownElement.GetString() ?? string.Empty;
                }

                if (doc.RootElement.TryGetProperty("sessionCreatedAtUtc", out var sessionCreatedElement) &&
                    DateTime.TryParse(sessionCreatedElement.GetString(), null, DateTimeStyles.RoundtripKind, out var restoredCreatedAt))
                {
                    sessionCreatedAt = restoredCreatedAt.ToLocalTime();
                }

                if (doc.RootElement.TryGetProperty("sessionExpiresAtUtc", out var sessionExpiresElement) &&
                    DateTimeOffset.TryParse(sessionExpiresElement.GetString(), out var restoredExpiresAt))
                {
                    sessionExpiresAt = restoredExpiresAt.LocalDateTime;
                }
                else if (sessionCreatedAt != default)
                {
                    sessionExpiresAt = resolveSessionExpiry(sessionCreatedAt);
                }

                if (doc.RootElement.TryGetProperty("protectedSession", out var protectedSessionElement))
                {
                    hadProtectedSession = !string.IsNullOrWhiteSpace(protectedSessionElement.GetString());
                    restoredSession = DesktopSessionProtector.Unprotect(protectedSessionElement.GetString() ?? string.Empty);
                }

                if (restoredSession == null &&
                    doc.RootElement.TryGetProperty("sessionToken", out var tokenElement) &&
                    doc.RootElement.TryGetProperty("sessionPublicKey", out var publicKeyElement) &&
                    doc.RootElement.TryGetProperty("sessionPrivateKey", out var privateKeyElement))
                {
                    var token = tokenElement.GetString();
                    var publicKey = publicKeyElement.GetString();
                    var privateKey = privateKeyElement.GetString();
                    var trusted = doc.RootElement.TryGetProperty("sessionTrusted", out var trustedElement) && trustedElement.GetBoolean();

                    if (!string.IsNullOrWhiteSpace(token) &&
                        !string.IsNullOrWhiteSpace(publicKey) &&
                        !string.IsNullOrWhiteSpace(privateKey))
                    {
                        restoredSession = new PersistedDesktopSession(token, publicKey, privateKey, trusted);
                        migrateLegacySession = true;
                    }
                }

                if (restoredSession != null)
                {
                    restoredFromPrimary = true;
                }
            }

            if (!restoredFromPrimary && File.Exists(_sessionBackupFile))
            {
                var backupProtectedSession = File.ReadAllText(_sessionBackupFile);
                if (!string.IsNullOrWhiteSpace(backupProtectedSession))
                {
                    var restoredFromBackupSession = DesktopSessionProtector.Unprotect(backupProtectedSession);
                    if (restoredFromBackupSession != null)
                    {
                        restoredSession = restoredFromBackupSession;
                        recoveredFromBackup = true;
                    }
                    else if (hadProtectedSession)
                    {
                        persistedSessionLost = true;
                    }
                }
            }
            else if (hadProtectedSession && restoredSession == null)
            {
                persistedSessionLost = true;
            }
        }
        catch
        {
        }

        return new DesktopSettingsLoadResult(
            fileSavePath,
            serverUrl,
            closeToTray,
            launchAtStartup,
            language,
            regexFilterMarkdown,
            sessionCreatedAt,
            sessionExpiresAt,
            persistedSessionLost,
            migrateLegacySession || recoveredFromBackup,
            restoredSession);
    }

    public void Save(DesktopSettingsSaveRequest request)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            var payload = JsonSerializer.Serialize(new
            {
                savePath = request.FileSavePath,
                serverUrl = request.ServerUrl,
                closeToTray = request.CloseToTray,
                launchAtStartup = request.LaunchAtStartup,
                language = request.Language,
                regexFilterMarkdown = request.RegexFilterMarkdown,
                protectedSession = request.PersistedSession == null ? null : DesktopSessionProtector.Protect(request.PersistedSession),
                sessionCreatedAtUtc = request.SessionCreatedAt == default ? null : request.SessionCreatedAt.ToUniversalTime().ToString("O"),
                sessionExpiresAtUtc = request.SessionExpiresAt == default ? null : request.SessionExpiresAt.ToUniversalTime().ToString("O"),
            });
            File.WriteAllText(_settingsFile, payload);

            if (request.PersistedSession == null)
            {
                if (File.Exists(_sessionBackupFile))
                {
                    File.Delete(_sessionBackupFile);
                }

                return;
            }

            File.WriteAllText(_sessionBackupFile, DesktopSessionProtector.Protect(request.PersistedSession));
        }
        catch
        {
        }
    }
}