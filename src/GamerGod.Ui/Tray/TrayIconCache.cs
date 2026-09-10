using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using GamerGod.Core.Catalogue;
using GamerGod.Core.Library;

namespace GamerGod.Ui.Tray;

/// <summary>
/// Small bitmaps for the Library Quick Launch menu, built once and kept.
///
/// <para>
/// A menu of bare names is a menu you have to read. With the game's own artwork beside each one
/// it becomes a menu you recognise, which is the whole point of a launcher that lives next to
/// the clock.
/// </para>
///
/// <para>
/// Two sources, in order of how much they look like the game: the cover art already on disk —
/// whatever the store cached, or whatever an opt-in fetch downloaded — and failing that the
/// executable's own icon. Neither touches the network. A title with neither simply has no
/// picture, which is a menu entry that still works.
/// </para>
///
/// <para>
/// Cached because the menu is rebuilt every time it opens, and decoding a 600x900 JPEG per game
/// per open would make the right-click feel broken on a large library.
/// </para>
/// </summary>
internal sealed class TrayIconCache : IDisposable
{
    /// <summary>
    /// Menu icon size. Windows draws these at 16 logical pixels; anything larger is scaled down
    /// by the menu itself, which is worse than scaling it here with a good interpolator.
    /// </summary>
    private const int Size = 16;

    private readonly Dictionary<string, Image?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// The icon for a game, or null when this machine offers nothing to draw.
    ///
    /// <para>
    /// Never throws. A menu that will not open because one game has a corrupt thumbnail would
    /// be a worse bug than a missing picture.
    /// </para>
    /// </summary>
    public Image? For(GameEntry entry, string? artFile)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_disposed)
        {
            return null;
        }

        var key = entry.LaunchTarget;

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Image? built = null;

        try
        {
            built = FromArt(artFile) ?? FromExecutable(entry);
        }
        catch (Exception)
        {
            // Falls through as null. See above.
        }

        _cache[key] = built;
        return built;
    }

    /// <summary>
    /// The store's cover art, centre-cropped to a square before it is scaled.
    ///
    /// <para>
    /// Cropped rather than squashed: a capsule is 2:3 and a menu icon is 1:1, so scaling the
    /// whole thing turns every game into an unreadable smear. The middle of a capsule is where
    /// the artwork is.
    /// </para>
    /// </summary>
    private static Image? FromArt(string? artFile)
    {
        if (string.IsNullOrEmpty(artFile) || !File.Exists(artFile))
        {
            return null;
        }

        // Loaded through a stream and copied, so the file is not left locked — Steam replaces
        // art in place and a held handle would make it fail.
        using var stream = new FileStream(artFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var source = Image.FromStream(stream);

        var side = Math.Min(source.Width, source.Height);
        var crop = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);

        return Scale(source, crop);
    }

    private static Image? FromExecutable(GameEntry entry)
    {
        if (FindExecutable(entry) is not { } exe)
        {
            return null;
        }

        using var icon = Icon.ExtractAssociatedIcon(exe);

        if (icon is null)
        {
            return null;
        }

        using var bitmap = icon.ToBitmap();
        return Scale(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
    }

    /// <summary>
    /// The program to take an icon from.
    ///
    /// <para>
    /// <see cref="ExecutablePicker"/>, the same chooser the catalogue and the ownership handover
    /// use — and for the same reason: its first rule is that an uninstaller is never a launch
    /// target, so a menu can never end up showing the uninstaller's icon for a game.
    /// </para>
    /// </summary>
    private static string? FindExecutable(GameEntry entry)
    {
        var folder = entry.InstallPath;

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            // A store title launched by URI with no recorded install path. Nothing to read.
            return null;
        }

        System.Collections.Immutable.ImmutableArray<string> executables =
        [
            .. Directory
                .EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OfType<string>(),
        ];

        return ExecutablePicker.Choose(entry.Name, new DirectoryInfo(folder).Name, executables) is { } chosen
            ? Path.Combine(folder, chosen.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? chosen : chosen + ".exe")
            : null;
    }

    private static Image Scale(Image source, Rectangle from)
    {
        var target = new Bitmap(Size, Size);

        using var graphics = Graphics.FromImage(target);

        // Box art shrunk to sixteen pixels is mostly aliasing unless this is asked for.
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        graphics.DrawImage(source, new Rectangle(0, 0, Size, Size), from, GraphicsUnit.Pixel);

        return target;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var image in _cache.Values)
        {
            image?.Dispose();
        }

        _cache.Clear();
    }
}
