using System.Diagnostics;

namespace OpenScct.Editor.Core.Animation;

public static class AnimationRuntimeProbe
{
    public static void Require(string executable)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "--headless", "--quit-after", "30", "--", "--svm-animation-probe" }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new IOException("Cannot check the selected OpenSCCT build.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new IOException("The OpenSCCT animation capability check timed out."); }
        string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        if (!output.Contains("OPENSCCT_ANIMATION_RUNTIME=1", StringComparison.Ordinal)) throw new InvalidOperationException("Choose an OpenSCCT build that contains Animation Studio runtime support.");
    }
}
