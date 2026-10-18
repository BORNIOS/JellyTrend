using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Jellyfin.Plugin.JellyTrend.Services.Channel;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// The media sources of a channel item must leave the source id unassigned: Jellyfin fills it with
/// the channel item id in <c>ChannelManager.NormalizeMediaSources</c>. Clients such as Android TV
/// send the id of the item's media source back in PlaybackInfo, and the server filters the dynamic
/// sources by that id, so returning the library item id there leaves the request without a stream.
/// </summary>
public sealed class ChannelMediaInfoTests
{
    [Fact]
    public void LibrarySourcesAreReturnedWithoutTheirOwnId()
    {
        var itemId = Guid.NewGuid();
        var movie = Movie(itemId);
        var sources = new List<MediaSourceInfo>
        {
            new() { Id = itemId.ToString("N", CultureInfo.InvariantCulture), Path = movie.Path, Protocol = MediaProtocol.File },
            new() { Id = "alternate", Path = @"C:\media\alternate.mkv", Protocol = MediaProtocol.File }
        };
        var library = FakeProxy.Create<ILibraryManager, LibraryFake>(fake => fake.Item = movie);
        var mediaSources = FakeProxy.Create<IMediaSourceManager, MediaSourceFake>(fake => fake.Sources = sources);

        var result = ChannelItemFactory.GetMediaInfo(library, mediaSources, itemId.ToString()).ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, source => Assert.True(string.IsNullOrEmpty(source.Id)));
    }

    [Fact]
    public void FallbackSourceIsReturnedWithoutId()
    {
        var itemId = Guid.NewGuid();
        var movie = Movie(itemId);
        var library = FakeProxy.Create<ILibraryManager, LibraryFake>(fake => fake.Item = movie);
        var mediaSources = FakeProxy.Create<IMediaSourceManager, MediaSourceFake>(fake => fake.Sources = []);

        var result = ChannelItemFactory.GetMediaInfo(library, mediaSources, itemId.ToString()).ToList();

        var source = Assert.Single(result);
        Assert.True(string.IsNullOrEmpty(source.Id));
        Assert.Equal(movie.Path, source.Path);
    }

    private static Movie Movie(Guid id) => new()
    {
        Id = id,
        Name = "Pelicula",
        Path = @"C:\media\movie.mkv"
    };

    private static class FakeProxy
    {
        public static TInterface Create<TInterface, TProxy>(Action<TProxy> configure)
            where TInterface : class
            where TProxy : DispatchProxy
        {
            var created = DispatchProxy.Create<TInterface, TProxy>();
            configure((TProxy)(object)created);
            return created;
        }
    }

    /// <summary>Library manager that resolves one item by id.</summary>
    public class LibraryFake : DispatchProxy
    {
        public BaseItem? Item { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemById)
                && !targetMethod.IsGenericMethod
                && args is { Length: 1 }
                && args[0] is Guid id)
            {
                return Item is not null && Item.Id == id ? Item : null;
            }

            return Default(targetMethod?.ReturnType);
        }
    }

    /// <summary>Media source manager that serves a fixed source list and no streams.</summary>
    public class MediaSourceFake : DispatchProxy
    {
        public IReadOnlyList<MediaSourceInfo> Sources { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                nameof(IMediaSourceManager.GetStaticMediaSources) => Sources,
                nameof(IMediaSourceManager.GetMediaStreams) => Array.Empty<MediaStream>(),
                _ => Default(targetMethod?.ReturnType)
            };
    }

    private static object? Default(Type? returnType)
    {
        if (returnType is null || returnType == typeof(void))
        {
            return null;
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
