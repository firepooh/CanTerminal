using System.Reflection;

namespace CanTerminal.Core;

/// <summary>
/// What this build calls itself.
///
/// The version comes from <see cref="AssemblyInformationalVersionAttribute"/> rather than the
/// assembly version, because that is the only one that survives a prerelease label: a build of
/// tag v1.2.0-rc1 has assembly version 1.2.0.0 and informational version 1.2.0-rc1, and reporting
/// the former would make a release candidate indistinguishable from the release.
/// </summary>
public static class AppInfo
{
    /// <summary>e.g. "1.0.0", or "1.0.0-dev.42" for a build off a branch.</summary>
    public static string Version { get; } = Read();

    public const string Name = "CanTerminal";

    public const string RepositoryUrl = "https://github.com/firepooh/CanTerminal";

    private static string Read()
    {
        string informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        if (informational.Length == 0) return "dev";

        // The SDK appends "+<commit sha>" when the repository is known. Useful in a log, noise in
        // a title bar, so it is kept off the displayed string.
        int plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
