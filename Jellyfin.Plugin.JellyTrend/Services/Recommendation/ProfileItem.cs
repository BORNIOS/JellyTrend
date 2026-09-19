using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// One consumed title with everything the profile can learn from it.
/// </summary>
/// <remarks>
/// The item brings the content facets and the weight brings how much it teaches (see
/// <see cref="InteractionWeight"/>). Facets that are not known for a title stay empty: the builder simply
/// ignores them, so filling more of them later enriches the profile without touching this contract.
/// </remarks>
public sealed class ProfileItem
{
    /// <summary>Gets or sets the title id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets how much this title teaches the profile.</summary>
    public double Weight { get; set; }

    /// <summary>Gets or sets the genres of the title.</summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>Gets or sets the tags or keywords of the title.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>Gets or sets the studios or production companies of the title.</summary>
    public IReadOnlyList<string> Studios { get; set; } = [];

    /// <summary>Gets or sets the directors of the title.</summary>
    public IReadOnlyList<string> Directors { get; set; } = [];

    /// <summary>Gets or sets the main cast of the title, most important first.</summary>
    public IReadOnlyList<string> Actors { get; set; } = [];

    /// <summary>Gets or sets the writers or creators of the title.</summary>
    public IReadOnlyList<string> Writers { get; set; } = [];

    /// <summary>Gets or sets the collection or franchise the title belongs to.</summary>
    public string? Collection { get; set; }

    /// <summary>Gets or sets the release decade, as "1990s".</summary>
    public string? Decade { get; set; }

    /// <summary>Gets or sets the original language or country of the title.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    public int? Runtime { get; set; }

    /// <summary>Gets or sets the community rating (0-10).</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets the popularity, when the provider reports one.</summary>
    public double? Popularity { get; set; }
}
