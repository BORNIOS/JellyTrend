namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Turns how a title was consumed into how much it should teach the profile.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <b>not</b> another percentage competing inside the taste weights. A reproduce count
/// does not describe the content: it describes how certain we are that the content interests this user. So
/// it multiplies the contribution of the item to the profile instead of adding a facet.
/// </para>
/// <para>
/// A title watched to the end once contributes 1.00. The same title watched three times and marked as a
/// favourite contributes 1.60 x 1.30 = 2.08, roughly twice as much. An abandoned title only counts as
/// rejection after real exposure: below a quarter watched the run is ignored, because it can be an accident
/// or a mis-click, and the penalty is intentionally much smaller than the positives.
/// </para>
/// </remarks>
public static class InteractionWeight
{
    /// <summary>Fraction of the title that must be watched before an unfinished run counts as rejection.</summary>
    public const double AbandonThreshold = 0.25;

    /// <summary>Extra weight of a title the user marked as a favourite.</summary>
    public const double FavoriteMultiplier = 1.30;

    /// <summary>Weight of a title that was started, not finished, and never resumed.</summary>
    public const double AbandonedWeight = -0.20;

    /// <summary>
    /// Computes how much one consumption of a title teaches the profile.
    /// </summary>
    /// <param name="progress">Fraction of the title reproduced (0-1).</param>
    /// <param name="playCount">Times the title was played, for rewatches.</param>
    /// <param name="favorite">Whether the user marked it as a favourite.</param>
    /// <param name="abandoned">Whether it was started, left unfinished and never resumed.</param>
    /// <returns>The weight: 0 (ignored), positive (interest) or a small negative (rejection).</returns>
    public static double Compute(double progress, int playCount, bool favorite, bool abandoned)
    {
        // Solo se asume rechazo con exposicion suficiente; por debajo, la reproduccion no cuenta.
        if (abandoned && progress >= AbandonThreshold)
        {
            return AbandonedWeight;
        }

        var reached = FromProgress(progress);
        if (reached == 0d)
        {
            return 0d;
        }

        var weight = reached * FromPlayCount(playCount);
        return favorite ? weight * FavoriteMultiplier : weight;
    }

    /// <summary>
    /// Weight contributed by how far the title was watched.
    /// </summary>
    /// <param name="progress">Fraction reproduced (0-1).</param>
    /// <returns>The base weight of the run.</returns>
    public static double FromProgress(double progress) => progress switch
    {
        < 0.05 => 0.00,
        < 0.20 => 0.10,
        < 0.50 => 0.35,
        < 0.80 => 0.65,
        < 0.95 => 0.90,
        _ => 1.00
    };

    /// <summary>
    /// Multiplier for repeated viewings, capped so a single obsession cannot dominate the profile.
    /// </summary>
    /// <param name="playCount">Times the title was played.</param>
    /// <returns>The rewatch multiplier.</returns>
    public static double FromPlayCount(int playCount) => playCount switch
    {
        <= 1 => 1.00,
        2 => 1.35,
        3 => 1.60,
        _ => 1.80
    };
}
