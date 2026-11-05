namespace OpenScct.Editor.Core.Services;

/// <summary>
/// The editor's cache folder (asset index, thumbnails): <c>%LOCALAPPDATA%\OpenSCCT-Editor\cache</c>, shared by every
/// editor and <c>ose</c> run of the user. <c>OSE_CACHE</c> moves it, so a background or scripted run never rewrites
/// the files the user's own editor is reading; the editor app sets it for its automated runs (headless, background,
/// <c>--run=</c>) unless the launch did.
/// </summary>
public static class EditorCache
{
    public static string Root =>
        Environment.GetEnvironmentVariable("OSE_CACHE") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSCCT-Editor", "cache");
}
