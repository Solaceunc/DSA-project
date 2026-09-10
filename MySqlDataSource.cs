using System.Diagnostics;
using MySqlConnector;

namespace MiniSearchEngine;

/// <summary>
/// Seed source backed by a MariaDB / MySQL server (works with both —
/// MySqlConnector speaks the MySQL wire protocol either way).
///
/// Schema the provider ensures on connect (idempotent CREATE TABLE IF NOT EXISTS):
/// <code>
///   CREATE TABLE IF NOT EXISTS terms (
///       term       VARCHAR(100) PRIMARY KEY,   -- the word, unique key
///       definition TEXT NOT NULL               -- its dictionary entry
///   ) CHARACTER SET utf8mb4;
/// </code>
/// (The assignment's "definitions" lives on the terms row itself; the table
/// name and columns are the two-table concept collapsed into one keyed table.)
///
/// Every call is wrapped defensively: an unreachable server must never take
/// the whole search engine down — the UI simply shows zero seeded terms.
/// </summary>
public sealed class MySqlDataSource : IDataSource
{
    private readonly string _connectionString;
    private readonly string _tableName;

    /// <inheritdoc />
    public string ProviderName => "MariaDB";

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <param name="connectionString">
    /// e.g. "Server=localhost;Port=3306;Database=search_engine;User ID=root;Password=secret;"
    /// </param>
    /// <param name="tableName">Optional table override; defaults to "terms".</param>
    public MySqlDataSource(string connectionString, string tableName = "terms")
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("MariaDB connection string is empty.", nameof(connectionString));

        _connectionString = connectionString;
        _tableName = tableName;
    }

    /// <inheritdoc />
    public async Task<List<TermDefinition>> LoadTermsAsync()
    {
        var stopwatch = Stopwatch.StartNew();          // ── benchmark: seeding ──
        LastError = null;

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            // 1. Make sure the schema exists — idempotent, safe on every boot.
            await EnsureTablesAsync(connection);

            // 2. Read every saved word/definition pair.
            var terms = new List<TermDefinition>();
            var sql = $"SELECT term, definition FROM `{_tableName}`";

            await using var command = new MySqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var term = reader.GetString(0).Trim();
                var definition = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                if (term.Length > 0)
                    terms.Add(new TermDefinition(term, definition));
            }

            stopwatch.Stop();
            Console.WriteLine(
                $"[seed] MariaDB: loaded {terms.Count} term(s) in {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
            return terms;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LastError = $"MariaDB seed failed: {ex.Message}";
            Console.WriteLine($"[seed] {LastError}");
            return [];                                  // app stays fully functional
        }
    }

    /// <summary>Creates the terms table when it is missing (and the database row format).</summary>
    private async Task EnsureTablesAsync(MySqlConnection connection)
    {
        const string createSql = """
            CREATE TABLE IF NOT EXISTS `{0}` (
                term       VARCHAR(100) NOT NULL PRIMARY KEY,
                definition TEXT         NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;

        await using var create = new MySqlCommand(string.Format(createSql, _tableName), connection);
        await create.ExecuteNonQueryAsync();
    }
}
