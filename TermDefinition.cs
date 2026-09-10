namespace MiniSearchEngine;

/// <summary>
/// One external seed entry: a single word plus its dictionary definition.
///
/// Seeds come from the configured data source (MariaDB or GitHub raw JSON)
/// and are loaded once during app startup (and on every re-index) to:
///   1. INSERT the word into the Trie — the word becomes searchable and
///      autocompletable even when it does not appear in any .txt file;
///   2. store its definition for GET /api/definition?word=…
/// </summary>
/// <param name="Term">The word, e.g. "algorithm" (sanitized on ingestion).</param>
/// <param name="Definition">Plain-text explanation shown in the UI.</param>
public sealed record TermDefinition(string Term, string Definition);
