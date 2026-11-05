namespace OpenScctEditor.Panels;

/// <summary>Search helpers: multi-word filters (every word must match somewhere, as in RE+'s outliner and property
/// filter) and a fuzzy score for the command palette.</summary>
internal static class TextMatch
{
    public static string[] Words(string filter) =>
        filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when every word is a substring of the text, ignoring case.</summary>
    public static bool AllWords(string[] words, string text)
    {
        foreach (string w in words)
        {
            if (!text.Contains(w, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A fuzzy match score (higher is better), or null when the query's letters do not appear in order.
    /// Consecutive letters, word starts and an early first match score higher.</summary>
    public static int? Fuzzy(string query, string text)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        int score = 0, qi = 0, run = 0, first = -1;
        for (int i = 0; i < text.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(query[qi]))
            {
                run = 0;
                continue;
            }

            if (first < 0)
            {
                first = i;
            }

            bool wordStart = i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
            run++;
            score += 1 + run * 2 + (wordStart ? 6 : 0);
            qi++;
        }

        if (qi < query.Length)
        {
            return null;
        }

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return score - Math.Min(first, 20) - text.Length / 10;
    }

    /// <summary>The best fuzzy score over several space-separated query words (all must match), or null.</summary>
    public static int? FuzzyWords(string query, string text)
    {
        int total = 0;
        foreach (string w in Words(query))
        {
            if (Fuzzy(w, text) is not { } s)
            {
                return null;
            }

            total += s;
        }

        return total;
    }
}
