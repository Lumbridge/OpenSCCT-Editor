using OpenScct.Content.Json;

namespace OpenScct.Editor.Core.Scripting.Live;

/// <summary>
/// Something that answers scripting API calls: an <see cref="ApiHost"/> over a session in this process, the editor
/// app's live host, or a <see cref="LiveClient"/> talking to a running editor. The JSON-lines server takes
/// any endpoint, so <c>ose serve</c> works the same whether the map is open here or in the app.
/// </summary>
public interface IApiEndpoint
{
    /// <summary>The methods this endpoint answers (the describe call lists them).</summary>
    IReadOnlyList<ApiMethod> Methods { get; }

    ApiReply Call(string method, SvJsonObject? args);
}
