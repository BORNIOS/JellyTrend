using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTrend.ExternalAPI;

public sealed class TmdbPagedResponse
{
    [JsonPropertyName("results")]
    public List<TmdbItem> Results { get; set; } = new();

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

public sealed class TmdbItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Movie title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>TV show name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>"movie" or "tv" — present in multi-type trending results.</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    public string DisplayTitle => Title ?? Name ?? string.Empty;
}
