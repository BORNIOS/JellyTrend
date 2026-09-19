using System;

using Jellyfin.Plugin.JellyTrend.Services.Recommendation;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// El alcance de bibliotecas no debe incluir los canales.
/// </summary>
/// <remarks>
/// Los canales del propio plugin (Recomendados, Tendencias) cuelgan del usuario como si fueran vistas y
/// derivan de <c>Folder</c>, así que una recursión ingenua los toma por bibliotecas: infla el alcance y,
/// peor, mete en la consulta los ítems sombra, que no están marcados como virtuales y podrían aparecer en
/// la fila de recomendaciones.
/// </remarks>
public class LibraryScopeTests
{
    [Fact]
    public void UnaBibliotecaSiAportaAlcance()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid(), Name = "Películas" };

        Assert.True(LibraryScope.IsLibrary(library));
    }

    [Fact]
    public void UnCanalNoAportaAlcance()
    {
        var channel = new Channel { Id = Guid.NewGuid(), Name = "JellyTrend - Trending Now" };

        Assert.False(LibraryScope.IsLibrary(channel));
    }

    [Fact]
    public void UnItemDentroDeUnCanalNoAportaAlcance()
    {
        var shadow = new Movie { Id = Guid.NewGuid(), Name = "Sombra del canal" };
        shadow.ChannelId = Guid.NewGuid();

        Assert.False(LibraryScope.IsLibrary(shadow));
    }

    [Fact]
    public void UnaVistaNormalSiAportaAlcance()
    {
        // Las vistas agrupadas no son canales: hay que seguir entrando en ellas para encontrar las bibliotecas.
        var view = new UserView { Id = Guid.NewGuid(), Name = "Agrupada" };

        Assert.True(LibraryScope.IsLibrary(view));
    }
}
