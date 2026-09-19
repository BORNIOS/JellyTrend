using System;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Turns the raw engagement signals of a watched movie into a single weight, so a title the user
/// rewatched last week counts far more than one abandoned years ago. Every signal is optional: a
/// movie with no user data still contributes a neutral weight instead of being dropped.
/// </summary>
internal static class EngagementModel
{
    /// <summary>Days in which the influence of a play is halved.</summary>
    internal const double RecencyHalfLifeDays = 180d;

    /// <summary>Weight floor for very old plays, so long-lived taste never disappears.</summary>
    internal const double MinimumRecency = 0.15d;

    /// <summary>Weight applied when a title is marked as favorite.</summary>
    internal const double FavoriteBoost = 1.5d;

    /// <summary>
    /// Computes the engagement weight of a watched movie.
    /// </summary>
    /// <param name="played">Whether the movie was played to the end at least once.</param>
    /// <param name="progress">Fraction of the movie watched (0-1), used for in-progress items.</param>
    /// <param name="playCount">Number of plays registered by Jellyfin.</param>
    /// <param name="isFavorite">Whether the user marked it as favorite.</param>
    /// <param name="lastPlayed">Last play date in UTC, or <see langword="null"/> when unknown.</param>
    /// <param name="nowUtc">Current UTC time.</param>
    /// <returns>A positive weight; 1.0 is a plain, recently finished, one-time play.</returns>
    public static double Weight(
        bool played,
        double progress,
        int playCount,
        bool isFavorite,
        DateTime? lastPlayed,
        DateTime nowUtc)
    {
        var completion = played
            ? 1d
            : 0.35d + (0.65d * Math.Clamp(progress, 0d, 1d));

        // Rewatch is a strong explicit signal: log growth keeps a 3rd viewing from swamping the profile.
        var rewatch = 1d + Math.Log(1d + Math.Max(0, playCount - 1));

        var recency = MinimumRecency;
        if (lastPlayed is { } last)
        {
            var ageDays = Math.Max(0d, (nowUtc - last.ToUniversalTime()).TotalDays);
            recency = Math.Max(MinimumRecency, Math.Pow(0.5d, ageDays / RecencyHalfLifeDays));
        }
        else
        {
            // Unknown date: neutral value between "just watched" and "ancient".
            recency = 0.5d;
        }

        var feedback = isFavorite ? FavoriteBoost : 1d;
        return completion * rewatch * recency * feedback;
    }
}
