using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Verifica el motor contra una biblioteca real exportada de PostgreSQL
/// (<c>JELLYTREND_LIBRARY_SNAPSHOT</c>, por omisión <c>D:\Jellyfin\.scratch\library.json</c>) y
/// compara la fila nueva con la que producía el motor anterior
/// (<c>JELLYTREND_PREVIOUS_ROW</c>, el JSON de recomendaciones que se quiera usar como referencia).
/// Sin esos archivos la prueba se omite: no es un dato del repositorio.
/// </summary>
public sealed class RecommendationLibrarySnapshotTests
{
    private const string SnapshotVariable = "JELLYTREND_LIBRARY_SNAPSHOT";
    private const string PreviousRowVariable = "JELLYTREND_PREVIOUS_ROW";
    private const string DefaultSnapshotPath = @"D:\Jellyfin\.scratch\library.json";
    private const int RowSize = 50;

    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationLibrarySnapshotTests"/> class.
    /// </summary>
    /// <param name="output">Test output helper.</param>
    public RecommendationLibrarySnapshotTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void RealLibraryRowFollowsTheWatchedProfile()
    {
        var snapshotPath = ResolveSnapshotPath();
        Skip.If(snapshotPath is null, $"Sin instantánea de biblioteca ({SnapshotVariable} o {DefaultSnapshotPath}).");

        var (watched, candidates, universe) = LoadLibrary(snapshotPath!);
        Assert.NotEmpty(watched);
        Assert.NotEmpty(candidates);

        var nowUtc = DateTime.UtcNow;
        var index = new FacetIndex();
        foreach (var item in universe)
        {
            index.Add(item.Genres, item.Tags, item.Studios, item.People);
        }

        var profile = TasteProfile.Build(watched, index);
        var scored = candidates
            .Select(candidate => RecommendationScorer.Score(candidate, profile, index, nowUtc))
            .ToList();

        var ids = RecommendationSelector.Select(scored, profile, RowSize);
        var byId = universe.ToDictionary(movie => movie.Id);
        var row = Tally(ids.Where(byId.ContainsKey).Select(id => byId[id]));
        var watchedTally = Tally(universe.Where(movie => watched.Any(item => item.Id == movie.Id)));

        _output.WriteLine("Perfil visto: " + Describe(watchedTally));
        _output.WriteLine("Perfil (pesos): " + string.Join(", ", profile
            .TopGenres(6)
            .Select(genre => string.Format(CultureInfo.InvariantCulture, "{0} {1:P0}", genre.Key, genre.Value))));
        _output.WriteLine($"Fila nueva ({ids.Count}): " + Describe(row));

        var previous = LoadPreviousRow();
        if (previous is not null)
        {
            var previousTally = Tally(previous.Where(byId.ContainsKey).Select(id => byId[id]));
            _output.WriteLine($"Fila anterior ({previous.Count}): " + Describe(previousTally));
        }

        // Lo que el motor viejo no respetaba: el peso del perfil manda sobre la popularidad del género.
        var topProfileGenres = profile.TopGenres(5).Select(genre => genre.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offProfile = ids
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Count(movie => !movie.Genres.Any(topProfileGenres.Contains));

        Assert.True(ids.Count >= 40, $"La fila debe llenarse: {ids.Count} de {RowSize}.");
        Assert.True(
            Count(row, "Terror") >= 30,
            $"Terror pesa 42% del perfil y debe dominar la fila: {Describe(row)}");
        Assert.True(
            Count(row, "Terror") > Count(row, "Drama"),
            $"El género dominante del perfil debe superar al más común de la biblioteca: {Describe(row)}");
        Assert.True(
            Count(row, "Romance") <= 3,
            $"Romance es el 7% del perfil y no puede pesar más que eso: {Describe(row)}");
        Assert.True(
            offProfile <= 6,
            $"Fuera del perfil solo cabe el presupuesto de exploración: {offProfile} de {ids.Count}.");
    }

    private static string? ResolveSnapshotPath()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(SnapshotVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        return File.Exists(DefaultSnapshotPath) ? DefaultSnapshotPath : null;
    }

    private static (List<TasteItem> Watched, List<CandidateItem> Candidates, List<CandidateItem> Universe) LoadLibrary(string path)
    {
        var watched = new List<TasteItem>();
        var candidates = new List<CandidateItem>();
        var universe = new List<CandidateItem>();
        var seen = new HashSet<Guid>();
        var nowUtc = DateTime.UtcNow;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = element.GetProperty("id").GetGuid();

            // La exportación puede repetir una película si el servidor tiene filas duplicadas en
            // UserData (pasa en esta base): el motor real trabaja con ids únicos, así que aquí también.
            if (!seen.Add(id))
            {
                continue;
            }

            var name = element.GetProperty("name").GetString() ?? string.Empty;
            var genres = Split(element, "genres");
            var tags = Split(element, "tags");
            var studios = Split(element, "studios");
            var people = People(element);
            var rating = Number(element, "rating");
            var premiere = Date(element, "premiere");

            var movie = new CandidateItem(id, name, null, genres, tags, studios, people, rating, premiere);
            universe.Add(movie);

            var played = Bool(element, "played");
            var position = element.TryGetProperty("pos", out var pos) && pos.ValueKind == JsonValueKind.Number ? pos.GetInt64() : 0L;
            if (!played && position <= 0)
            {
                candidates.Add(movie);
                continue;
            }

            var runtime = element.TryGetProperty("runtime", out var run) && run.ValueKind == JsonValueKind.Number ? run.GetInt64() : 0L;
            var progress = runtime > 0 ? Math.Clamp(position / (double)runtime, 0d, 1d) : 0d;
            var weight = EngagementModel.Weight(
                played,
                progress,
                Int(element, "plays", 1),
                Bool(element, "fav"),
                Date(element, "lastplayed"),
                nowUtc);

            watched.Add(new TasteItem(id, genres, tags, studios, people, weight));
        }

        return (watched, candidates, universe);
    }

    private static List<Guid>? LoadPreviousRow()
    {
        var path = Environment.GetEnvironmentVariable(PreviousRowVariable);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("ItemIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return ids.EnumerateArray().Select(id => id.GetGuid()).ToList();
    }

    private static string[] Split(JsonElement element, string property)
        => !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String
            ? []
            : (value.GetString() ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Guid[] People(JsonElement element)
    {
        if (!element.TryGetProperty("people", out var people) || people.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var person in people.EnumerateArray())
        {
            if (!person.TryGetProperty("n", out var name) || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var type = person.TryGetProperty("t", out var kind) ? kind.GetString() : null;
            if (type is not ("Actor" or "Director" or "Writer"))
            {
                continue;
            }

            ids.Add(PersonId(name.GetString() ?? string.Empty));
        }

        return [.. ids.Distinct()];
    }

    // Identificador estable por nombre: la instantánea guarda nombres, no GUID de persona.
    private static Guid PersonId(string name)
    {
        var hash = 17;
        foreach (var character in name.ToUpperInvariant())
        {
            hash = unchecked((hash * 31) + character);
        }

        return new Guid(hash, 0, 0, new byte[8]);
    }

    private static float? Number(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : null;

    private static DateTime? Date(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetDateTime()
            : null;

    private static bool Bool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement element, string property, int fallback)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static Dictionary<string, int> Tally(IEnumerable<CandidateItem> movies)
    {
        var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in movies)
        {
            foreach (var genre in movie.Genres)
            {
                tally[genre] = tally.GetValueOrDefault(genre) + 1;
            }
        }

        return tally;
    }

    private static int Count(Dictionary<string, int> tally, string genre) => tally.GetValueOrDefault(genre);

    private static string Describe(Dictionary<string, int> tally)
        => string.Join(", ", tally
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(pair => string.Format(CultureInfo.InvariantCulture, "{0} {1}", pair.Key, pair.Value)));
}
