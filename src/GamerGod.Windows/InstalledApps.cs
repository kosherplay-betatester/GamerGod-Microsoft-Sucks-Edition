using System.Collections.Immutable;
using System.Runtime.Versioning;
using GamerGod.Core.Catalogue;
using Microsoft.Win32;

namespace GamerGod.Windows;

/// <summary>
/// One installed program, as Windows itself records it.
/// </summary>
public sealed record InstalledApp
{
    public required string Name { get; init; }

    /// <summary>Where its icon lives — often an exe, sometimes an .ico.</summary>
    public string? IconPath { get; init; }

    /// <summary>The executable to start, or null when none could be identified.</summary>
    public string? LaunchTarget { get; init; }

    public string? InstallLocation { get; init; }
}

/// <summary>
/// Finds programs already on this machine by reading the uninstall registry — the same list
/// Windows shows in Installed apps.
///
/// <para>
/// Read-only, and it has to be: this class exists to light up launch buttons and icons, and
/// nothing here has any business writing. It is also the only way to get an executable path,
/// which winget does not report — winget knows a package is installed, not where it put it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstalledApps
{
    private static readonly (RegistryHive Hive, RegistryView View)[] Roots =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Everything with a display name and a resolvable location.
    ///
    /// <para>
    /// Both 32- and 64-bit views plus the per-user hive, because launchers scatter across all
    /// three — Steam registers per-machine 32-bit, Epic per-machine 64-bit, and several
    /// emulators install per-user.
    /// </para>
    /// </summary>
    public static ImmutableArray<InstalledApp> Scan()
    {
        var found = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in Roots)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);

                if (uninstall is null)
                {
                    continue;
                }

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    if (Read(uninstall, name) is { } app && !found.ContainsKey(app.Name))
                    {
                        found[app.Name] = app;
                    }
                }
            }
            catch (Exception)
            {
                // A hive that cannot be opened is not worth failing the whole scan over. The
                // interface degrades to generated icons and no launch button, which is the
                // same state as "not installed" and equally harmless.
            }
        }

        return [.. found.Values];
    }

    private static InstalledApp? Read(RegistryKey uninstall, string subKeyName)
    {
        try
        {
            using var key = uninstall.OpenSubKey(subKeyName);

            if (key?.GetValue("DisplayName") as string is not { Length: > 0 } name)
            {
                return null;
            }

            // Windows hides updates and components from Installed apps with these, and so
            // should this — otherwise every security update shows up as a program.
            if (key.GetValue("SystemComponent") is int and not 0 || key.GetValue("ParentKeyName") is not null)
            {
                return null;
            }

            var icon = Clean(key.GetValue("DisplayIcon") as string);
            var remover = Clean(key.GetValue("UninstallString") as string);
            var location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');

            // Steam, MSI Afterburner and RivaTuner all record an empty InstallLocation, so the
            // folder has to be recovered from whichever path they did write — including the
            // uninstaller's, which is at least in the right directory.
            if (string.IsNullOrWhiteSpace(location))
            {
                location = DirectoryOf(icon) ?? DirectoryOf(remover);
            }

            return new InstalledApp
            {
                Name = name.Trim(),
                IconPath = icon,
                InstallLocation = string.IsNullOrWhiteSpace(location) ? null : location,
                LaunchTarget = ResolveLaunchTarget(name.Trim(), icon, location),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// DisplayIcon carries an icon index more often than not — <c>"C:\...\steam.exe",0</c> —
    /// and the quoting is inconsistent between publishers.
    /// </summary>
    private static string? Clean(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim();

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : value.Trim('"');
        }

        // Unquoted, so the trailing ",0" has to be split off — but only if what precedes it
        // still looks like a path, since a comma can legitimately appear in a folder name.
        var comma = value.LastIndexOf(',');

        if (comma > 2 && int.TryParse(value[(comma + 1)..].Trim(), out _))
        {
            return value[..comma].Trim();
        }

        return value;
    }

    private static string? DirectoryOf(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The executable worth starting.
    ///
    /// <para>
    /// <b>DisplayIcon is not it</b>, however much it looks like it. Steam records
    /// <c>uninstall.exe</c> there, as do MSI Afterburner and RivaTuner Statistics Server —
    /// three of the most common programs on a gaming PC. Trusting that value puts a Launch
    /// button on an uninstaller, so it is only ever used here after it has survived the same
    /// filter as everything else.
    /// </para>
    ///
    /// <para>
    /// App Paths is consulted first because it is the one place Windows records a launch target
    /// as a launch target rather than as an icon source.
    /// </para>
    /// </summary>
    private static string? ResolveLaunchTarget(string displayName, string? icon, string? location)
    {
        if (AppPathsLookup(displayName) is { } registered)
        {
            return registered;
        }

        if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location))
        {
            // Nothing to search. The icon path is only acceptable when it is plainly not an
            // uninstaller.
            return icon is not null
                   && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                   && !ExecutablePicker.IsUninstaller(icon)
                   && File.Exists(icon)
                ? icon
                : null;
        }

        try
        {
            var executables = Directory
                .EnumerateFiles(location, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .Select(f => f!)
                .ToImmutableArray();

            var chosen = ExecutablePicker.Choose(
                displayName, new DirectoryInfo(location).Name, executables);

            return chosen is null ? null : Path.Combine(location, chosen);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The App Paths key, which is where a program registers the command that starts it.
    ///
    /// <para>
    /// Keyed by executable name, so the display name has to be guessed into one — but a hit
    /// here is authoritative in a way nothing else in the uninstall key is.
    /// </para>
    /// </summary>
    private static string? AppPathsLookup(string displayName)
    {
        var candidate = new string([.. displayName.Where(char.IsLetterOrDigit)]) + ".exe";

        if (candidate.Length <= 4)
        {
            return null;
        }

        foreach (var (hive, view) in Roots)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + candidate);

                if (key?.GetValue(null) as string is not { Length: > 0 } path)
                {
                    continue;
                }

                var cleaned = path.Trim().Trim('"');

                if (File.Exists(cleaned) && !ExecutablePicker.IsUninstaller(cleaned))
                {
                    return cleaned;
                }
            }
            catch (Exception)
            {
                // Next hive.
            }
        }

        return null;
    }
}
