using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTrend.ExternalAPI;

/// <summary>
/// Paged response envelope returned by the TMDB v3 API.
/// </summary>
public sealed class TmdbPagedResponse
{
    /// <summary>
    /// Gets the page of results.
    /// </summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<TmdbItem> Results { get; init; } = new List<TmdbItem>();

    /// <summary>
    /// Gets or sets the total number of pages available.
    /// </summary>
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    /// <summary>
    /// Gets or sets the total number of results available.
    /// </summary>
    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}
