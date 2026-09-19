using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Outcome of a recommendation run: the selected movie ids plus a one-line explanation of how the
/// list was built (data source, profile weights and composition of the result) that the task writes
/// to the log, so a surprising row can always be explained instead of guessed.
/// </summary>
/// <param name="ItemIds">Selected movie ids, best first.</param>
/// <param name="Diagnostics">Human-readable summary of the run.</param>
internal sealed record RecommendationResult(IReadOnlyList<Guid> ItemIds, string Diagnostics);
