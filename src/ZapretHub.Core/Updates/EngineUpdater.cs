namespace ZapretHub.Core.Updates;

public enum UpdateOutcome { UpToDate, Skipped, Installed }

public sealed record UpdateResult(UpdateOutcome Outcome, string LatestVersion, EngineLayout? Installed);

/// <summary>Check → compare → download → install → switch → cleanup. Failures leave a working engine active.</summary>
public sealed class EngineUpdater
{
    private readonly FlowsealReleaseClient _releases;
    private readonly EngineStore _store;

    public EngineUpdater(FlowsealReleaseClient releases, EngineStore store)
    {
        _releases = releases;
        _store = store;
    }

    /// <param name="switchTo">Runs once the new version is active and before old versions are deleted
    /// (e.g. restart winws on the new files). If it throws, the previous version is re-activated and
    /// passed to <paramref name="rollback"/>, and nothing is cleaned up.</param>
    public async Task<UpdateResult> UpdateAsync(
        Func<EngineLayout, Task>? switchTo,
        CancellationToken ct,
        Func<EngineLayout, Task>? rollback = null,
        string? skipVersion = null)
    {
        var latest = await _releases.GetLatestAsync(ct).ConfigureAwait(false);
        if (!ReleaseVersion.IsNewer(latest.Version, _store.ActiveVersion))
        {
            return new UpdateResult(UpdateOutcome.UpToDate, latest.Version, null);
        }
        if (skipVersion is not null && latest.Version == ReleaseVersion.Normalize(skipVersion))
        {
            return new UpdateResult(UpdateOutcome.Skipped, latest.Version, null);
        }

        return await InstallAsync(latest, switchTo, rollback, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the latest release if it is newer than the active engine and not skipped; nothing is downloaded.</summary>
    public async Task<ReleaseInfo?> CheckAsync(string? skipVersion, CancellationToken ct)
    {
        var latest = await _releases.GetLatestAsync(ct).ConfigureAwait(false);
        if (!ReleaseVersion.IsNewer(latest.Version, _store.ActiveVersion)) return null;
        if (skipVersion is not null && latest.Version == ReleaseVersion.Normalize(skipVersion)) return null;
        return latest;
    }

    /// <summary>Downloads, verifies and activates a release found by <see cref="CheckAsync"/> (e.g. after the user confirmed).</summary>
    public async Task<UpdateResult> InstallAsync(
        ReleaseInfo latest, Func<EngineLayout, Task>? switchTo, Func<EngineLayout, Task>? rollback, CancellationToken ct)
    {
        // A second confirmation of the same release (menu + balloon) must not reinstall the running engine:
        // reinstalling the active version deletes the directory winws runs from.
        if (!ReleaseVersion.IsNewer(latest.Version, _store.ActiveVersion))
        {
            return new UpdateResult(UpdateOutcome.UpToDate, latest.Version, null);
        }
        try
        {
            return await InstallCoreAsync(latest, switchTo, rollback, ct).ConfigureAwait(false);
        }
        catch (UpdateException ex) when (ex.Version is null)
        {
            throw new UpdateException(ex.Message, ex.InnerException, latest.Version, ex.ReleaseDefect);
        }
    }

    private async Task<UpdateResult> InstallCoreAsync(
        ReleaseInfo latest, Func<EngineLayout, Task>? switchTo, Func<EngineLayout, Task>? rollback, CancellationToken ct)
    {
        var previous = _store.GetActive();
        await using var zip = await _releases.DownloadAsync(latest, ct).ConfigureAwait(false);
        EngineLayout layout;
        try
        {
            layout = _store.Install(zip, latest.Version);
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException($"Flowseal {latest.Version} archive is not usable: {ex.Message}", ex, latest.Version, releaseDefect: true);
        }

        if (switchTo is not null)
        {
            try
            {
                await switchTo(layout).ConfigureAwait(false);
            }
            catch (Exception ex) when (previous is not null)
            {
                _store.Activate(previous.Version);
                if (rollback is not null) await rollback(previous).ConfigureAwait(false);
                throw new UpdateException($"Flowseal {latest.Version} failed to start; rolled back to {previous.Version}: {ex.Message}", ex, latest.Version, releaseDefect: true);
            }
        }

        _store.CleanupInactive();
        return new UpdateResult(UpdateOutcome.Installed, latest.Version, layout);
    }
}
