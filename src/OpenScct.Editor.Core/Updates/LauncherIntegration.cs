using System.Text.Json;
using OpenScct.Launcher.GitHub;
using OpenScct.Launcher.Versioning;

namespace OpenScct.Editor.Core.Updates;

/// <summary>The small, versioned contract shared with the Windows launcher.</summary>
public static class LauncherIntegration
{
    public const int Protocol = 1;
    public const string ManagedMark = ".launcher-managed";
    public const string MinimumGameVersion = "0.8.117";

    public static bool IsManaged(string install) => File.Exists(Path.Combine(install, ManagedMark));

    public static string AssetName(SemVersion version) => $"{EditorRelease.AssetPrefix}{version}-{EditorRelease.Platform}.launcher.json";

    public static ReleaseAsset? Asset(UpdateOffer offer) => offer.Release.Release.Assets.FirstOrDefault(a =>
        string.Equals(a.Name, AssetName(offer.Version), StringComparison.OrdinalIgnoreCase));

    public static string? Problem(InstallInfo info) => info.LauncherProtocol != Protocol
        ? "This editor package does not support this launcher's update protocol."
        : !SemVersion.TryParse(info.MinimumGameVersion, out _) ? "The editor package has no valid minimum game version." : null;

    public static async Task ValidateOfferAsync(HttpClient http, GitHubReleaseClient client, UpdateOffer offer, CancellationToken cancel)
    {
        ReleaseAsset asset = Asset(offer) ?? throw new UpdateException("This release does not support launcher-managed installation.");
        using HttpRequestMessage request = client.CreateAssetRequest(asset);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        byte[] bytes = new byte[16 * 1024 + 1];
        int count = 0, read;
        while (count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), cancel).ConfigureAwait(false)) > 0) count += read;
        if (count == bytes.Length) throw new UpdateException("The launcher compatibility information is too large.");
        InstallInfo? info = JsonSerializer.Deserialize<InstallInfo>(bytes.AsSpan(0, count));
        if (info is null || Problem(info) is { }) throw new UpdateException(info is null ? "Missing launcher compatibility information." : Problem(info)!);
    }
}
