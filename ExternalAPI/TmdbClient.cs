using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.ExternalAPI;

/// <summary>
/// Thin wrapper around the TMDB v3 REST API.
/// Uses IHttpClientFactory so the underlying SocketsHttpHandler is shared/pooled.
/// </summary>
public sealed class TmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbClient> _logger;

    public TmdbClient(IHttpClientFactory httpClientFactory, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<List<TmdbItem>> GetTrendingMoviesAsync(
        string apiKey, int maxItems, string language, string region, CancellationToken cancellationToken)
        => FetchTrendingAsync("movie", apiKey, maxItems, language, region, cancellationToken);

    public Task<List<TmdbItem>> GetTrendingTvAsync(
        string apiKey, int maxItems, string language, string region, CancellationToken cancellationToken)
        => FetchTrendingAsync("tv", apiKey, maxItems, language, region, cancellationToken);

    private async Task<List<TmdbItem>> FetchTrendingAsync(
        string mediaType, string apiKey, int maxItems,
        string language, string region, CancellationToken cancellationToken)
    {
        var results = new List<TmdbItem>();
        var page = 1;

        using var client = _httpClientFactory.CreateClient();

        // Build optional query string extras (language / region are empty → omitted)
        var extras = string.Empty;
        if (!string.IsNullOrWhiteSpace(language)) extras += $"&language={Uri.EscapeDataString(language)}";
        if (!string.IsNullOrWhiteSpace(region))   extras += $"&region={Uri.EscapeDataString(region)}";

        while (results.Count < maxItems)
        {
            var url = $"{BaseUrl}/trending/{mediaType}/week?api_key={apiKey}&page={page}{extras}";

            TmdbPagedResponse? response;
            try
            {
                response = await client.GetFromJsonAsync<TmdbPagedResponse>(url, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyTrend: Failed to fetch trending {MediaType} (page {Page})", mediaType, page);
                break;
            }

            if (response?.Results is not { Count: > 0 })
                break;

            results.AddRange(response.Results);

            if (page >= response.TotalPages)
                break;

            page++;
        }

        return results.Count > maxItems ? results.GetRange(0, maxItems) : results;
    }
}
