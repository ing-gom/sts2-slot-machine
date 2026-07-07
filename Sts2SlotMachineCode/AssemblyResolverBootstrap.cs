using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Sts2SlotMachine;

/// <summary>
/// STS2's ModManager loads only <c>{ModId}.dll</c> via <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/>
/// — the mod folder is NOT added to the runtime's probing paths, so any DLL the mod
/// depends on (e.g. <c>Sts2.ModKit.dll</c> bundled next to ours) is never found by
/// default and the JIT throws <c>FileNotFoundException</c> the moment Initialize()
/// references a ModKit type.
///
/// Workaround: register an <see cref="AssemblyLoadContext.Resolving"/> handler that
/// looks next to our own DLL for a sibling assembly of the missing name. The
/// <c>[ModuleInitializer]</c> attribute runs this code at assembly-load time —
/// before any of our methods JIT — so Initialize() is safe by the time it executes.
/// </summary>
internal static class AssemblyResolverBootstrap
{
    private static bool _registered;

    [ModuleInitializer]
    internal static void Register()
    {
        if (_registered) return;
        _registered = true;

        var self = typeof(AssemblyResolverBootstrap).Assembly;
        var ctx = AssemblyLoadContext.GetLoadContext(self);
        if (ctx is null) return;

        var modDir = Path.GetDirectoryName(self.Location);
        if (string.IsNullOrEmpty(modDir)) return;

        ctx.Resolving += (loadContext, name) =>
        {
            if (string.IsNullOrEmpty(name.Name)) return null;
            var candidate = Path.Combine(modDir, name.Name + ".dll");
            return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
        };
    }
}
