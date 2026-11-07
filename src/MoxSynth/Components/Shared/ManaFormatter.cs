using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace MoxSynth.Components.Shared;

/// <summary>Turns "{2}{W/U}" style symbols into Mana font icons; all other text is HTML-encoded.</summary>
public static partial class ManaFormatter
{
    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex SafeClassRegex();

    public static MarkupString ToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new MarkupString("");

        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in SymbolRegex().Matches(text))
        {
            sb.Append(Encode(text[last..m.Index]));
            sb.Append(Symbol(m.Groups[1].Value));
            last = m.Index + m.Length;
        }
        sb.Append(Encode(text[last..]));
        return new MarkupString(sb.ToString());
    }

    private static string Encode(string s) => WebUtility.HtmlEncode(s).Replace("\n", "<br />");

    private static string Symbol(string code)
    {
        var cls = code.Replace("/", "").ToLowerInvariant() switch
        {
            "t" => "tap",
            "q" => "untap",
            "∞" => "infinity",
            "½" => "1-2",
            var other => other,
        };

        if (!SafeClassRegex().IsMatch(cls))
            return Encode("{" + code + "}");

        return $"<i class=\"ms ms-{cls} ms-cost\" title=\"{WebUtility.HtmlEncode(code)}\"></i>";
    }
}
