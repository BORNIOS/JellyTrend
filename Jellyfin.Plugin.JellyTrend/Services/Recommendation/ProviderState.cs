using System;

using Jellyfin.Plugin.JellyTrend.Api;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Life cycle of the optional database-provider backend inside one recommendation run.
/// </summary>
/// <remarks>
/// Whether the provider works is a property of the environment, not of the user: if the first user
/// shows that it answers nothing while the library has movies, there is no reason to ask it again for
/// the remaining users. Sharing this state across the run avoids a wasted round of queries and one
/// warning line per user, and keeps the log honest about what was used.
/// </remarks>
internal sealed class ProviderState
{
    private readonly Action? _onRejected;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderState"/> class.
    /// </summary>
    /// <param name="provider">Provider offered by another plugin, when it is installed.</param>
    /// <param name="onRejected">Notificado cuando el proveedor se descarta, para que el backend lo recuerde.</param>
    /// <param name="unavailableReason">Motivo por el que no se ofrece un proveedor que si existe.</param>
    public ProviderState(
        IRecommendationQueryProvider? provider = null,
        Action? onRejected = null,
        string? unavailableReason = null)
    {
        Provider = provider;
        _onRejected = onRejected;
        UnavailableReason = unavailableReason;
    }

    /// <summary>Gets the provider offered by another plugin, when it is installed.</summary>
    public IRecommendationQueryProvider? Provider { get; }

    /// <summary>
    /// Gets why there is no provider to use. Cuando hay uno instalado pero no se ofrece, aqui va el motivo
    /// medido (por ejemplo, que no devolvio candidatos), para que el diagnostico no diga solo "sin proveedor".
    /// </summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Gets a value indicating whether the provider was discarded during the run.</summary>
    public bool Rejected { get; private set; }

    /// <summary>Gets a value indicating whether the provider can still be used.</summary>
    public bool IsUsable => Provider is not null && !Rejected;

    /// <summary>
    /// Discards the provider for the rest of the run and tells the backend why, once.
    /// </summary>
    public void Reject()
    {
        if (Rejected)
        {
            return;
        }

        Rejected = true;
        UnavailableReason = "proveedor descartado durante la ejecucion";
        _onRejected?.Invoke();
    }
}
