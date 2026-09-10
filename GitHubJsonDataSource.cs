using System.Diagnostics;
using System.Text.Json;

namespace MiniSearchEngine;

/// <summary>
/// Seed source backed by a raw JSON file served from GitHub
/// (e.g. https://raw.githubusercontent.com/&lt;user&gt;/&lt;repo&gt;/main/dictionary.json).
///
/// Accepted JSON shapes (deserialized case-insensitively):
/// <code>
///   [ { "term": "algorithm", "definition": "…" }, … ]            // plain array
///   { "terms": [ { "term": …, "definition": … }, … ] }           // wrapped object
/// </code>
/// Optional extra fields on each entry are ignored by the deserializer.
/// </summary>
public sealed class GitHubJsonDataSource : IDataSource
{
    private readonly HttpClient _http;
    private readonly string _rawUrl;

    /// <inheritdoc />
    public string ProviderName => "GitHub";

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <param name="httpClient">Application HttpClient (typed-client lifetime).</param>
    /// <param name="rawUrl">Full raw.githubusercontent.com URL of the dictionary file.</param>
    public GitHubJsonDataSource(HttpClient httpClient, string rawUrl)
    {
        _http = httpClient;
        _rawUrl = rawUrl;
    }

    /// <inheritdoc />
    public async Task<List<TermDefinition>> LoadTermsAsync()
    {
        var stopwatch = Stopwatch.StartNew();          // ── benchmark: seeding ──
        LastError = null;

        try
        {
            // Public raw files need no auth; still send a UA (GitHub requires one).
            using var request = new HttpRequestMessage(HttpMethod.Get, _rawUrl);
            request.Headers.UserAgent.ParseAdd("MiniSearchEngine/1.0");

            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var terms = Parse(json);

            stopwatch.Stop();
            Console.WriteLine(
                $"[seed] GitHub JSON: loaded {terms.Count} term(s) from {_rawUrl} " +
                $"in {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
            return terms;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LastError = $"GitHub seed failed: {ex.Message}";
            Console.WriteLine($"[seed] {LastError}");
            return [];                                  // app stays fully functional
        }
    }

    /// <summary>Deserializes either accepted JSON shape into term/definition pairs.</summary>
    private static List<TermDefinition> Parse(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Shape 1: a wrapped object { "terms": [ … ] }.
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var arrayElement = root.ValueKind == JsonValueKind.Array
            ? root                                       // Shape 2: plain array
            : root.TryGetProperty("terms", out var wrapped) ? wrapped
            : throw new JsonException(
                "JSON must be an array of { term, definition } or an object with a \"terms\" array.");

        return arrayElement.Deserialize<List<TermDefinition>>(options)
               ?? [];
    }
}
