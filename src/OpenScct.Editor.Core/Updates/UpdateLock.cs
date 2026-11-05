namespace OpenScct.Editor.Core.Updates;

/// <summary>A lock file held open without sharing: the updates lock (<see cref="UpdatePaths.Lock"/>, while an update
/// downloads or unpacks) or an install's swap lock (<see cref="UpdatePaths.SwapLock"/>, while the helper swaps or
/// recovers it). Released when disposed, or when the process ends, however it ends.</summary>
public sealed class UpdateLock : IDisposable
{
    private readonly FileStream _file;

    private UpdateLock(FileStream file)
    {
        _file = file;
    }

    /// <summary>Takes the updates lock; null when another process holds it.</summary>
    public static UpdateLock? TryTake(UpdatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return TryTake(paths.Lock);
    }

    /// <summary>Takes the lock file <paramref name="path"/>; null when another process holds it.</summary>
    public static UpdateLock? TryTake(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            return new UpdateLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();
}
