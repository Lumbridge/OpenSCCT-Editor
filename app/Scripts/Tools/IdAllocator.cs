using OpenScct.Editor.Core.Geometry.Edit;
using OpenScct.Editor.Core.Model;

namespace OpenScctEditor.Tools;

/// <summary>Hands out ids no object of a map uses (ignoring case), several at a time: Stem0, Stem1... A thin name for
/// Core's <see cref="FreshIds"/>, which the tools use directly.</summary>
public sealed class IdAllocator(MapProject project)
{
    private readonly FreshIds _ids = new(project);

    public string Next(string stem) => _ids.Next(stem);
}
