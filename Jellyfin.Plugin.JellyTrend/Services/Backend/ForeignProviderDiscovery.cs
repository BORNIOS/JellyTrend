using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTrend.Services.Backend;

/// <summary>
/// Looks for another loaded plugin that offers one of our contracts.
/// </summary>
/// <remarks>
/// <para>
/// Ningun plugin de Jellyfin se presenta ante otro: no comparten ensamblados ni hay un registro comun, y
/// resolver un tipo por nombre de ensamblado desde el plugin ajeno no es fiable. Por eso busca el que
/// necesita el dato: aqui se revisan primero los ensamblados de los plugins que Jellyfin tiene vivos (esa
/// es la copia que de verdad esta en uso, con sus estaticos ya inicializados) y despues el resto de
/// ensamblados cargados. La copia se reconoce por el nombre completo del contrato, no por el nombre del
/// plugin: cualquier proveedor que replique el contrato aparece aqui sin tocar este codigo.
/// </para>
/// <para>
/// Buscar en todos los ensamblados cargados no basta: puede haber una segunda copia del mismo archivo, sin
/// inicializar, y esa copia responde con sus propios estaticos vacios (por ejemplo sin la configuracion del
/// plugin). Ir primero a los plugins vivos evita ese caso.
/// </para>
/// </remarks>
internal static class ForeignProviderDiscovery
{
    /// <summary>
    /// Finds an implementation of <typeparamref name="TContract"/> offered by another plugin.
    /// </summary>
    /// <typeparam name="TContract">Our contract to look for.</typeparam>
    /// <param name="services">The server service provider, used to look at its plugins and to build the foreign type.</param>
    /// <param name="loggerFactory">Logger factory handed to the foreign implementation.</param>
    /// <param name="detail">Describes what was found or why nothing was.</param>
    /// <returns>The adapted provider, or null when no other plugin offers it.</returns>
    public static TContract? Find<TContract>(IServiceProvider services, ILoggerFactory loggerFactory, out string detail)
        where TContract : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var contractName = typeof(TContract).FullName!;
        var ourAssembly = typeof(TContract).Assembly;
        string? lastProblem = null;

        foreach (var (assembly, label) in CandidateAssemblies(services))
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

            if (!TryCreate(implementation, services, loggerFactory, out var instance, out var creationError))
            {
                lastProblem = $"{label}: {creationError}";
                continue;
            }

            detail = label;
            return Adapt<TContract>(mirror, instance);
        }

        detail = lastProblem is null ? "ningun plugin ofrece este contrato" : lastProblem;

        return null;
    }

    /// <summary>
    /// Los ensamblados donde buscar y como llamarlos en el log, empezando por los de los plugins vivos.
    /// </summary>
    private static List<(Assembly Assembly, string Label)> CandidateAssemblies(IServiceProvider services)
    {
        var candidates = LivePluginAssemblies(services);

        foreach (var assembly in LoadedAssemblies())
        {
            if (!candidates.Any(candidate => candidate.Assembly == assembly))
            {
                candidates.Add((assembly, $"{assembly.GetName().Name} {assembly.GetName().Version}"));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Ensamblados de los plugins que Jellyfin tiene cargados como plugins, no solo presentes en memoria.
    /// </summary>
    private static List<(Assembly Assembly, string Label)> LivePluginAssemblies(IServiceProvider services)
    {
        var found = new List<(Assembly Assembly, string Label)>();

        try
        {
            if (services.GetService<IPluginManager>() is { } pluginManager)
            {
                foreach (var plugin in pluginManager.Plugins)
                {
                    if (plugin.Instance is not { } instance)
                    {
                        continue;
                    }

                    var assembly = instance.GetType().Assembly;
                    found.Add((assembly, $"{plugin.Manifest.Name} {assembly.GetName().Version}"));
                }
            }
        }
        catch (Exception)
        {
            // Sin lista de plugins seguimos con el resto de ensamblados.
        }

        return found.Distinct().ToList();
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

    private static bool TryCreate(Type implementation, IServiceProvider services, ILoggerFactory loggerFactory, out object instance, out string error)
    {
        instance = null!;
        error = string.Empty;

        // Lo normal en Jellyfin: el contenedor resuelve las dependencias del constructor.
        try
        {
            instance = ActivatorUtilities.CreateInstance(services, implementation);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name} al construir por contenedor";
        }

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
