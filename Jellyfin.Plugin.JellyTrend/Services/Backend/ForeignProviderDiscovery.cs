using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Backend;

/// <summary>
/// Looks for another loaded plugin that offers one of our contracts.
/// </summary>
/// <remarks>
/// <para>
/// Ningun plugin de Jellyfin se presenta ante otro: no comparten ensamblados ni hay un registro comun, y
/// resolver un tipo por nombre de ensamblado desde el plugin ajeno no es fiable. Por eso busca el que
/// necesita el dato: aqui se revisan los ensamblados ya cargados (de todos los contextos de carga) en busca
/// de una copia del contrato pedido, y su implementacion se envuelve para poder usarla como si fuera nuestra.
/// </para>
/// <para>
/// La copia se reconoce por el nombre completo del contrato, no por el nombre del plugin: cualquier proveedor
/// que replique el contrato aparece aqui sin tocar este codigo.
/// </para>
/// </remarks>
internal static class ForeignProviderDiscovery
{
    /// <summary>
    /// Finds an implementation of <typeparamref name="TContract"/> offered by another plugin.
    /// </summary>
    /// <typeparam name="TContract">Our contract to look for.</typeparam>
    /// <param name="loggerFactory">Logger factory handed to the foreign implementation.</param>
    /// <param name="detail">Describes what was found or why nothing was.</param>
    /// <returns>The adapted provider, or null when no other plugin offers it.</returns>
    public static TContract? Find<TContract>(ILoggerFactory loggerFactory, out string detail)
        where TContract : class
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var contractName = typeof(TContract).FullName!;
        var ourAssembly = typeof(TContract).Assembly;
        string? lastProblem = null;

        foreach (var assembly in LoadedAssemblies())
        {
            if (assembly == ourAssembly)
            {
                continue;
            }

            var mirror = SafeGetType(assembly, contractName);
            if (mirror is null || !mirror.IsInterface)
            {
                continue;
            }

            var implementation = FirstImplementation(assembly, mirror);
            if (implementation is null)
            {
                continue;
            }

            if (!TryCreate(implementation, loggerFactory, out var instance, out var creationError))
            {
                lastProblem = $"{assembly.GetName().Name}: {creationError}";
                continue;
            }

            detail = $"encontrado por busqueda en {assembly.GetName().Name} {assembly.GetName().Version}";
            return Adapt<TContract>(mirror, instance);
        }

        detail = lastProblem is null
            ? "ningun plugin cargado ofrece este contrato"
            : $"encontrado pero no se pudo usar ({lastProblem})";

        return null;
    }

    private static TContract Adapt<TContract>(Type mirror, object instance)
        where TContract : class
    {
        var proxy = DispatchProxy.Create<TContract, ForeignForwardingProxy>();
        var forwarder = (ForeignForwardingProxy)(object)proxy;
        forwarder.Target = instance;
        forwarder.Mirror = mirror;
        return proxy;
    }

    private static IEnumerable<Assembly> LoadedAssemblies()
        => AssemblyLoadContext.All
            .SelectMany(static context => context.Assemblies)
            .Distinct();

    private static Type? SafeGetType(Assembly assembly, string name)
    {
        try
        {
            return assembly.GetType(name, throwOnError: false, ignoreCase: false);
        }
        catch (Exception)
        {
            // Un ensamblado ajeno puede fallar al resolver dependencias; no es asunto nuestro.
            return null;
        }
    }

    private static Type? FirstImplementation(Assembly assembly, Type mirror)
    {
        IEnumerable<Type> types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(static type => type is not null)!;
        }
        catch (Exception)
        {
            return null;
        }

        return types.FirstOrDefault(type => type is { IsAbstract: false, IsInterface: false }
            && mirror.IsAssignableFrom(type));
    }

    private static bool TryCreate(Type implementation, ILoggerFactory loggerFactory, out object instance, out string error)
    {
        instance = null!;
        error = string.Empty;

        var constructors = implementation.GetConstructors();
        var parameterless = constructors.FirstOrDefault(static ctor => ctor.GetParameters().Length == 0);
        if (parameterless is not null)
        {
            instance = parameterless.Invoke(null);
            return true;
        }

        // Patron habitual de Jellyfin: un unico parametro ILogger<T>.
        var withLogger = constructors.FirstOrDefault(static ctor =>
            ctor.GetParameters() is [{ ParameterType: var parameter }]
            && parameter.IsGenericType
            && parameter.GetGenericTypeDefinition() == typeof(ILogger<>));

        if (withLogger is null)
        {
            error = "no hay un constructor que podamos satisfacer";
            return false;
        }

        try
        {
            var logger = Activator.CreateInstance(
                typeof(Logger<>).MakeGenericType(implementation),
                loggerFactory);

            instance = withLogger.Invoke([logger]);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name} al construir";
            return false;
        }
    }
}
