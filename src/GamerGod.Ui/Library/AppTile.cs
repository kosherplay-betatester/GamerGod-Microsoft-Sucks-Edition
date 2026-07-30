using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerGod.Core.Catalogue;
using GamerGod.Windows;

namespace GamerGod.Ui.Library;

/// <summary>
/// One installed launcher or emulator, as a tile.
///
/// <para>
/// The icon comes out of the program's own executable, which Windows already has on disk — no
/// vendor lookup, no network, and it is always the current artwork because it ships with the
/// binary. When there is no icon to read, the same generated mark the game grid uses stands in,
/// so a missing icon looks designed rather than absent.
/// </para>
/// </summary>
public sealed class AppTile
{
    private AppTile(CatalogueEntry entry, InstalledApp? installed)
    {
        Entry = entry;
        Installed = installed;
        Icon = LoadIcon(installed);
        Fallback = TileArt.Gradient(entry.Name);
        Initial = TileArt.Initial(entry.Name);
    }

    public CatalogueEntry Entry { get; }

    public InstalledApp? Installed { get; }

    public string Name => Entry.Name;

    public string KindLabel => Entry.Kind == CatalogueKind.Launcher ? "LAUNCHER" : "EMULATOR";

    public string Systems => Entry.Systems;

    public ImageSource? Icon { get; }

    public bool HasIcon => Icon is not null;

    public Brush Fallback { get; }

    public string Initial { get; }

    /// <summary>The executable to start, or null when none was identified.</summary>
    public string? LaunchTarget => Installed?.LaunchTarget;

    public bool CanLaunch => LaunchTarget is not null;

    public static AppTile For(CatalogueEntry entry, InstalledApp? installed) => new(entry, installed);

    private static ImageSource? LoadIcon(InstalledApp? installed)
    {
        if (installed is null)
        {
            return null;
        }

        // The icon path first, then the executable. They are usually the same file, but an
        // installer that points DisplayIcon at a stale path still leaves a launchable binary.
        foreach (var candidate in new[] { installed.IconPath, installed.LaunchTarget })
        {
            using var handle = IconExtractor.TryExtract(candidate);

            if (handle is null || handle.IsInvalid)
            {
                continue;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    handle.DangerousGetHandle(),
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // Frozen before the handle is released: the bitmap copies the pixels, and
                // freezing also lets it cross to the render thread without a lock.
                source.Freeze();

                return source;
            }
            catch (Exception)
            {
                // A malformed icon resource. Try the next candidate, then give up quietly.
            }
        }

        return null;
    }
}
