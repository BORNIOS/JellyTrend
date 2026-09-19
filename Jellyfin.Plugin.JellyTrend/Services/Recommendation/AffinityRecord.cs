namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// One learned preference of a user: how much a facet value (or a pair of values) explains what they watch.
/// </summary>
/// <remarks>
/// The facet is kept as a <see cref="string"/> ("genre", "director", "tag"...) so the profile serializes
/// without ceremony and adding a facet later does not change the stored shape. When
/// <see cref="PairedValue"/> is set the record is a <b>combined</b> affinity: not "horror" or
/// "science fiction" alone, but "horror + science fiction", which is where taste actually lives.
/// </remarks>
public sealed class AffinityRecord
{
    /// <summary>Gets or sets the facet the preference belongs to ("genre", "actor", "tag"...).</summary>
    public string Facet { get; set; } = string.Empty;

    /// <summary>Gets or sets the value of the facet ("Horror", "David Fincher"...).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets the second facet of a combined affinity, when there is one.</summary>
    public string? PairedFacet { get; set; }

    /// <summary>Gets or sets the second value of a combined affinity, when there is one.</summary>
    public string? PairedValue { get; set; }

    /// <summary>Gets or sets the learned weight, normalized so the strongest preference of the profile is 1.</summary>
    public double Weight { get; set; }
}
