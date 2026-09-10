using System.Text.RegularExpressions;

namespace MiniSearchEngine;

/// <summary>
/// Text sanitization pipeline required by the assignment:
///   1. Strip ALL punctuation — a single Unicode-aware regex keeps only
///      letters and digits, so every punctuation character becomes a split point.
///   2. Lowercase every token.
///   3. Remove the standard English stop-words.
///
/// The SAME pipeline is applied to documents AND to user queries, so both live
/// in the same normalized vocabulary space ("The", "the", "THE!" -> "the").
/// </summary>
public static partial class Tokenizer
{
    /// <summary>
    /// A run of Unicode letters (\p{L}) or digits (\p{N}) is one token.
    /// Anything that is not a letter or digit acts as a separator.
    /// Source-generated (no runtime regex compilation cost).
    /// </summary>
    /// <summary>Source-generated regex: zero runtime compilation cost (C# 12 style).</summary>
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenPattern();

    /// <summary>Standard English stop-words ignored by the index (per assignment spec).</summary>
    public static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "of", "to", "in", "on", "at", "for"
    };

        /// <summary>True when the token carries no indexing value.</summary>
    public static bool IsStopWord(string token) => StopWords.Contains(token);

    /// <summary>
    /// Phase 2: normalizes a dictionary term without stop-word removal —
    /// entries like "inverted index" contain "index" + a stop-word-safe word,
    /// and "the big O" must not lose words. Lowercases and keeps letters/digits
    /// plus internal spaces (multi-word keys allowed), trimming the result.
    /// </summary>
    public static string SanitizeTerm(string term)
    {
        if (string.IsNullOrEmpty(term))
            return string.Empty;

        var chars = term.ToLowerInvariant().ToCharArray();
        var output = new char[chars.Length];
        var length = 0;

        foreach (var ch in chars)
        {
            // Keep letters/digits as-is; collapse whitespace runs to one space.
            if (char.IsLetterOrDigit(ch))
                output[length++] = ch;
            else if (char.IsWhiteSpace(ch))
            {
                if (length > 0 && output[length - 1] != ' ')
                    output[length++] = ' ';
            }
            // every other character (punctuation) is dropped
        }

        var normalized = new string(output, 0, length).Trim();
        return normalized;
    }

    /// <summary>
    /// Tokenizes raw text into sanitized, stop-word-free, lowercase terms.
    /// Example: "The Quick-Brown_Fox!" => [ "quick", "brown", "fox" ].
    /// </summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        foreach (Match match in TokenPattern().Matches(text))
        {
            var token = match.Value.ToLowerInvariant();
            if (!IsStopWord(token))
                yield return token;
        }
    }
}
