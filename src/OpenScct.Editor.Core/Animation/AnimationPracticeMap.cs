using OpenScct.Editor.Core.Compile;
using OpenScct.Editor.Core.Session;

namespace OpenScct.Editor.Core.Animation;

/// <summary>Local animation playtests work with installed games, which do not ship the development TestRange.</summary>
public static class AnimationPracticeMap
{
    public static void Build(string mapsRoot)
    {
        var session = EditorSession.New("TestRange", "Animation practice");
        Geometry.Starter.AddStarterRoom(session.Project);
        var result = new MapCompiler().Compile(session.Project, Path.Combine(mapsRoot, "TestRange"), new() { FastLighting = true });
        if (!result.Ok) throw new InvalidOperationException("Could not build animation practice map: " + string.Join("; ", result.Findings.Where(f => f.Severity == Services.Severity.Error).Select(f => f.Message)));
    }
}
