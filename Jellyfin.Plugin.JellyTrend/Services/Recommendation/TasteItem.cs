using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// A movie the user has engaged with (finished or in progress) together with the weight that
/// engagement deserves in the taste profile.
/// </summary>
internal sealed class TasteItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TasteItem"/> class.
    /// </summary>
    /// <param name="id">Movie id.</param>
    /// <param name="genres">Genre names.</param>
    /// <param name="tags">Tag names.</param>
    /// <param name="studios">Studio names.</param>
    /// <param name="people">Ids of the cast and crew that matter (director, writer, actor).</param>
    /// <param name="weight">Engagement weight from <see cref="EngagementModel"/>.</param>
    public TasteItem(
        Guid id,
        IReadOnlyList<string> genres,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> studios,
        IReadOnlyList<Guid> people,
        double weight)
    {
        Id = id;
        Genres = genres;
        Tags = tags;
        Studios = studios;
        People = people;
        Weight = weight;
    }

    /// <summary>Gets the movie id.</summary>
    public Guid Id { get; }

    /// <summary>Gets the genre names.</summary>
    public IReadOnlyList<string> Genres { get; }

    /// <summary>Gets the tag names.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>Gets the studio names.</summary>
    public IReadOnlyList<string> Studios { get; }

    /// <summary>Gets the person ids that count towards the profile.</summary>
    public IReadOnlyList<Guid> People { get; }

    /// <summary>Gets the engagement weight.</summary>
    public double Weight { get; }
}
