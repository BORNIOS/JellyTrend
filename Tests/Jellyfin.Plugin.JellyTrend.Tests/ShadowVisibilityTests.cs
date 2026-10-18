using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyTrend.Services.Channel;
using Jellyfin.Plugin.JellyTrend.Services.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyTrend.Tests;

/// <summary>
/// Jellyfin persists channel items as regular library entities. These tests pin the cleanup that
/// keeps a JellyTrend shadow out of search, person pages and genre/studio/tag listings while the
/// channel itself keeps serving it.
/// </summary>
public sealed class ShadowVisibilityTests
{
    [Fact]
    public async Task HideShadowRemovesEveryLibraryAssociation()
    {
        var channelId = Guid.NewGuid();
        var shadow = Shadow(channelId);
        var library = FakeProxy.Create<ILibraryManager, ShadowLibraryFake>(fake =>
        {
            fake.Items = [shadow];
            fake.Parent = new Folder { Id = channelId, Name = "Canal" };
            fake.People[shadow.Id] = [new PersonInfo { Id = Guid.NewGuid(), Name = "Actor", Type = PersonKind.Actor }];
        });

        var changed = await TrendingShadowMetadataSync.HideShadowAsync(library, shadow, CancellationToken.None);

        Assert.True(changed);
        Assert.True(shadow.IsVirtualItem);
        Assert.Empty(shadow.Genres);
        Assert.Empty(shadow.Studios);
        Assert.Empty(shadow.Tags);
        Assert.Empty(shadow.ProviderIds);
        Assert.Empty(library.GetPeople(shadow));
        Assert.True(((ShadowLibraryFake)(object)library).Saved);
    }

    [Fact]
    public async Task HideShadowIsIdempotent()
    {
        var channelId = Guid.NewGuid();
        var shadow = Shadow(channelId);
        var library = FakeProxy.Create<ILibraryManager, ShadowLibraryFake>(fake =>
        {
            fake.Items = [shadow];
            fake.Parent = new Folder { Id = channelId, Name = "Canal" };
        });

        Assert.True(await TrendingShadowMetadataSync.HideShadowAsync(library, shadow, CancellationToken.None));
        Assert.False(await TrendingShadowMetadataSync.HideShadowAsync(library, shadow, CancellationToken.None));
    }

    [Fact]
    public async Task NormalizationCoversEveryShadowOfBothChannels()
    {
        var first = Shadow(Guid.NewGuid());
        var second = Shadow(Guid.NewGuid());
        var library = FakeProxy.Create<ILibraryManager, ShadowLibraryFake>(fake =>
        {
            fake.Items = [first, second];
            fake.Parent = new Folder { Id = first.ParentId, Name = "Canal" };
            fake.People[first.Id] = [new PersonInfo { Id = Guid.NewGuid(), Name = "Actor", Type = PersonKind.Actor }];
            fake.People[second.Id] = [new PersonInfo { Id = Guid.NewGuid(), Name = "Actriz", Type = PersonKind.Actor }];
        });

        await TrendingShadowMetadataSync.NormalizeShadowVisibilityAsync(library, NullLogger.Instance, CancellationToken.None);

        Assert.All(new[] { first, second }, shadow =>
        {
            Assert.True(shadow.IsVirtualItem);
            Assert.Empty(shadow.Genres);
            Assert.Empty(shadow.ProviderIds);
            Assert.Empty(library.GetPeople(shadow));
        });
    }

    [Fact]
    public void NewChannelItemDoesNotCarryLibraryAssociations()
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "LEGO",
            Genres = ["Animación"],
            Studios = ["Estudio"],
            Tags = ["lego"],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "1" }
        };
        var library = FakeProxy.Create<ILibraryManager, ShadowLibraryFake>(_ => { });
        var appHost = FakeProxy.Create<IServerApplicationHost, AppHostFake>(_ => { });

        var info = ChannelItemFactory.BuildMovieItem(library, appHost, movie, null, null);

        Assert.Empty(info.Genres);
        Assert.Empty(info.Studios);
        Assert.Empty(info.Tags);
        Assert.Empty(info.ProviderIds);
        Assert.Empty(info.People);
    }

    private static Movie Shadow(Guid channelId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "LEGO",
        ParentId = channelId,
        Genres = ["Animación"],
        Studios = ["Estudio"],
        Tags = ["lego"],
        ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [MetadataProvider.Tmdb.ToString()] = "1" },
        IsVirtualItem = false
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

    /// <summary>Library fake that only implements what the shadow cleanup uses.</summary>
    public class ShadowLibraryFake : DispatchProxy
    {
        public IReadOnlyList<BaseItem> Items { get; set; } = [];

        public BaseItem? Parent { get; set; }

        public Dictionary<Guid, IReadOnlyList<PersonInfo>> People { get; } = [];

        public bool Saved { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ILibraryManager.GetItemList):
                    return Items;

                case nameof(ILibraryManager.GetItemById):
                    return args is { Length: 1 } && args[0] is Guid id && Parent?.Id == id ? Parent : null;

                case nameof(ILibraryManager.GetNewItemId):
                    return Guid.NewGuid();

                case nameof(ILibraryManager.GetPeople):
                    return args is { Length: 1 } && args[0] is BaseItem item
                        ? People.TryGetValue(item.Id, out var people) ? people : []
                        : [];

                case nameof(ILibraryManager.UpdatePeopleAsync):
                    if (args is { Length: >= 2 } && args[0] is BaseItem target)
                    {
                        People[target.Id] = (IReadOnlyList<PersonInfo>)args[1]!;
                    }

                    return Task.CompletedTask;

                case nameof(ILibraryManager.UpdateItemAsync):
                    Saved = true;
                    return Task.CompletedTask;

                default:
                    return Default(targetMethod?.ReturnType);
            }
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

    /// <summary>Application host fake; the movie path never needs its members.</summary>
    public class AppHostFake : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;
            if (returnType is null || returnType == typeof(void))
            {
                return null;
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
