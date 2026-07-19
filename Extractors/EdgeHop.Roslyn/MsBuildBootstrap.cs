using Microsoft.Build.Locator;

namespace EdgeHop.Roslyn;

/// <summary>
/// Registers the MSBuild locator exactly once, before any MSBuild or
/// <c>MSBuildWorkspace</c> type is resolved by the JIT.
/// </summary>
/// <remarks>
/// <para>
/// <b>CRITICAL JIT-ORDERING RULE (README "MSBuild gotcha"):</b> this class must never
/// reference any <c>Microsoft.CodeAnalysis</c> or <c>MSBuildWorkspace</c> type. The CLR
/// resolves an assembly the first time a method referencing one of its types is
/// JIT-compiled — if MSBuild assemblies load before
/// <see cref="MSBuildLocator.RegisterDefaults"/> runs, the process fails at runtime with
/// "could not load file or assembly Microsoft.Build…". Callers (only
/// <c>Program.Main</c> in this app) invoke <see cref="EnsureRegistered"/> first and only
/// then call into code that touches workspace types.
/// </para>
/// </remarks>
public static class MsBuildBootstrap
{
    private static readonly object Gate = new();

    /// <summary>
    /// Registers the default MSBuild instance (<see cref="MSBuildLocator.RegisterDefaults"/>)
    /// if no instance is registered yet. Safe to call more than once and from multiple
    /// threads; only the first call registers.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No MSBuild / .NET SDK installation could be located. Surfaced to the developer by
    /// the caller — this tool never tries to install or configure an SDK itself.
    /// </exception>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }
}
