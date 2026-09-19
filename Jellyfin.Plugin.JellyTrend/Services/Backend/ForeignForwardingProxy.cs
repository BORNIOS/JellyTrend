using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Jellyfin.Plugin.JellyTrend.Api;

namespace Jellyfin.Plugin.JellyTrend.Services.Backend;

/// <summary>
/// Forwards our contract calls to the implementation offered by another plugin.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin plugins do not share types: the PostgreSQL plugin compiles its own copy of
/// <see cref="IRecommendationQueryProvider"/> (same full name, different assembly), so its class cannot be
/// registered against our type and its instance is not castable to ours. This proxy implements the contract
/// on our side and translates each call by name, so type identity stops mattering.
/// </para>
/// <para>
/// Its members are reached through reflection, so the foreign plugin only has to keep the same method names
/// and the same shapes — which the mirrored contract guarantees.
/// </para>
/// </remarks>
// No puede ser sealed: DispatchProxy genera una subclase en tiempo de ejecucion.
internal class ForeignForwardingProxy : DispatchProxy
{
    private static readonly ConcurrentDictionary<Type, ForeignItemProperties> Readers = new();

    /// <summary>Gets or sets the foreign object that actually answers the queries.</summary>
    public object? Target { get; set; }

    /// <summary>Gets or sets the foreign (mirrored) contract implemented by <see cref="Target"/>.</summary>
    public Type? Mirror { get; set; }

    /// <summary>Gets the full name of the foreign type that answers the calls, for the log.</summary>
    public string ForeignName => Target?.GetType().FullName ?? "(proveedor externo sin tipo)";

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null || Target is null || Mirror is null)
        {
            return null;
        }

        if (targetMethod.DeclaringType == typeof(object))
        {
            return InvokeObjectMember(targetMethod.Name, args);
        }

        var method = Mirror.GetMethod(
            targetMethod.Name,
            BindingFlags.Public | BindingFlags.Instance,
            null,
            targetMethod.GetParameters().Select(static parameter => parameter.ParameterType).ToArray(),
            null);

        if (method is null)
        {
            // El otro plugin no tiene ese metodo: mejor decirlo que devolver una lista vacia en silencio.
            throw new MissingMethodException(
                $"{Mirror.FullName} no declara {targetMethod.Name}; el proveedor externo no cumple el contrato.");
        }

        return Convert(targetMethod.ReturnType, method.Invoke(Target, args));
    }

    private object? InvokeObjectMember(string name, object?[]? args)
        => name switch
        {
            nameof(ToString) => $"{Target!.GetType().FullName} (proveedor externo de Jellyfin)",
            nameof(GetHashCode) => Target!.GetHashCode(),
            nameof(Equals) => ReferenceEquals(Target, args is { Length: > 0 } ? args[0] : null),
            _ => null
        };

    private static object? Convert(Type expectedType, object? result)
    {
        if (result is null || !expectedType.IsGenericType)
        {
            return result;
        }

        if (expectedType.GetGenericTypeDefinition() != typeof(IReadOnlyList<>)
            || expectedType.GetGenericArguments()[0] != typeof(RecommendationItem))
        {
            return result;
        }

        var items = new List<RecommendationItem>();
        var reader = Readers.GetOrAdd(GetItemType(result), static type => new ForeignItemProperties(type));

        foreach (var item in (IEnumerable)result)
        {
            if (item is not null)
            {
                items.Add(reader.Read(item));
            }
        }

        return items;
    }

    private static Type GetItemType(object result)
    {
        var enumerableInterface = result.GetType()
            .GetInterfaces()
            .FirstOrDefault(static type => type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerableInterface?.GetGenericArguments()[0] ?? result.GetType();
    }

    /// <summary>
    /// Reads the projection returned by the foreign plugin, whose properties have the same names as ours.
    /// </summary>
    private sealed class ForeignItemProperties
    {
        private readonly PropertyInfo _id;
        private readonly PropertyInfo _tmdbId;
        private readonly PropertyInfo _genres;
        private readonly PropertyInfo _tags;
        private readonly PropertyInfo _studios;
        private readonly PropertyInfo _communityRating;
        private readonly PropertyInfo _premiereDate;

        public ForeignItemProperties(Type type)
        {
            _id = Require(type, "Id");
            _tmdbId = Require(type, "TmdbId");
            _genres = Require(type, "Genres");
            _tags = Require(type, "Tags");
            _studios = Require(type, "Studios");
            _communityRating = Require(type, "CommunityRating");
            _premiereDate = Require(type, "PremiereDate");
        }

        public RecommendationItem Read(object item)
            => new(
                (Guid)_id.GetValue(item)!,
                _tmdbId.GetValue(item) as string,
                _genres.GetValue(item) as IReadOnlyList<string> ?? [],
                _tags.GetValue(item) as IReadOnlyList<string> ?? [],
                _studios.GetValue(item) as IReadOnlyList<string> ?? [],
                _communityRating.GetValue(item) as float?,
                _premiereDate.GetValue(item) as DateTime?);

        private static PropertyInfo Require(Type type, string name)
            => type.GetProperty(name)
                ?? throw new MissingMemberException($"{type.FullName} no expone {name}; el proveedor externo no cumple el contrato.");
    }
}
