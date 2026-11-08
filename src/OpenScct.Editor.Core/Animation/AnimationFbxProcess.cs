using System.Diagnostics;

namespace OpenScct.Editor.Core.Animation;

public static class AnimationFbxProcess
{
    public static string Convert(string executable, string? appFolder, string input)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenSCCT-Animation", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "model.glb");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--headless");
        if (appFolder is not null) { start.ArgumentList.Add("--path"); start.ArgumentList.Add(Path.GetFullPath(appFolder)); }
        foreach (string arg in new[] { "--", "--animation-fbx=" + Path.GetFullPath(input), "--animation-output=" + output }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new IOException("Cannot start the FBX importer.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) { process.Kill(true); throw new IOException("FBX import timed out."); }
        string error = stderr.GetAwaiter().GetResult(); _ = stdout.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidDataException("FBX import failed: " + error);
        return output;
    }
}
