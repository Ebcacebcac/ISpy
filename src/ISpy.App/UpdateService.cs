using System.IO;
using ISpy.Core;
using ISpy.Core.Storage;
using Velopack;
using Velopack.Sources;

namespace ISpy.App;

/// <summary>What the update check found.</summary>
public sealed record UpdateStatus(
    bool IsAvailable,
    string? Version = null,
    string? ReleaseNotes = null,
    string? Error = null);

/// <summary>
/// Checks GitHub Releases for a newer build and installs it on request.
/// </summary>
/// <remarks>
/// Never runs on the startup path. The check happens on a background thread after the window is on
/// screen, and it fails silently when offline - a machine with no internet must still open its
/// cameras at full speed. Updates are delta where possible, so a small fix is a short download
/// rather than the whole application.
/// </remarks>
public sealed class UpdateService : IDisposable
{
    /// <summary>Where releases are published.</summary>
    public const string RepositoryUrl = "https://github.com/Ebcacebcac/ISpy";

    /// <summary>Settings key for the user's opt-out.</summary>
    public const string EnabledSetting = "updates.enabled";

    /// <summary>Long-running sessions re-check on this interval.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(6);

    private readonly InventoryStore? _store;
    private readonly UpdateManager _manager;
    private readonly CancellationTokenSource _shutdown = new();

    private UpdateInfo? _pending;
    private Timer? _timer;

    public UpdateService(InventoryStore? store)
    {
        _store = store;
        _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, prerelease: false));
    }

    /// <summary>False when the user has switched updates off.</summary>
    public bool IsEnabled =>
        !string.Equals(_store?.GetSetting(EnabledSetting), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// False when running from a plain build directory rather than an installed copy, in which case
    /// there is nothing to update.
    /// </summary>
    public bool CanUpdate => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "development build";

    /// <summary>Raised, on a background thread, when a newer release is found.</summary>
    public event Action<UpdateStatus>? StatusChanged;

    public void SetEnabled(bool enabled)
    {
        _store?.SetSetting(EnabledSetting, enabled ? "true" : "false");

        if (enabled) Start();
        else Stop();
    }

    /// <summary>Begins periodic checking. Safe to call before the user has any devices configured.</summary>
    public void Start()
    {
        if (!IsEnabled || !CanUpdate) return;

        _timer ??= new Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, RecheckInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Looks for a newer release. Never throws.</summary>
    public async Task<UpdateStatus> CheckAsync()
    {
        if (!IsEnabled) return new UpdateStatus(false);

        if (!CanUpdate)
        {
            return new UpdateStatus(false,
                Error: "Updates apply to installed copies; this is a development build.");
        }

        try
        {
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);

            if (update is null)
            {
                var upToDate = new UpdateStatus(false);
                StatusChanged?.Invoke(upToDate);
                return upToDate;
            }

            _pending = update;

            var status = new UpdateStatus(
                true,
                update.TargetFullRelease.Version.ToString(),
                update.TargetFullRelease.NotesMarkdown);

            StatusChanged?.Invoke(status);
            return status;
        }
        catch (Exception ex)
        {
            // Offline, GitHub unreachable, rate limited - none of these are worth interrupting the
            // user over. Record it and try again on the next interval.
            Log(ex);

            var failed = new UpdateStatus(false, Error: ex.Message);
            StatusChanged?.Invoke(failed);
            return failed;
        }
    }

    /// <summary>
    /// Downloads the pending update and restarts into it. <paramref name="beforeRestart"/> runs once
    /// the download is complete, so streams can be shut down cleanly first.
    /// </summary>
    public async Task<string?> ApplyAsync(
        IProgress<int>? progress = null, Action? beforeRestart = null)
    {
        if (_pending is null) return "No update is pending.";

        try
        {
            await _manager
                .DownloadUpdatesAsync(
                    _pending,
                    percent => progress?.Report(percent),
                    _shutdown.Token)
                .ConfigureAwait(false);

            beforeRestart?.Invoke();

            _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
            return null;
        }
        catch (Exception ex)
        {
            Log(ex);
            return ex.Message;
        }
    }

    private static void Log(Exception exception)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.LogDirectory, "update.log"),
                $"{DateTimeOffset.UtcNow:O} {exception.Message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Diagnostics must never break the app.
        }
    }

    public void Dispose()
    {
        Stop();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
