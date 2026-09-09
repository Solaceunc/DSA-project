# 🔎 Mini Search Engine — C# .NET 8

A standalone keyword search engine that indexes and searches local `.txt` files, built as a
complete Data Structures & Algorithms showcase on top of ASP.NET Core Minimal APIs.

- **Inverted Index** (hash table) — `Dictionary<string, List<SearchResult>>` mapping every
  sanitized word to its postings (documents, occurrence counts, line numbers).
- **Trie / Prefix Tree** — implemented 100% from scratch (`TrieNode` + `Trie`, no libraries)
  over the full vocabulary, powering live autocomplete.
- **TF ranking** — documents ranked by term frequency; multi-word **AND** intersection and
  **OR** union matching.
- **Stopwatch benchmarks** — exact `indexTimeMs` / `searchTimeMs` returned by every call and
  displayed live in the UI.
- **Highlighted snippets** — ~100-char previews with `<mark>` tags, extracted from the
  original (non-sanitized) text.

## Quick start

```bash
dotnet run
```

Kestrel binds to **http://localhost:5000** and the app **auto-opens your default browser**
(`Process.Start`) as soon as the server answers its own `/health` probe. On first page load
the GUI indexes the bundled `./documents` folder automatically.

Put your own `.txt` files in `documents/` (or type any absolute folder path in the GUI and
press **Index folder**).

> Requires the .NET 8 SDK. The project also sets `RollForward: LatestMajor`, so newer SDKs
> (9/10+) can run it unchanged.

## API

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/index?folder={path}` | Crawl the folder, sanitize text, build the inverted index + Trie. Default folder: `./documents`. |
| `GET`  | `/api/search?q={query}&mode=and\|or` | TF-ranked matches, line numbers, highlighted snippets, `searchTimeMs`. `mode=or` forces union matching; otherwise a standalone `or` token in the query switches modes automatically. |
| `GET`  | `/api/autocomplete?prefix={p}` | Top 10 Trie completions with corpus frequencies + `queryTimeMs`. |
| `GET`  | `/api/stats` | Files indexed, unique words, total tokens, index build time. |
| `GET`  | `/health` | Liveness probe used by the auto-launcher. |

### Example

```bash
curl -X POST http://localhost:5000/api/index
curl "http://localhost:5000/api/search?q=hash+table"
curl "http://localhost:5000/api/search?q=graph+or+tree"        # explicit OR operator
curl "http://localhost:5000/api/search?q=graph+tree&mode=or"   # forced OR
curl "http://localhost:5000/api/autocomplete?prefix=sort"
curl http://localhost:5000/api/stats
```

```json
{
  "query": "hash table",
  "mode": "AND",
  "totalMatches": 3,
  "searchTimeMs": 0.28,
  "results": [
    {
      "documentName": "algorithms.txt",
      "relevanceScore": 6,
      "occurrenceCount": 6,
      "lineNumbers": [9, 10, 24],
      "snippets": ["A <mark>hash</mark> <mark>table</mark> maps keys to buckets…"]
    }
  ]
}
```

## Text sanitization pipeline

1. **Tokenize** — a Unicode regex (`[\p{L}\p{N}]+`) splits on every non-letter/digit, which
   strips all punctuation in one pass.
2. **Lowercase** — `ToLowerInvariant()` on both documents and queries.
3. **Stop-words** — `a, an, the, is, of, to, in, on, at, for` are ignored by the index.

The same pipeline runs for indexing *and* searching, so both live in one normalized
vocabulary space.

## Data structures & algorithms (teaching notes)

| Structure | File | Complexity |
|-----------|------|------------|
| Inverted index (hash table of postings) | `SearchEngine.cs` | O(1) average lookup per term |
| Trie with explicit `TrieNode` pointers  | `Trie.cs`, `TrieNode.cs` | O(L) insert/lookup; prefix DFS collects the sub-trie |
| AND matching                            | `SearchEngine.MultiTermLookup` | intersection seeded from the rarest postings list |
| OR matching                             | `SearchEngine.UnionLookup` | union merge keeping the max per-document frequency |
| TF ranking                              | `SearchEngine.RankAndBuildResults` | sort by occurrence count (descending) |
| Snippet extractor                       | `SearchEngine.BuildSnippets` | ~100-char window around the first whole-word hit, HTML-escaped, `<mark>`-wrapped |

Postings (`SearchResult` in `Models.cs`) carry the **document name, file path, occurrence
count, and 1-based line numbers**. The AND merge unions the line numbers of every matched
term and sums their frequencies into the relevance score.

Threading: the index is rebuilt atomically (build fully, then swap under one lock), so
HTTP readers never observe a half-built index.

## Project layout

```
MiniSearchEngine.csproj     net8.0 web project (RollForward: LatestMajor)
Program.cs                  Minimal API host, endpoints, auto-browser launch
SearchEngine.cs             inverted index, crawler, ranking, snippets, benchmarks
Trie.cs / TrieNode.cs       from-scratch prefix tree
Tokenizer.cs                punctuation stripping, lowercasing, stop-words
Models.cs                   SearchResult posting + all API DTOs
wwwroot/index.html          Tailwind single-page GUI (autocomplete, metrics, results)
documents/                  sample corpus (4 files)
```

## Publishing to GitHub

The `gh` CLI may not be installed; either path below works.

**Option A — GitHub Desktop / web (no CLI):**
1. Initialize and commit locally (if not already done):
   ```bash
   git init
   git add .
   git commit -m "Mini Search Engine: inverted index + trie autocomplete (.NET 8)"
   ```
2. Create an empty repo on https://github.com/new (no README/gitignore — this project has them).
3. Push:
   ```bash
   git branch -M main
   git remote add origin https://github.com/<you>/mini-search-engine.git
   git push -u origin main
   ```

**Option B — GitHub CLI:**
```bash
winget install GitHub.cli          # once
gh auth login
gh repo create mini-search-engine --public --source=. --push
```

---

Built as an academic DSA showcase: every core structure is hand-implemented and commented
for classroom walkthroughs.
