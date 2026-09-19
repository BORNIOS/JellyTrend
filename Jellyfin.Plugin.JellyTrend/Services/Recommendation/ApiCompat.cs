using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Puente de compatibilidad que mantiene un único motor para la línea 10.11 y la 12.1.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin 12 añadió dos lecturas en lote que el motor usa de serie
/// (<c>ILibraryManager.GetPeopleByItems</c> y <c>IUserDataManager.GetUserDataBatch</c>). En 10.11 no
/// existen —su <c>InternalPeopleQuery</c> solo acepta un <c>ItemId</c>— así que aquí se resuelven
/// elemento a elemento, con el mismo coste que la versión 2.0.4.
/// </para>
/// <para>
/// El algoritmo es idéntico en ambas líneas: lo único que cambia es el número de consultas. Por eso
/// la mejora de rendimiento por lote solo se puede medir en la línea 12.1; en 10.11 este puente sirve
/// para validar resultados con el historial real sin tocar Jellyfin.
/// </para>
/// </remarks>
internal static class ApiCompat
{
    /// <summary>
    /// Reads the user data of several items at once.
    /// </summary>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="items">Items to read.</param>
    /// <param name="user">Target user.</param>
    /// <returns>User data keyed by item id; items without data are omitted.</returns>
    public static Dictionary<Guid, UserItemData> GetUserData(
        IUserDataManager userDataManager,
        IReadOnlyList<BaseItem> items,
        User user)
    {
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(items);

#if NET10_0_OR_GREATER
        return userDataManager.GetUserDataBatch(items, user);
#else
        var userData = new Dictionary<Guid, UserItemData>(items.Count);
        foreach (var item in items)
        {
            var data = userDataManager.GetUserData(user, item);
            if (data is not null)
            {
                userData[item.Id] = data;
            }
        }

        return userData;
#endif
    }

    /// <summary>
    /// Reads the people of several items at once.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="items">Items to read.</param>
    /// <returns>People keyed by item id; items without people are omitted.</returns>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<PersonInfo>> GetPeopleByItems(
        ILibraryManager libraryManager,
        IReadOnlyList<BaseItem> items)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(items);

#if NET10_0_OR_GREATER
        return libraryManager.GetPeopleByItems(items.Select(static item => item.Id).ToList());
#else
        var people = new Dictionary<Guid, IReadOnlyList<PersonInfo>>(items.Count);
        foreach (var item in items)
        {
            var itemPeople = libraryManager.GetPeople(item);
            if (itemPeople.Count > 0)
            {
                people[item.Id] = itemPeople;
            }
        }

        return people;
#endif
    }
}
