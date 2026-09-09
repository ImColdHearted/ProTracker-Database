using System;
using System.Reflection;

namespace Foot_Tracker.Services;

/// <summary>
/// §231. The version this build reports, read once.
///
/// It was worth a file of its own because until §231 there was no version to
/// read: the csproj never set one, so every build since the project began
/// reported the SDK's 1.0.0.0 default. Bug reports carried it (§226), the
/// presence heartbeat now carries it, and both were about to carry the same
/// meaningless constant forever.
///
/// InformationalVersion first, because it is the one that says exactly what
/// the csproj's Version says - "1.0.1" rather than the four-part "1.0.1.0"
/// that AssemblyName.Version pads it into. Some build setups append
/// "+commithash" to it, which is useful to a developer and noise in a
/// grouped count, so anything from the plus onwards is dropped.
/// </summary>
internal static class AppVersion
{
    private static readonly Lazy<string> current = new(Read);

    /// <summary>Never empty and never throws - "unknown" when an assembly
    /// somehow carries no version at all, which groups exactly as usefully
    /// in a breakdown as a real one would.</summary>
    internal static string Current => current.Value;

    private static string Read()
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();

            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+');
                string trimmed = (plus >= 0 ? informational[..plus] : informational).Trim();

                if (trimmed.Length > 0)
                    return trimmed;
            }

            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
