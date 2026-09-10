namespace MiniSearchEngine;

/// <summary>
/// Abstraction over the two Phase-2 seed sources:
///   • <see cref="MySqlDataSource"/>       — local/remote MariaDB or MySQL
///   • <see cref="GitHubJsonDataSource"/> — a public raw JSON file on GitHub
///
/// The selected implementation is registered in Program.cs and awaited once
/// during startup, so <see cref="SearchEngine"/> is seeded before the first
/// HTTP request is served. Loading must never throw out of startup: sources
/// return an empty list on failure and log the reason, keeping the app usable.
/// </summary>
public interface IDataSource
{
    /// <summary>Human-readable provider name ("MariaDB" or "GitHub").</summary>
    string ProviderName { get; }

    /// <summary>
    /// Loads every word/definition pair the provider has. Never throws —
    /// on any error the returned list is empty and <see cref="LastError"/>
    /// explains what went wrong (surfaced via /api/stats).
    /// </summary>
    Task<List<TermDefinition>> LoadTermsAsync();

    /// <summary>Failure reason of the last <see cref="LoadTermsAsync"/> call, or null when it succeeded.</summary>
    string? LastError { get; }
}
