using OpenScct.Launcher.GitHub;
using OpenScct.Launcher.Update;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Core.Updates;

/// <summary>A release this platform can update to: its archive and the archive's published checksum.</summary>
public sealed record UpdateOffer(VersionedRelease Release, ReleaseAsset Archive, ReleaseAsset Checksum)
{
    public SemVersion Version => Release.Version;

    public DateTimeOffset? Published => Release.Release.PublishedAt;

    /// <summary>The release's page on GitHub (its full notes).</summary>
    public string Page => Release.Release.HtmlUrl ?? EditorRelease.ReleasesPage;

    /// <summary>The first lines of the release notes as plain text.</summary>
    public IReadOnlyList<string> Notes => ReleaseNotesPreview.Create(Release.Release.Body);
}

public enum UpdateCheckKind
{
    /// <summary>The newest release is not newer than this build (or there is no release yet).</summary>
    UpToDate,

    /// <summary>A newer release is out (<see cref="UpdateCheckResult.Offer"/>).</summary>
    Available,

    /// <summary>GitHub could not be asked and nothing is cached (<see cref="UpdateCheckResult.Problem"/>).</summary>
    Failed,
}

/// <summary>What a check found. <see cref="Problem"/> and <see cref="RateLimitReset"/> say why GitHub could not be
/// asked; an answer from the cache (<see cref="FromCache"/>) still counts, since the releases in it are real.</summary>
public sealed record UpdateCheckResult(
    UpdateCheckKind Kind, SemVersion Current, UpdateOffer? Offer, SemVersion? Latest, string? Problem, DateTimeOffset? RateLimitReset, bool FromCache);

/// <summary>
/// Asks GitHub for the editor's releases (OpenSCCT's <see cref="GitHubReleaseClient"/>: ETag cache, rate limits,
/// offline cache) and picks the newest one with this platform's archive and its checksum file. Drafts and tags that
/// are not versions are never offered; pre-releases only when asked for, or when this build is one.
/// </summary>
public static class UpdateChecker
{
    public static async Task<UpdateCheckResult> CheckAsync(HttpClient http, UpdatePaths paths, SemVersion current, bool prereleases, CancellationToken cancel, bool requireLauncherProtocol = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(current);
        var client = new GitHubReleaseClient(http, EditorRelease.Source, paths.Cache);
        ReleaseFeed feed = await client.GetReleasesAsync(cancel).ConfigureAwait(false);
        if (feed.Source == ReleaseFeedSource.None)
        {
            return new UpdateCheckResult(UpdateCheckKind.Failed, current, null, null, feed.Problem ?? "GitHub could not be reached.", feed.RateLimitReset, false);
        }

        bool includePre = prereleases || (current.IsPrerelease && !EditorRelease.IsDevVersion(current));
        var offers = new Dictionary<ReleaseInfo, UpdateOffer>();
        foreach (ReleaseInfo release in feed.Releases)
        {
            if (Offer(release) is { } offer && (!requireLauncherProtocol || LauncherIntegration.Asset(offer) is not null))
            {
                offers[release] = offer;
            }
        }

        bool cached = feed.Source == ReleaseFeedSource.Cache;
        VersionedRelease? latest = ReleasePicker.Latest(offers.Keys, includePre);
        if (latest is null)
        {
            return new UpdateCheckResult(UpdateCheckKind.UpToDate, current, null, null, feed.Problem, feed.RateLimitReset, cached);
        }

        UpdateOffer best = offers[latest.Release];
        return latest.Version > current
            ? new UpdateCheckResult(UpdateCheckKind.Available, current, best, latest.Version, feed.Problem, feed.RateLimitReset, cached)
            : new UpdateCheckResult(UpdateCheckKind.UpToDate, current, null, latest.Version, feed.Problem, feed.RateLimitReset, cached);
    }

    /// <summary>The release as an offer for this platform: its archive under the exact name, and that archive's
    /// checksum file. Null when either is missing (an archive without a published checksum is never installed).</summary>
    public static UpdateOffer? Offer(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Draft || !SemVersion.TryParse(release.TagName, out SemVersion? version))
        {
            return null;
        }

        var versioned = new VersionedRelease(version, release);
        string name = EditorRelease.AssetName(version);
        ReleaseAsset? archive = release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        ReleaseAsset? checksum = archive is null ? null : ReleasePicker.ChecksumAsset(versioned, archive);
        return archive is null || checksum is null ? null : new UpdateOffer(versioned, archive, checksum);
    }

    /// <summary>"1.2 MB", "112 MB".</summary>
    public static string Size(long bytes) =>
        bytes >= 10L << 20 ? $"{bytes >> 20} MB"
        : bytes >= 1L << 20 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1 << 20):0.0} MB")
        : $"{Math.Max(1, bytes >> 10)} KB";
}
