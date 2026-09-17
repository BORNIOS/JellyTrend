using System;
using System.Text.Json;
using Jellyfin.Plugin.JellyTrend;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// La configuración es lo que el panel guarda y lo que el plugin lee al arrancar: los valores
/// por defecto tienen que ser usables sin tocar nada y el ida y vuelta por JSON no debe perder
/// ningún campo (así se persiste a través de la API de Jellyfin).
/// </summary>
public sealed class ConfigurationTests
{
    [Fact]
    public void DefaultsAreUsableWithNoConfiguration()
    {
        var config = new PluginConfiguration();

        Assert.Equal(PluginConfiguration.DefaultChannelName, config.ChannelName);
        Assert.Equal(PluginConfiguration.DefaultRecommendationChannelName, config.RecommendationChannelName);
        Assert.True(config.MaxItems > 0);
        Assert.True(config.RecommendationMaxItems > 0);
        Assert.True(config.EnableBannerMode);
        Assert.True(config.EnableRecommendationRow);
        Assert.Empty(config.TmdbApiKey);
    }

    [Fact]
    public void JsonRoundTripKeepsEverySetting()
    {
        var original = new PluginConfiguration
        {
            TmdbApiKey = "clave-de-prueba",
            TmdbLanguage = "es-MX",
            TmdbRegion = "MX",
            MaxItems = 33,
            RecommendationMaxItems = 7,
            EnableBannerMode = false,
            EnableTrendingSeries = false,
            EnableChannel = false,
            ChannelName = "Canal de prueba",
            EnableRecommendationRow = false,
            RecommendationChannelName = "Recomendados de prueba"
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<PluginConfiguration>(json);

        Assert.NotNull(restored);
        Assert.Equal(original.TmdbApiKey, restored!.TmdbApiKey);
        Assert.Equal(original.TmdbLanguage, restored.TmdbLanguage);
        Assert.Equal(original.TmdbRegion, restored.TmdbRegion);
        Assert.Equal(original.MaxItems, restored.MaxItems);
        Assert.Equal(original.RecommendationMaxItems, restored.RecommendationMaxItems);
        Assert.Equal(original.EnableBannerMode, restored.EnableBannerMode);
        Assert.Equal(original.EnableTrendingSeries, restored.EnableTrendingSeries);
        Assert.Equal(original.EnableChannel, restored.EnableChannel);
        Assert.Equal(original.ChannelName, restored.ChannelName);
        Assert.Equal(original.EnableRecommendationRow, restored.EnableRecommendationRow);
        Assert.Equal(original.RecommendationChannelName, restored.RecommendationChannelName);
    }

    [Fact]
    public void ChannelNamesAreNeverBlank()
    {
        var config = new PluginConfiguration();

        Assert.False(string.IsNullOrWhiteSpace(config.ChannelName));
        Assert.False(string.IsNullOrWhiteSpace(config.RecommendationChannelName));
        Assert.NotEqual(config.ChannelName, config.RecommendationChannelName);
    }
}
