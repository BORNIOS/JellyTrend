using System;
using System.Globalization;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// The bucket labels shared by layer 2 (which learns the profile) and layer 3 (which applies it).
/// </summary>
/// <remarks>
/// The two layers have to name the same bucket the same way: if the profile learned "8-10" and the
/// candidate is scored as "8 a 10", the affinity never matches and the profile looks thinner than it is.
/// The labels therefore live in one place.
/// </remarks>
internal static class FacetLabels
{
    /// <summary>
    /// Names the decade of a year ("1990s"), which is how the profile remembers when the user watches.
    /// </summary>
    /// <param name="year">Year of release, or null.</param>
    /// <returns>The decade label, or null when there is no usable year.</returns>
    public static string? Decade(int? year)
        => year is { } value && value > 1900
            ? string.Format(CultureInfo.InvariantCulture, "{0}s", value / 10 * 10)
            : null;

    /// <summary>
    /// Names the decade of a premiere date.
    /// </summary>
    /// <param name="premiereDate">Premiere date, or null.</param>
    /// <returns>The decade label, or null when there is no date.</returns>
    public static string? DecadeOf(DateTime? premiereDate)
        => premiereDate is { } date ? Decade(date.Year) : null;

    /// <summary>
    /// Names the rating range of a community rating.
    /// </summary>
    /// <param name="rating">Community rating (0-10), or null.</param>
    /// <returns>The range label, or null when there is no rating.</returns>
    public static string? RatingBucket(double? rating) => rating switch
    {
        null => null,
        >= 8d => "8-10",
        >= 6.5d => "6.5-8",
        >= 5d => "5-6.5",
        _ => "menos de 5"
    };

    /// <summary>
    /// Names the duration range of a runtime.
    /// </summary>
    /// <param name="minutes">Runtime in minutes, or null.</param>
    /// <returns>The range label, or null when there is no runtime.</returns>
    public static string? RuntimeBucket(int? minutes) => minutes switch
    {
        null => null,
        < 90 => "menos de 90 min",
        <= 120 => "90-120 min",
        _ => "mas de 120 min"
    };
}
