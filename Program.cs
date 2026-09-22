using System.Diagnostics;
using MiniSearchEngine;
using MiniSearchEngine.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Mini Search Engine — ASP.NET Core Minimal API host
//
//   GET  /                     static GUI (wwwroot/index.html)
//   POST /api/index            (re)build the index over a folder of .txt files
//   GET  /api/search?q=…       keyword search with AND/OR matching + snippets
//   GET  /api/autocomplete?p=… Trie-based live word completions
//   GET  /api/definition?word= Phase 2: dictionary entry for one word
//   GET  /api/stats            index health, sizes, build latency, seed state
//
// Phase 2: on startup the configured IDataSource (MariaDB or GitHub raw JSON)
// seeds the Trie + definition store BEFORE the server accepts traffic.
//
// When started, the app opens the default browser at http://localhost:5000
// so the GUI appears without the user typing any URL (see LaunchBrowser).
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Kestrel is pinned to the assignment's address: http://localhost:5000
builder.WebHost.UseUrls("http://localhost:5000");

// One shared, thread-safe engine instance for the whole process.
builder.Services.AddSingleton<SearchEngine>();

// ─────────────── Phase 2: pick the external data seed provider ───────────────
// appsettings.json → "SeedDataSource:Provider" is "GitHub" or "MariaDB".
// Each provider gets a named HttpClient so the URL base can live in config.

var seedConfig = builder.Configuration.GetSection("SeedDataSource");
var providerName = seedConfig["Provider"] ?? "GitHub";

builder.Services.AddHttpClient("GitHub", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    // GitHub raw URLs resolve relative to the API root:
    //   https://raw.githubusercontent.com/<user>/<repo>/<branch>/
    client.BaseAddress = new Uri("https://raw.githubusercontent.com/");
});

if (providerName.Equals("MariaDB", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = seedConfig["MariaDB:ConnectionString"] ?? string.Empty;
    var table = seedConfig["MariaDB:Table"] ?? "terms";
    builder.Services.AddSingleton<IDataSource>(_ => new MySqlDataSource(connectionString, table));
}
else
{
    // GitHub raw JSON: the named "GitHub" HttpClient carries the base address;
    // the provider only needs the repository-relative raw URL from config.
    // A bundled data/dictionary.json next to the exe acts as an automatic
    // offline/private-repo fallback when the remote fetch fails.
    var rawUrl = seedConfig["GitHub:RawUrl"] ?? string.Empty;
    var bundledDictionary = Path.Combine(AppContext.BaseDirectory, "data", "dictionary.json");
    builder.Services.AddSingleton<IDataSource>(sp =>
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        return new GitHubJsonDataSource(factory.CreateClient("GitHub"), rawUrl,
                                        bundledDictionary);
    });
}

var app = builder.Build();

// Serve wwwroot (index.html at "/", css/js if added later).
app.UseDefaultFiles();
app.UseStaticFiles();

// ───────────────────────────── API endpoints ─────────────────────────────

app.MapPost("/api/index",
        (SearchEngine engine, HttpRequest request) =>
        {
            // Optional ?folder= override; defaults to the bundled ./documents.
            var folder = (string?)request.Query["folder"];
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = Path.Combine(AppContext.BaseDirectory, "documents");
                if (!Directory.Exists(folder))
                    folder = Path.GetFullPath(Path.Combine(
                        Directory.GetCurrentDirectory(), "documents"));
            }

            var result = engine.BuildIndex(folder);
            return Results.Json(result,
                statusCode: result.Success ? StatusCodes.Status200OK
                                           : StatusCodes.Status400BadRequest);
        })
    .WithName("BuildIndex");

app.MapGet("/api/search",
        (SearchEngine engine, HttpRequest request) =>
        {
            var query = (string?)request.Query["q"];
            if (string.IsNullOrWhiteSpace(query))
                return Results.Json(
                    new SearchResponse(string.Empty, "none", 0, 0,
                        Array.Empty<DocumentMatch>()),
                    statusCode: StatusCodes.Status400BadRequest);

            // ?mode=or forces union matching; without it the engine parses the
            // query itself: AND by default, OR when the token "or" appears.
            var mode = (string?)request.Query["mode"];
            return Results.Json(engine.Search(query, mode));
        })
    .WithName("Search");

app.MapGet("/api/autocomplete",
        (SearchEngine engine, HttpRequest request) =>
        {
            var prefix = (string?)request.Query["prefix"] ?? string.Empty;
            return Results.Json(engine.Autocomplete(prefix));
        })
    .WithName("Autocomplete");

// ── Phase 2: dictionary lookup, complements /api/search ──
// Returns the definition of one word (null when unknown) plus whether the
// word is in the Trie, so the UI can distinguish "no definition" from
// "unknown word entirely".
app.MapGet("/api/definition",
        (SearchEngine engine, HttpRequest request) =>
        {
            var word = (string?)request.Query["word"];
            if (string.IsNullOrWhiteSpace(word))
                return Results.Json(new DefinitionResponse(string.Empty, null, "none", false),
                    statusCode: StatusCodes.Status400BadRequest);

            var definition = engine.TryGetDefinition(word);
            return definition is null
                ? Results.Json(new DefinitionResponse(word.Trim(), null, "none", false))
                : Results.Json(definition);
        })
    .WithName("Definition");

app.MapGet("/api/stats",
        (SearchEngine engine) => Results.Json(engine.GetStats()))
    .WithName("Stats");

// Health probe used by the auto-launch logic (and handy for tooling).
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .ExcludeFromDescription();

// ─────────────── Phase 2: seed the Trie before serving traffic ───────────────
// The engine is registered but nothing is built until the first resolve —
// so resolving here performs the injection. A 15 s ceiling keeps a dead
// MariaDB server from hanging startup (the provider itself never throws).
using (var scope = app.Services.CreateScope())
{
    var engine = scope.ServiceProvider.GetRequiredService<SearchEngine>();
    var dataSource = scope.ServiceProvider.GetRequiredService<IDataSource>();

    Console.WriteLine($"[seed] Provider: {dataSource.ProviderName}");
    await engine.SeedAsync(dataSource);   // top-level await: seed before Kestrel serves
}

// ─────────────────────── auto-launch the GUI ───────────────────────
// The browser may still be starting when we fire Process.Start, so we poll
// /health on a background thread until Kestrel answers, then open the URL.

const string uiUrl = "http://localhost:5000";
_ = Task.Run(() => LaunchBrowserWhenServerReady(uiUrl));   // fire-and-forget

app.Run();

/// <summary>
/// Opens <paramref name="url"/> in the OS default browser once the web app
/// answers HTTP requests. Windows / macOS / Linux are all covered.
/// </summary>
static void LaunchBrowserWhenServerReady(string url)
{
    var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

    // Wait up to ~15 s for Kestrel to accept connections.
    for (var attempt = 0; attempt < 30; attempt++)
    {
        try
        {
            using var response = httpClient.GetAsync($"{url}/health").GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                OpenBrowser(url);
                return;
            }
        }
        catch
        {
            // Server not accepting connections yet — retry.
        }

        Thread.Sleep(500);
    }

    // Never block app startup on browser problems; report to the console only.
    Console.WriteLine($"[warn] Browser was not auto-launched; open {url} manually.");
}

/// <summary>Cross-platform default-browser launcher via Process.Start.</summary>
static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            // "UseShellExecute = true" resolves the default browser for the URL.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsLinux())
        {
            Process.Start("xdg-open", url);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start("open", url);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[warn] Could not launch browser: {ex.Message}");
        Console.WriteLine($"       Open {url} manually.");
    }
}
