using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTrend.ExternalAPI;

/// <summary>
/// A single movie or TV show result from TMDB.
/// </summary>
public sealed class TmdbItem
{
    /// <summary>
    /// Gets or sets the TMDB id.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the movie title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the TV show name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the backdrop path.
    /// </summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>
    /// Gets or sets the poster path.
    /// </summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>
    /// Gets or sets the vote average.
    /// </summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets "movie" or "tv" — present in multi-type trending results.</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>
    /// Gets the best available display title for the item.
    /// </summary>
    public string DisplayTitle => Title ?? Name ?? string.Empty;
}
