namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// A candidate movie with the scores computed for it. <see cref="Taste"/> and <see cref="Quality"/>
/// are kept apart from the combined <see cref="Score"/> so the ranking can be explained in the log
/// instead of being a black box.
/// </summary>
/// <param name="Movie">The scored movie.</param>
/// <param name="Score">Combined score used for ranking (0-1).</param>
/// <param name="Taste">How well the movie matches the taste profile (0-1).</param>
/// <param name="Quality">Normalized community rating (0-1).</param>
internal readonly record struct ScoredCandidate(CandidateItem Movie, double Score, double Taste, double Quality);
