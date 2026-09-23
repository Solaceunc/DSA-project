using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using MiniSearchEngine.Models;

namespace MiniSearchEngine;

/// <summary>
/// The complete in-memory search engine.
///
/// Data structures (all hand-built for the assignment):
/// ─────────────────────────────────────────────────────────────
/// • Inverted index (hash table):
///       Dictionary&lt;string, List&lt;SearchResult&gt;&gt;
///   maps each sanitized word to a postings list — one entry per document
///   containing that word, carrying occurrence count + line numbers.
///
/// • Document store (hash table):
///       Dictionary&lt;string, List&lt;string&gt;&gt;
///   full path -> ORIGINAL text lines, kept only to build ~100-char
///   highlighted snippets at query time without re-reading the disk.
///
/// • Trie (prefix tree): every unique vocabulary word, powering autocomplete.
///
/// Normalization: documents and user queries run through the SAME pipeline
/// (<see cref="Tokenizer.Tokenize"/>) so they share one vocabulary space.
///
/// Benchmarks: <see cref="Stopwatch"/> wraps every expensive operation and the
/// exact millisecond latencies travel to the UI in each API response.
/// </summary>
public sealed class SearchEngine
{
    /// <summary>Target snippet length in characters (assignment: ~100 chars).</summary>
    private const int SnippetLength = 100;

    /// <summary>Maximum snippets returned per matching document.</summary>
    private const int MaxSnippetsPerDocument = 3;

    /// <summary>How many autocomplete words the Trie returns at most.</summary>
    private const int MaxAutocompleteSuggestions = 10;

    // ─────────────────────────────── state ───────────────────────────────

    /// <summary>THE inverted index: sanitized word -> postings (one per document).</summary>
    private Dictionary<string, List<SearchResult>> _invertedIndex =
        new(StringComparer.Ordinal);

    /// <summary>Document store: full path -> original lines (for snippet building).</summary>
    private Dictionary<string, List<string>> _documentLines =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every unique indexed word, for the Trie and /api/stats.</summary>
    private HashSet<string> _vocabulary = new(StringComparer.Ordinal);

    /// <summary>Hand-built prefix tree over the vocabulary (swapped on re-index).</summary>
    private Trie _trie = new();

    /// <summary>Folder that was indexed; empty string until the first indexing.</summary>
    public string FolderPath { get; private set; } = string.Empty;

    /// <summary>True once POST /api/index has completed successfully.</summary>
    public bool IsIndexed { get; private set; }

    /// <summary>Exact latency of the last index build, in milliseconds.</summary>
    public double LastIndexTimeMs { get; private set; }

    /// <summary>UTC timestamp of the last successful index build.</summary>
    public DateTime? LastIndexedUtc { get; private set; }

    /// <summary>Total tokens that survived sanitization during the last index build.</summary>
    public int TotalTokens { get; private set; }

    /// <summary>Single lock guarding all mutable engine state (index, trie, stats).</summary>
    private readonly object _lock = new();

    // ─────────────────────────────── indexing ───────────────────────────────

    /// <summary>
    /// POST /api/index pipeline:
    ///   1. crawl the folder for *.txt files
    ///   2. read every file (originals kept for snippets)
    ///   3. sanitize + tokenize into the inverted index, vocabulary and Trie
    ///   4. atomically swap the freshly built state into the engine
    /// Returns an <see cref="IndexResponse"/> with the exact Stopwatch latency.
    /// </summary>
    public IndexResponse BuildIndex(string folderPath)
    {
        var stopwatch = Stopwatch.StartNew();          // ── benchmark: indexing ──

        try
        {
            // Normalize relative paths like "./documents" into absolute form.
            var fullPath = Path.GetFullPath(folderPath);

            if (!Directory.Exists(fullPath))
            {
                stopwatch.Stop();
                return new IndexResponse(false, fullPath, 0, 0, 0,
                    stopwatch.Elapsed.TotalMilliseconds,
                    $"Folder not found: {fullPath}");
            }

            // ── 1. crawl: every *.txt in the folder (non-recursive by design) ──
            var filePaths = Directory
                .EnumerateFiles(fullPath, "*.txt", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ── 2 + 3. read, sanitize and build ALL structures from scratch ──
            var index = new Dictionary<string, List<SearchResult>>(StringComparer.Ordinal);
            var lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var vocabulary = new HashSet<string>(StringComparer.Ordinal);
            var trie = new Trie();
            var totalTokens = 0;

            foreach (var filePath in filePaths)
            {
                var documentName = Path.GetFileName(filePath);
                var rawLines = File.ReadAllLines(filePath).ToList();
                lines[filePath] = rawLines;

                // ── the inverted-index build loop ──
                for (var lineIndex = 0; lineIndex < rawLines.Count; lineIndex++)
                {
                    var lineNumber = lineIndex + 1;    // human-readable, 1-based

                    // Same sanitization as queries: strip punctuation, lowercase,
                    // drop stop-words (see Tokenizer).
                    foreach (var token in Tokenizer.Tokenize(rawLines[lineIndex]))
                    {
                        totalTokens++;
                        vocabulary.Add(token);         // unique words for the Trie

                        // One postings entry per (word, document) pair.
                        if (!index.TryGetValue(token, out var postings))
                        {
                            postings = new List<SearchResult>();
                            index[token] = postings;
                        }

                        var last = postings.Count > 0 ? postings[^1] : null;
                        if (last is null || !string.Equals(last.FilePath, filePath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            last = new SearchResult(documentName, filePath);
                            postings.Add(last);
                        }
                        last.RecordOccurrence(lineNumber);
                    }
                }
            }

            // Build the Trie from the final vocabulary with true corpus counts.
            foreach (var word in vocabulary)
                trie.Insert(word, index[word].Sum(p => p.OccurrenceCount));

            // Phase 2: re-apply external definitions into the freshly built Trie,
            // so a rebuild never drops seeded vocabulary.
            foreach (var entry in _definitions.Values)
                trie.Insert(entry.Term, 1);

            stopwatch.Stop();                          // ── benchmark result ──

            // ── 4. atomic swap: readers never observe a half-built index ──
            lock (_lock)
            {
                _invertedIndex = index;
                _documentLines = lines;
                _vocabulary = vocabulary;
                _trie = trie;
                FolderPath = fullPath;
                IsIndexed = true;
                TotalTokens = totalTokens;
                LastIndexTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                LastIndexedUtc = DateTime.UtcNow;
            }

            return new IndexResponse(true, fullPath, filePaths.Count, totalTokens,
                vocabulary.Count, LastIndexTimeMs);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new IndexResponse(false, folderPath, 0, 0, 0,
                stopwatch.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    // ──────────────────────────────── search ────────────────────────────────

    /// <summary>
    /// GET /api/search pipeline: sanitize the query, auto-detect the boolean
    /// mode, rank by term frequency, and extract highlighted snippets.
    /// Modes:
    ///   • single keyword            e.g.  "algorithm"
    ///   • multi-word AND (default)  e.g.  "graph sorting"   -> docs with BOTH
    ///   • explicit OR operator      e.g.  "graph or tree"  -> docs with EITHER
    ///   • forced mode via ?mode=or  (API-level override, same union matching)
    /// A standalone "or"/"and" token is consumed as an OPERATOR and is never
    /// searched as an ordinary word.
    /// </summary>
    public SearchResponse Search(string query, string? forcedMode = null)
    {
        var stopwatch = Stopwatch.StartNew();          // ── benchmark: search ──

        var tokens = Tokenizer.Tokenize(query).Distinct().ToList();

        // Operator parsing: strip boolean operators from the term list first.
        var hasOr = tokens.RemoveAll(t => t == "or") > 0;
        tokens.RemoveAll(t => t == "and");

        // ?mode=or forces union matching even when the query has no operator.
        if (string.Equals(forcedMode, "or", StringComparison.OrdinalIgnoreCase))
            hasOr = true;

        var terms = tokens;
        if (terms.Count == 0)
        {
            stopwatch.Stop();
            return new SearchResponse(query, "none", 0,
                stopwatch.Elapsed.TotalMilliseconds, Array.Empty<DocumentMatch>());
        }

        List<SearchResult> postings;
        lock (_lock)
        {
            postings = hasOr                 ? UnionLookup(terms)
                     : terms.Count == 1      ? SingleTermLookup(terms[0])
                     :                         MultiTermLookup(terms);
        }

        var results = RankAndBuildResults(postings, terms);
        stopwatch.Stop();                              // ── benchmark result ──

        var mode = hasOr ? "OR" : terms.Count == 1 ? "single" : "AND";
        return new SearchResponse(query, mode,
            results.Count, stopwatch.Elapsed.TotalMilliseconds, results);
    }

    /// <summary>
    /// OR (union) lookup: documents containing AT LEAST ONE of the terms.
    /// Posting lists are merged per document, keeping the highest single-term
    /// frequency as the ranking score (the union of line numbers feeds the
    /// snippet extractor). Caller must hold <see cref="_lock"/>.
    /// </summary>
    private List<SearchResult> UnionLookup(List<string> terms)
    {
        var merged = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (!_invertedIndex.TryGetValue(term, out var postings))
                continue;
            foreach (var posting in postings)
            {
                if (!merged.TryGetValue(posting.FilePath, out var existing) ||
                    posting.OccurrenceCount > existing.OccurrenceCount)
                {
                    merged[posting.FilePath] = posting;
                }
            }
        }
        return merged.Values.ToList();
    }

    /// <summary>
    /// Shared post-processing: rank by term frequency (relevance score), then
    /// extract up to three ~100-char &lt;mark&gt;-highlighted snippets per document.
    /// </summary>
    private List<DocumentMatch> RankAndBuildResults(
        List<SearchResult> postings, List<string> terms)
    {
        // Relevance ranking: total term frequency inside the document, highest first.
        var ranked = postings
            .OrderByDescending(p => p.OccurrenceCount)
            .ThenBy(p => p.DocumentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<DocumentMatch>(ranked.Count);
        foreach (var posting in ranked)
        {
            List<string> snippets;
            lock (_lock)
            {
                snippets = BuildSnippets(posting.FilePath, terms);
            }
            results.Add(new DocumentMatch(
                posting.DocumentName,
                posting.FilePath,
                posting.OccurrenceCount,               // relevance = term frequency
                posting.OccurrenceCount,
                posting.LineNumbers,
                snippets));
        }
        return results;
    }

    /// <summary>Single keyword: one O(1) hash-table lookup into the inverted index.</summary>
    private List<SearchResult> SingleTermLookup(string term)
        => _invertedIndex.TryGetValue(term, out var postings)
            ? postings
            : new List<SearchResult>();

    /// <summary>
    /// AND matching: intersect the postings of every term so only documents
    /// containing ALL the words survive. Starts from the rarest term (smallest
    /// postings list) — the classic inverted-index intersection optimization.
    /// The surviving documents get the SUM of all matched term frequencies as
    /// their ranking score.
    /// </summary>
    private List<SearchResult> MultiTermLookup(List<string> terms)
    {
        // Gather each term's postings keyed by file path.
        var perTerm = new List<Dictionary<string, SearchResult>>(terms.Count);
        foreach (var term in terms)
        {
            var byFile = (_invertedIndex.TryGetValue(term, out var postings)
                    ? postings : Enumerable.Empty<SearchResult>())
                .ToDictionary(p => p.FilePath, p => p, StringComparer.OrdinalIgnoreCase);
            perTerm.Add(byFile);
        }

        // Intersect file paths, seeding from the rarest term first.
        var order = perTerm
            .Select((dict, i) => (dict, i))
            .OrderBy(x => x.dict.Count)
            .ToList();

        var result = new List<SearchResult>();
        foreach (var (candidatePath, _) in order[0].dict)
        {
            if (!order.Skip(1).All(x => x.dict.ContainsKey(candidatePath)))
                continue;                              // document missing a term

            // Merge the matched postings: union of line numbers, combined score.
            var best = order[0].dict[candidatePath];
            var merged = new SearchResult(best.DocumentName, best.FilePath);
            foreach (var line in order.SelectMany(x => x.dict[candidatePath].LineNumbers)
                                      .Distinct()
                                      .OrderBy(n => n))
            {
                merged.RecordOccurrence(line);
            }

            var combinedScore = order.Sum(x => x.dict[candidatePath].OccurrenceCount);
            merged.SetCombinedScore(combinedScore);
            result.Add(merged);
        }
        return result;
    }

    // ───────────────────────────── autocomplete ─────────────────────────────

    /// <summary>
    /// GET /api/autocomplete: walks the Trie with the sanitized prefix and
    /// returns the top completions ranked by corpus frequency.
    /// </summary>
    public AutocompleteResponse Autocomplete(string prefix)
    {
        var stopwatch = Stopwatch.StartNew();          // ── benchmark: query ──

        var normalized = prefix.Trim().ToLowerInvariant();
        var words = _trie.GetWordsWithPrefix(normalized, MaxAutocompleteSuggestions);
        var suggestions = words
            .Select(w => new WordSuggestion(w.Word, w.Frequency))
            .ToList();

        stopwatch.Stop();                              // ── benchmark result ──
        return new AutocompleteResponse(normalized,
            stopwatch.Elapsed.TotalMilliseconds, suggestions);
    }

    // ──────────────────────────────── stats ────────────────────────────────

    /// <summary>GET /api/stats payload — engine state without leaking internals.</summary>
    public StatsResponse GetStats()
    {
        lock (_lock)
        {
            return new StatsResponse(
                IsIndexed,
                FolderPath,
                _documentLines.Count,
                _vocabulary.Count,
                TotalTokens,
                LastIndexTimeMs,
                LastIndexedUtc,

                // Names of the indexed documents, for the UI's file-switcher.
                _documentLines.Keys
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList(),

                // Phase 2: seed state for the UI.
                _definitions.Count,
                SeedProvider,
                SeedSucceeded,
                SeedError);
        }
    }

    /// <summary>Total number of distinct words in the current vocabulary.</summary>
    public int UniqueWordCount
    {
        get { lock (_lock) return _vocabulary.Count; }
    }

    /// <summary>
    /// GET /api/export payload: a portable snapshot of the whole engine —
    /// stats, every posting of the inverted index, and the definitions —
    /// straight from the real C# data structures. Static sites (GitHub
    /// Pages) embed this JSON and replay searches client-side with the
    /// same algorithms, so the demo is genuinely the engine's output.
    /// </summary>
    public IndexExport Export()
    {
        lock (_lock)
        {
            var index = _invertedIndex.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PostingExport>)kvp.Value
                    .Select(p => new PostingExport(
                        p.DocumentName, p.OccurrenceCount, p.LineNumbers))
                    .ToList(),
                StringComparer.Ordinal);

            return new IndexExport(
                DateTime.UtcNow.ToString("o"),
                GetStats(),
                _definitions.ToDictionary(
                    kvp => kvp.Key, kvp => kvp.Value.Definition,
                    StringComparer.Ordinal),
                index,
                _documentLines.ToDictionary(
                    kvp => Path.GetFileName(kvp.Key) ?? kvp.Key,
                    kvp => (IReadOnlyList<string>)kvp.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    // ─────────────────── Phase 2: external term seeding ───────────────────

    /// <summary>Phase-2 definitions keyed by sanitized term (seeds from MariaDB or GitHub).</summary>
    private readonly ConcurrentDictionary<string, TermDefinition> _definitions = new(StringComparer.Ordinal);

    /// <summary>Provider that supplied the current definitions ("MariaDB", "GitHub", or "none").</summary>
    public string SeedProvider { get; private set; } = "none";

    /// <summary>True once at least one external seed batch loaded cleanly.</summary>
    public bool SeedSucceeded { get; private set; }

    /// <summary>Last provider error, surfaced through /api/stats for easy diagnosis.</summary>
    public string? SeedError { get; private set; }

    /// <summary>
    /// Phase 2: seeds the engine from the configured external data source.
    /// Search algorithms stay untouched — a seed does exactly two things:
    ///   1. stores the definition for GET /api/definition?word=…
    ///   2. INSERTs the word into the CURRENT Trie, so it appears in
    ///      autocomplete even without a matching .txt document.
    /// Also called from <see cref="BuildIndex"/> after every re-index so a
    /// rebuild never drops externally seeded vocabulary.
    /// </summary>
    public async Task SeedAsync(IDataSource dataSource)
    {
        var stopwatch = Stopwatch.StartNew();          // ── benchmark: seeding ──

        var terms = await dataSource.LoadTermsAsync(); // never throws by contract

        var inserted = 0;
        foreach (var entry in terms)
        {
            // Same sanitization as the corpus: "Inverted Index" -> "inverted index".
            var normalized = Tokenizer.SanitizeTerm(entry.Term);
            if (normalized.Length == 0)
                continue;

            _definitions[normalized] = new TermDefinition(normalized, entry.Definition);
            _trie.Insert(normalized, 1);               // O(L), merges into any existing node
            inserted++;
        }

        stopwatch.Stop();                              // ── benchmark result ──

        lock (_lock)
        {
            SeedProvider = dataSource.ProviderName;
            SeedSucceeded = terms.Count > 0;
            SeedError = dataSource.LastError;
        }

        Console.WriteLine(
            $"[seed] {dataSource.ProviderName}: {inserted} term(s) usable, " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms total.");
    }

    /// <summary>Looks up the dictionary entry for one sanitized word (GET /api/definition).</summary>
    public DefinitionResponse? TryGetDefinition(string rawWord)
    {
        var word = Tokenizer.SanitizeTerm(rawWord);
        if (word.Length == 0)
            return null;

        lock (_lock)
        {
            return new DefinitionResponse(
                word,
                _definitions.TryGetValue(word, out var entry) ? entry.Definition : null,
                _definitions.ContainsKey(word) ? SeedProvider : "none",
                _trie.Contains(word));
        }
    }

    // ─────────────────────────── snippet builder ───────────────────────────

    /// <summary>
    /// Builds up to <see cref="MaxSnippetsPerDocument"/> snippets of about
    /// <see cref="SnippetLength"/> characters around the first whole-word match
    /// in each relevant line of the ORIGINAL text, with every matched term
    /// wrapped in &lt;mark&gt;…&lt;/mark&gt;. The window is HTML-escaped first, so the
    /// output is safe to inject with innerHTML on the client.
    /// </summary>
    private List<string> BuildSnippets(string filePath, List<string> terms)
    {
        var snippets = new List<string>();

        if (!_documentLines.TryGetValue(filePath, out var lines))
            return snippets;

        var longestTerm = terms.Max(t => t.Length);

        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();

            // Earliest whole-word occurrence of any query term on this line.
            var firstPos = terms
                .Select(t => FindWholeWord(lower, t, 0))
                .Where(pos => pos >= 0)
                .DefaultIfEmpty(-1)
                .Min();
            if (firstPos < 0)
                continue;                              // no term on this line

            // Center a ~SnippetLength-char window on the first match.
            var start = Math.Max(0, firstPos - SnippetLength / 2);
            var length = Math.Min(SnippetLength + longestTerm, line.Length - start);
            var window = line.Substring(start, length);

            var prefix = start > 0 ? "…" : string.Empty;
            var suffix = start + length < line.Length ? "…" : string.Empty;

            snippets.Add(prefix + HighlightTerms(HtmlEncode(window), terms) + suffix);

            if (snippets.Count >= MaxSnippetsPerDocument)
                break;
        }

        return snippets;
    }

    /// <summary>
    /// Case-insensitive &lt;mark&gt; highlighting over already-HTML-escaped text.
    /// A match counts only when it is a whole word (neighbors are not letters
    /// or digits), which stops short terms from lighting up inside other words.
    /// </summary>
    private static string HighlightTerms(string escapedText, List<string> terms)
    {
        var lower = escapedText.ToLowerInvariant();
        var builder = new StringBuilder(escapedText.Length);
        var i = 0;

        while (i < escapedText.Length)
        {
            // Earliest whole-word match of any term at or after position i.
            var bestPos = -1;
            var bestLen = 0;
            foreach (var term in terms)
            {
                var searchFrom = i;
                int pos;
                while ((pos = lower.IndexOf(term, searchFrom, StringComparison.Ordinal)) >= 0)
                {
                    if (IsWholeWord(lower, pos, term.Length))
                    {
                        if (bestPos < 0 || pos < bestPos)
                        {
                            bestPos = pos;
                            bestLen = term.Length;
                        }
                        break;
                    }
                    searchFrom = pos + 1;              // mid-word hit; keep scanning
                }
            }

            if (bestPos < 0)
            {
                builder.Append(escapedText[i..]);
                break;
            }

            builder.Append(escapedText[i..bestPos]);
            builder.Append("<mark>");
            builder.Append(escapedText.Substring(bestPos, bestLen));
            builder.Append("</mark>");
            i = bestPos + bestLen;
        }

        return builder.ToString();
    }

    /// <summary>Finds the first whole-word occurrence of <paramref name="term"/>.</summary>
    private static int FindWholeWord(string lowerText, string term, int startIndex)
    {
        var searchFrom = startIndex;
        while (searchFrom <= lowerText.Length - term.Length)
        {
            var pos = lowerText.IndexOf(term, searchFrom, StringComparison.Ordinal);
            if (pos < 0)
                return -1;
            if (IsWholeWord(lowerText, pos, term.Length))
                return pos;
            searchFrom = pos + 1;
        }
        return -1;
    }

    /// <summary>True when text[pos..pos+length] is bounded by non-letter/digit chars.</summary>
    private static bool IsWholeWord(string text, int pos, int length)
    {
        var beforeIsBoundary = pos == 0 || !char.IsLetterOrDigit(text[pos - 1]);
        var after = pos + length;
        var afterIsBoundary = after >= text.Length || !char.IsLetterOrDigit(text[after]);
        return beforeIsBoundary && afterIsBoundary;
    }

    /// <summary>Minimal, dependency-free HTML encoder for snippet text.</summary>
    private static string HtmlEncode(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
