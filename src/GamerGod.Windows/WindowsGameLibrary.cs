using System.Collections.Immutable;
using System.Runtime.Versioning;
using GamerGod.Core.Library;
using GamerGod.Core.Workloads;
using Microsoft.Win32;

namespace GamerGod.Windows;

/// <summary>
/// Finds the games and emulators actually installed on this machine.
///
/// <para>
/// Read-only, and it reads only what the stores already publish about themselves: Steam's
/// manifests, Epic's manifests, GOG's registry keys. Nothing is scraped from a website, no
/// account is touched, and nothing is sent anywhere.
/// </para>
///
/// <para>
/// Launching goes through each store's own protocol handler rather than the game's executable.
/// That is not a shortcut — it is the only way a title gets the environment, cloud saves and
/// anti-cheat bootstrap it expects, and launching the exe directly is the classic way a
/// third-party launcher breaks a game.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameLibrary : IGameLibrary
{
    public ValueTask<ImmutableArray<GameEntry>> ScanAsync(CancellationToken cancellationToken)
    {
        var entries = ImmutableArray.CreateBuilder<GameEntry>();

        entries.AddRange(Probe(ScanSteam));
        entries.AddRange(Probe(ScanEpic));
        entries.AddRange(Probe(ScanGog));
        entries.AddRange(Probe(ScanEmulators));

        return ValueTask.FromResult<ImmutableArray<GameEntry>>(
        [
            .. entries
                .GroupBy(e => (e.Source, e.Name), TupleComparer.Instance)
                .Select(g => g.First())
                .OrderBy(e => e.Source == GameSource.Emulator)  // games first, emulators after
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        ]);
    }

    /// <summary>One unreadable store must not cost the user their whole library.</summary>
    private static ImmutableArray<GameEntry> Probe(Func<ImmutableArray<GameEntry>> scan)
    {
        try
        {
            return scan();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static ImmutableArray<GameEntry> ScanSteam()
    {
        var steamPath = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam")
            ?.GetValue("SteamPath") as string;

        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return [];
        }

        // Steam writes this value with forward slashes.
        steamPath = steamPath.Replace('/', '\\');

        var libraries = new List<string> { steamPath };

        var foldersFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(foldersFile))
        {
            // A user with games on a second drive has them listed here, and missing them would
            // make the library look half-empty for no visible reason.
            libraries.AddRange(StoreManifests.ParseSteamLibraryFolders(File.ReadAllText(foldersFile)));
        }

        var entries = ImmutableArray.CreateBuilder<GameEntry>();

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps))
            {
                continue;
            }

            foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                var app = StoreManifests.ParseSteamManifest(SafeRead(manifest));
                if (app is null)
                {
                    continue;
                }

                var installPath = Path.Combine(steamapps, "common", app.InstallDir);

                entries.Add(new GameEntry
                {
                    Name = app.Name,
                    Source = GameSource.Steam,
                    Id = app.AppId,
                    LaunchTarget = $"steam://rungameid/{app.AppId}",
                    InstallPath = Directory.Exists(installPath) ? installPath : null,
                });
            }
        }

        return entries.ToImmutable();
    }

    private static ImmutableArray<GameEntry> ScanEpic()
    {
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifests))
        {
            return [];
        }

        var entries = ImmutableArray.CreateBuilder<GameEntry>();

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            var app = StoreManifests.ParseEpicManifest(SafeRead(file));
            if (app is null)
            {
                continue;
            }

            entries.Add(new GameEntry
            {
                Name = app.DisplayName,
                Source = GameSource.Epic,
                Id = app.AppName,
                LaunchTarget =
                    $"com.epicgames.launcher://apps/{app.AppName}?action=launch&silent=true",
                InstallPath = app.InstallLocation is not null && Directory.Exists(app.InstallLocation)
                    ? app.InstallLocation
                    : null,
            });
        }

        return entries.ToImmutable();
    }

    private static ImmutableArray<GameEntry> ScanGog()
    {
        var entries = ImmutableArray.CreateBuilder<GameEntry>();

        foreach (var root in new[]
                 {
                     @"SOFTWARE\WOW6432Node\GOG.com\Games",
                     @"SOFTWARE\GOG.com\Games",
                 })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null)
            {
                continue;
            }

            foreach (var id in key.GetSubKeyNames())
            {
                using var game = key.OpenSubKey(id);

                if (game?.GetValue("gameName") is not string name || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var path = game.GetValue("path") as string;
                var exe = game.GetValue("exe") as string;

                // GOG titles are DRM-free and often have no launcher, so the executable is the
                // correct target here - unlike Steam and Epic, there is no bootstrapper to miss.
                var target = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe)
                    ? exe
                    : path is not null ? path : string.Empty;

                if (target.Length == 0)
                {
                    continue;
                }

                entries.Add(new GameEntry
                {
                    Name = name.Trim(),
                    Source = GameSource.Gog,
                    Id = id,
                    LaunchTarget = target,
                    InstallPath = path is not null && Directory.Exists(path) ? path : null,
                });
            }
        }

        return entries.ToImmutable();
    }

    /// <summary>
    /// Emulators, matched against the catalogue GamerGod already carries.
    ///
    /// <para>
    /// Worth finding because emulators carry no anti-cheat and are dominated by a few
    /// cache-sensitive threads, which makes them the workload GamerGod helps most.
    /// </para>
    /// </summary>
    private static ImmutableArray<GameEntry> ScanEmulators()
    {
        var entries = ImmutableArray.CreateBuilder<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Emulators"),
        };

        // PATH too, since emulators are frequently unzipped rather than installed.
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            roots.AddRange(path.Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        foreach (var profile in EmulatorCatalog.All)
        {
            foreach (var processName in profile.ProcessNames)
            {
                var found = Locate(roots, $"{processName}.exe");

                if (found is null || !seen.Add(profile.Name))
                {
                    continue;
                }

                entries.Add(new GameEntry
                {
                    Name = profile.Name,
                    Source = GameSource.Emulator,
                    Id = profile.Name,
                    LaunchTarget = found,
                    InstallPath = Path.GetDirectoryName(found),
                    Systems = profile.Systems,
                });

                break;
            }
        }

        return entries.ToImmutable();
    }

    private static string? Locate(IEnumerable<string> roots, string fileName)
    {
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var direct = Path.Combine(root, fileName);
                if (File.Exists(direct))
                {
                    return direct;
                }

                // One level down only. A full recursive walk of Program Files takes seconds and
                // would make the library page feel broken on first open.
                foreach (var child in Directory.EnumerateDirectories(root))
                {
                    var candidate = Path.Combine(child, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable or malformed path. Keep looking.
            }
        }

        return null;
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            // A manifest being rewritten by the store mid-scan is normal.
            return string.Empty;
        }
    }

    private sealed class TupleComparer : IEqualityComparer<(GameSource Source, string Name)>
    {
        public static readonly TupleComparer Instance = new();

        public bool Equals((GameSource Source, string Name) a, (GameSource Source, string Name) b) =>
            a.Source == b.Source
            && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((GameSource Source, string Name) value) =>
            HashCode.Combine(value.Source, value.Name.ToUpperInvariant());
    }
}
