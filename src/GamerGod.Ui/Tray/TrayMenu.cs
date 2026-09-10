using System;
using System.Collections.Immutable;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GamerGod.Core.Library;

namespace GamerGod.Ui.Tray;

/// <summary>
/// The notification-area icon and the menu behind it.
///
/// <para>
/// The point of this is that GamerGod stops being a window you have to keep in front of you. Game
/// Mode outlives the window by design — the session is journalled precisely so it does — and
/// before this there was no way to turn it off, or to start a game, without restoring the whole
/// application first.
/// </para>
///
/// <para>
/// The menu is rebuilt from scratch on every open rather than mutated in place. It has exactly
/// two live facts in it — whether Game Mode is on, and what is in the library — and both change
/// underneath it. A menu that is assembled at the moment it is shown cannot be stale; one that is
/// patched as things change can be, and the failure looks like an application that lies about
/// whether your machine is partitioned.
/// </para>
/// </summary>
public sealed class TrayMenu : IDisposable
{
    /// <summary>
    /// Games listed in the quick-launch submenu.
    ///
    /// <para>
    /// A cap, because a notification-area menu is not a scrollable list: a library of three
    /// hundred titles produces a menu taller than the screen, and Windows silently clips it. What
    /// is dropped is said out loud in the menu rather than hidden.
    /// </para>
    /// </summary>
    public const int MaximumGames = 25;

    private readonly NotifyIcon _icon;
    private readonly Func<bool> _isArmed;
    private readonly Func<ImmutableArray<GameEntry>> _games;
    private readonly Action<bool> _setArmed;
    private readonly Action<GameEntry> _launch;
    private readonly Action _show;
    private readonly Action _exit;

    private bool _disposed;

    public TrayMenu(
        Func<bool> isArmed,
        Func<ImmutableArray<GameEntry>> games,
        Action<bool> setArmed,
        Action<GameEntry> launch,
        Action show,
        Action exit)
    {
        _isArmed = isArmed ?? throw new ArgumentNullException(nameof(isArmed));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _setArmed = setArmed ?? throw new ArgumentNullException(nameof(setArmed));
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = false,
            Text = "GamerGod",
            ContextMenuStrip = new ContextMenuStrip(),
        };

        // Rebuilt here rather than when something changes, so it cannot be stale. See above.
        _icon.ContextMenuStrip.Opening += (_, _) => Rebuild();

        // Double-click is the gesture everybody already knows for "give me the window back".
        _icon.DoubleClick += (_, _) => _show();
    }

    /// <summary>Whether the icon is currently in the notification area.</summary>
    public bool IsShowing => !_disposed && _icon.Visible;

    public void Show()
    {
        if (!_disposed)
        {
            _icon.Visible = true;
        }
    }

    public void Hide()
    {
        if (!_disposed)
        {
            _icon.Visible = false;
        }
    }

    /// <summary>
    /// Says what happened, from the notification area, when there is no window to say it in.
    ///
    /// <para>
    /// Used only for things the user asked for and would otherwise get no answer to — a game
    /// launched from the menu, Game Mode turned on from the menu. Not for anything periodic:
    /// Charter Article IV rules out telling anybody else what this machine is doing, and a
    /// balloon every few minutes would be the local version of the same rudeness.
    /// </para>
    /// </summary>
    public void Notify(string title, string message)
    {
        if (_disposed || !_icon.Visible)
        {
            return;
        }

        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = ToolTipIcon.None;
        _icon.ShowBalloonTip(4000);
    }

    /// <summary>Keeps the hover text honest about the machine's state.</summary>
    public void RefreshTooltip()
    {
        if (_disposed)
        {
            return;
        }

        // Windows truncates this at 63 characters and gives no warning when it does.
        _icon.Text = _isArmed()
            ? "GamerGod — Game Mode is ON"
            : "GamerGod — Game Mode is off";
    }

    private void Rebuild()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();

        var armed = _isArmed();

        var header = new ToolStripMenuItem(armed ? "Game Mode is ON" : "Game Mode is off")
        {
            Enabled = false,
        };

        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        var toggle = new ToolStripMenuItem(armed ? "Turn Game Mode off" : "Turn Game Mode on");
        toggle.Click += (_, _) => _setArmed(!armed);
        menu.Items.Add(toggle);

        menu.Items.Add(BuildQuickLaunch());
        menu.Items.Add(new ToolStripSeparator());

        var open = new ToolStripMenuItem("Open GamerGod");
        open.Click += (_, _) => _show();
        menu.Items.Add(open);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => _exit();
        menu.Items.Add(exit);
    }

    private ToolStripMenuItem BuildQuickLaunch()
    {
        var quick = new ToolStripMenuItem("Library Quick Launch");
        var games = _games();

        if (games.IsDefaultOrEmpty)
        {
            // Distinguishes "no games" from "the library has not been read yet", because the
            // second is fixed by opening the window and the first is not.
            quick.DropDownItems.Add(new ToolStripMenuItem("No games found yet") { Enabled = false });
            return quick;
        }

        foreach (var game in games.Take(MaximumGames))
        {
            var entry = game;

            // The store is in the label because two launchers routinely list the same title, and
            // a menu with the same word twice is a menu you cannot choose from.
            var item = new ToolStripMenuItem($"{entry.Name}   ·   {entry.SourceLabel}");
            item.Click += (_, _) => _launch(entry);
            quick.DropDownItems.Add(item);
        }

        if (games.Length > MaximumGames)
        {
            quick.DropDownItems.Add(new ToolStripSeparator());
            quick.DropDownItems.Add(new ToolStripMenuItem(
                $"{games.Length - MaximumGames} more — open GamerGod to see them all")
            {
                Enabled = false,
            });
        }

        return quick;
    }

    /// <summary>
    /// The application's own icon, or Windows' default when it cannot be read.
    ///
    /// <para>
    /// Falling back rather than throwing: a notification-area icon that cannot load its picture
    /// is a cosmetic problem, and taking the application down over one would turn it into a
    /// startup failure.
    /// </para>
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } path
                && Icon.ExtractAssociatedIcon(path) is { } extracted)
            {
                return extracted;
            }
        }
        catch (Exception)
        {
            // Falls through to the default below.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Hidden before disposal, or the icon is left behind in the notification area until the
        // user hovers over it — Windows only reaps those lazily.
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
