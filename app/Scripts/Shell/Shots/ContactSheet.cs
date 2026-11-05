using System.Net;
using System.Text;
using OpenScct.Content.Json;
using OpenScct.Editor.Core.Scripting;

namespace OpenScctEditor.Shell.Shots;

/// <summary>Writes <c>index.html</c> for a screenshot run: every shot in order with its caption, state and UI check
/// findings, so a reviewer can look through a run in a browser.</summary>
public static class ContactSheet
{
    public static void Write(string file, string map, string script, SvJsonArray shots, IReadOnlyList<string> failures)
    {
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><title>OpenSCCT Editor screenshots</title>
            <style>
            body{background:#16171b;color:#dcdde5;font:14px/1.45 system-ui,sans-serif;margin:24px}
            h1{font-size:20px;margin:0 0 4px}.meta{color:#9497a5;margin-bottom:20px}
            .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(480px,1fr));gap:20px}
            figure{margin:0;background:#1e2025;border:1px solid #2c2e36;border-radius:6px;padding:10px}
            img{width:100%;display:block;border-radius:3px;cursor:zoom-in}
            figcaption{margin-top:8px}.state{color:#9497a5;font-size:12px}
            .lint{color:#f2b740;font-size:12px;margin:6px 0 0;padding-left:18px}.log{color:#ee5c5c;font-size:12px}
            .fail{color:#ee5c5c}a{color:#5494f5}
            </style></head><body>
            """);
        html.Append($"<h1>{Enc(map)}</h1><div class=\"meta\">Script {Enc(script)} · {shots.Count} shots. ");
        html.Append("Popups are drawn inside the window in these runs, and settings are the defaults, not anyone's own.</div>");
        foreach (string f in failures)
        {
            html.Append($"<p class=\"fail\">Failed: {Enc(f)}</p>");
        }

        html.Append("<div class=\"grid\">");
        foreach (SvJsonObject shot in shots.OfType<SvJsonObject>())
        {
            string png = Path.GetFileName(ApiJson.GetString(shot, "path") ?? "");
            html.Append($"<figure id=\"{Enc(ApiJson.GetString(shot, "name") ?? "")}\"><a href=\"{Enc(png)}\"><img loading=\"lazy\" src=\"{Enc(png)}\" alt=\"\"></a>");
            html.Append($"<figcaption><b>{Enc(ApiJson.GetString(shot, "name") ?? "")}</b> {Enc(ApiJson.GetString(shot, "caption") ?? "")}");
            string size = shot["size"] is SvJsonArray s ? $"{s[0]}x{s[1]}" : "";
            html.Append($"<div class=\"state\">{size} at scale {shot["scale"]} · tool {Enc(ApiJson.GetString(shot, "tool") ?? "")} · " +
                $"{Enc(string.Join(", ", ApiJson.StringList(shot["docks"], "docks") ?? []))} · {shot["selection"]} selected</div>");
            if (ApiJson.GetString(shot, "crop") is { } crop)
            {
                html.Append($"<div class=\"state\"><a href=\"{Enc(Path.GetFileName(crop))}\">crop</a></div>");
            }

            if (shot["lint"] is SvJsonArray { Count: > 0 } lint)
            {
                html.Append("<ul class=\"lint\">");
                foreach (SvJsonObject l in lint.OfType<SvJsonObject>())
                {
                    html.Append($"<li>{Enc(ApiJson.GetString(l, "code") ?? "")}: {Enc(ApiJson.GetString(l, "message") ?? "")} <span class=\"state\">{Enc(ApiJson.GetString(l, "path") ?? "")}</span></li>");
                }

                html.Append("</ul>");
            }

            foreach (string line in ApiJson.StringList(shot["log"], "log") ?? [])
            {
                html.Append($"<div class=\"log\">{Enc(line)}</div>");
            }

            html.Append("</figcaption></figure>");
        }

        html.Append("</div></body></html>\n");
        File.WriteAllText(file, html.ToString());
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
