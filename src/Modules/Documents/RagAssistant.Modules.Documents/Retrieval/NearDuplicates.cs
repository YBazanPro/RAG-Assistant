namespace RagAssistant.Modules.Documents.Retrieval;

/// <summary>Word 3-gram Jaccard similarity between passages (spec 011, pipeline step 6).</summary>
internal static class NearDuplicates
{
    private const int ShingleSize = 3;

    /// <summary>Lowercased words split on anything that is not a letter or digit; fewer than 3 words form one shingle.</summary>
    public static HashSet<string> Shingles(string text)
    {
        var words = Words(text);
        if (words.Count == 0)
        {
            return [];
        }

        if (words.Count < ShingleSize)
        {
            return [string.Join(' ', words)];
        }

        var shingles = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= words.Count - ShingleSize; i++)
        {
            shingles.Add(string.Join(' ', words[i], words[i + 1], words[i + 2]));
        }

        return shingles;
    }

    /// <summary>|A ∩ B| / |A ∪ B|; two empty sets share nothing, so they are never duplicates.</summary>
    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var shared = small.Count(large.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                words.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }
        }

        return words;
    }
}
