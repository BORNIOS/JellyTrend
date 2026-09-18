using System;
using System.Collections.Generic;
using System.Linq;

using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyTrend.Services.Recommendation;

/// <summary>
/// Resolves the library ids a user can see, in the form the item query expects.
/// </summary>
/// <remarks>
/// <para>
/// Two mistakes are easy to make here, and both leave the row empty. First, the ids returned by
/// <c>GetUserRootFolder().GetChildren(user, true)</c> are <b>views</b>, not libraries: grouped
/// libraries get a synthetic view and user-specific ones are named views. Second, even the library
/// id is not what items store as <c>TopParentId</c>: a movie inside a library points to the
/// <b>physical folder</b> of that library.
/// </para>
/// <para>
/// Jellyfin's own translation (<c>LibraryManager.GetTopParentIdsForQuery</c>) does exactly this:
/// for a <c>CollectionFolder</c> it returns <c>PhysicalFolderIds</c>, and for a view it walks down to
/// the underlying libraries. This mirrors that logic using public members, and keeps the library id
/// as well because a whitelist with extra ids is harmless while a missing one empties the row.
/// </para>
/// </remarks>
internal static class LibraryScope
{
    /// <summary>
    /// Gets the ids the item query needs to scope results to what the user can see.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="user">Target user.</param>
    /// <returns>Physical folder and library ids, empty when the user has no visible library.</returns>
    public static List<Guid> Resolve(ILibraryManager libraryManager, User user)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(user);

        var libraryIds = new List<Guid>();
        var views = libraryManager.GetUserRootFolder().GetChildren(user, true);

        foreach (var view in views)
        {
            AddLibraryIds(view, user, libraryIds);
        }

        return libraryIds.Distinct().ToList();
    }

    private static void AddLibraryIds(BaseItem item, User user, List<Guid> libraryIds)
    {
        if (item is CollectionFolder collectionFolder)
        {
            libraryIds.Add(collectionFolder.Id);
            libraryIds.AddRange(collectionFolder.PhysicalFolderIds);
            return;
        }

        if (item is ICollectionFolder)
        {
            libraryIds.Add(item.Id);
            return;
        }

        // Vista agrupada o nombrada: las bibliotecas reales cuelgan de ella.
        if (item is not Folder folder)
        {
            return;
        }

        foreach (var child in folder.GetChildren(user, false))
        {
            AddLibraryIds(child, user, libraryIds);
        }
    }
}
