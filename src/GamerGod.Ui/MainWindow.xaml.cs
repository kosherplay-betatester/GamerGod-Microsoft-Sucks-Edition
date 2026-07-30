using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GamerGod.Core.Catalogue;
using GamerGod.Core.Diagnostics;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Library;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using GamerGod.Ui.Audio;
using GamerGod.Ui.Library;
using GamerGod.Ui.Settings;
using GamerGod.Windows;

namespace GamerGod.Ui;

public partial class MainWindow : Window
{
    private readonly UiSounds _sounds = new();
    private readonly WindowsAmbientOperations _operations = new();
    private UiSettings _settings = UiSettings.Load();
    private CpuTopology? _topology;
    private bool _loading = true;
    private bool _libraryLoaded;
    private bool _catalogueLoaded;
    private GameTile? _selected;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private static string JournalPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GamerGod", "state", "session.journal");

    private MutationLedger BuildLedger() =>
        new(new FileJournal(JournalPath), new AmbientResolver(_operations, _topology));

    /// <summary>
    /// The line next to the edition name. One is picked per launch.
    ///
    /// <para>
    /// Every one of these is a fact about what this product actually does, told as a joke.
    /// A gag that is also true is the only kind worth putting in a title bar — the alternative
    /// is attitude with nothing behind it, which is what the rest of this category ships.
    /// </para>
    /// </summary>
    private static readonly string[] Quips =
    [
        "your cores, your rules",
        "it puts everything back",
        "no drivers were harmed",
        "we read the CPU, not your data",
        "the reboot is the undo button",
        "no telemetry, not even a little",
        "111 background apps, politely relocated",
        "measured, or not claimed",
        "Task Manager could never",
        "yes, it works with anti-cheat",
    ];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Indexed by the minute so it changes between launches without needing a random source,
        // which the rest of this codebase deliberately avoids.
        Quip.Text = "— " + Quips[(int)(DateTime.Now.Ticks / TimeSpan.TicksPerMinute % Quips.Length)];

        UpdateMaximiseGlyph();
        StateChanged += (_, _) => UpdateMaximiseGlyph();

        ApplySettingsToControls();

        _sounds.Enabled = _settings.SoundEnabled;
        _sounds.Volume = _settings.SoundVolume;

        LoadTopology();
        await RefreshStateAsync();

        _loading = false;

        if (_settings.ArmOnLaunch && MasterSwitch.IsChecked != true)
        {
            await ApplyAsync(turnOn: true);
        }
    }

    // ---------------------------------------------------------------- topology

    private void LoadTopology()
    {
        try
        {
            _topology = new WindowsTopologyProvider().Classify();
        }
        catch (Exception)
        {
            // A machine whose topology cannot be read is one GamerGod should say so about,
            // not one it should guess at.
            ProcessorName.Text = "Could not read this processor";
            ProcessorDetail.Text = "Domain partitioning is unavailable. Other levers still apply.";
            return;
        }

        ProcessorName.Text = _topology.ProcessorName;
        ProcessorDetail.Text =
            $"{_topology.PhysicalCoreCount} cores / {_topology.LogicalProcessorCount} threads"
            + (_topology.MaxFrequencyMhz > 0 ? $"  ·  {_topology.MaxFrequencyMhz} MHz" : string.Empty);

        TopologyKind.Text = _topology.Kind switch
        {
            Core.Hardware.TopologyKind.AsymmetricCache => "ASYMMETRIC CACHE",
            Core.Hardware.TopologyKind.Hybrid => "HYBRID CORES",
            Core.Hardware.TopologyKind.SymmetricMultiDomain => "MULTI-DOMAIN",
            _ => "UNIFORM",
        };

        RenderDomains(_topology);
    }

    /// <summary>
    /// The core map. Built in code rather than XAML because the shape depends entirely on the
    /// machine — a laptop with one domain and a Threadripper with four need the same code path,
    /// and a template with converters would obscure that rather than express it.
    /// </summary>
    private void RenderDomains(CpuTopology topology)
    {
        Domains.Items.Clear();

        foreach (var domain in topology.Domains)
        {
            var isGame = domain.Id == topology.GameDomain.Id && topology.CanPartition;
            var accent = (Brush)FindResource(isGame ? "Signal" : "InkFaint");

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(Label($"D{domain.Id}", "Display", 14, accent, FontWeights.SemiBold));
            header.Children.Add(Chip(
                topology.CanPartition ? (isGame ? "GAME" : "BACKGROUND") : "ALL WORK",
                accent,
                isGame));
            header.Children.Add(Label(
                $"{domain.PhysicalCoreCount}C / {domain.LogicalProcessorCount}T"
                + (domain.LastLevelCacheBytes > 0
                    ? $"   {domain.LastLevelCacheBytes / (1024 * 1024)} MB L3"
                    : "   no L3"),
                "Data", 11.5, (Brush)FindResource("InkDim")));

            // One cell per logical processor. This is the whole point of the panel: it turns
            // "I set an affinity, I think it worked" into something observable.
            var cells = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var lp in domain.LogicalProcessors)
            {
                cells.Children.Add(new Border
                {
                    Width = 20,
                    Height = 26,
                    Margin = new Thickness(0, 0, 3, 3),
                    CornerRadius = new CornerRadius(2),
                    Background = (Brush)FindResource(isGame ? "SignalGlow" : "Sunk"),
                    BorderBrush = (Brush)FindResource(isGame ? "SignalDim" : "LineSoft"),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = lp.ToString("00"),
                        FontFamily = (FontFamily)FindResource("Data"),
                        FontSize = 9,
                        Foreground = (Brush)FindResource(isGame ? "Signal" : "InkFaint"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(cells);

            Domains.Items.Add(new Border
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(13),
                CornerRadius = new CornerRadius(3),
                Background = (Brush)FindResource("Sunk"),
                BorderBrush = (Brush)FindResource(isGame ? "SignalDim" : "LineSoft"),
                BorderThickness = new Thickness(1),
                Child = body,
            });
        }

        if (!topology.CanPartition)
        {
            Domains.Items.Add(new TextBlock
            {
                Style = (Style)FindResource("BodyText"),
                Margin = new Thickness(0, 2, 0, 0),
                Text = "This processor has a single performance domain, so there is nowhere to "
                     + "move background work to. GamerGod will not pretend otherwise — every "
                     + "other lever still applies.",
            });
        }
    }

    // ---------------------------------------------------------------- state

    private async Task RefreshStateAsync()
    {
        bool active;
        try
        {
            active = await BuildLedger().HasOutstandingChangesAsync();
        }
        catch (Exception)
        {
            active = false;
        }

        MasterSwitch.IsChecked = active;
        SwitchLabel.Text = active ? "ON" : "OFF";
        SwitchLabel.Foreground = (Brush)FindResource(active ? "Signal" : "InkFaint");

        StateHeadline.Text = active ? "Game Mode is on" : "Game Mode is off";
        StateDetail.Text = active
            ? "Background apps have been moved out of your games' way. Turn this off, or reboot, "
              + "and everything goes back exactly as it was."
            : "Nothing on your machine is being changed. Turn this on and everything else moves "
              + "out of your games' way.";
    }

    private async void MasterSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var wantOn = MasterSwitch.IsChecked == true;
        await ApplyAsync(wantOn);
    }

    private async Task ApplyAsync(bool turnOn)
    {
        MasterSwitch.IsEnabled = false;

        try
        {
            if (turnOn)
            {
                await TurnOnAsync();
            }
            else
            {
                await TurnOffAsync();
            }
        }
        catch (Exception ex)
        {
            _sounds.Play(UiSound.Alert);
            ShowReceipt(
                "Nothing was changed.",
                [$"error: {ex.Message}", "Rebooting undoes anything that did apply."]);
        }
        finally
        {
            MasterSwitch.IsEnabled = true;
            await RefreshStateAsync();
        }
    }

    private async Task TurnOnAsync()
    {
        if (_topology is null)
        {
            _sounds.Play(UiSound.Alert);
            ShowReceipt("Cannot start.", ["This machine's processor layout could not be read."]);
            return;
        }

        var engine = new AmbientEngine(_operations, BuildLedger());
        var options = OptionsFromSettings(dryRun: _settings.ConfirmBeforeApplying);

        // Ambient-only by construction. Nothing this engine emits touches a game, which is why
        // an ambient-only permit refuses none of it.
        var permit = GameIntegrityPolicy.Evaluate(
            "desktop session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

        var restore = new RestoreStatus
        {
            Availability = RestoreAvailability.Unknown,
            Detail = "not required: no change in this session survives a reboot",
        };

        var preview = await engine.EnterAsync(
            Guid.NewGuid().ToString("N"), _topology, permit, options, restore);

        if (_settings.ConfirmBeforeApplying)
        {
            var proceed = MessageBox.Show(
                preview.Explain() + "\n\nApply these changes?",
                "GamerGod",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (proceed != MessageBoxResult.OK)
            {
                MasterSwitch.IsChecked = false;
                ShowReceipt("Nothing was changed.", []);
                return;
            }

            preview = await engine.EnterAsync(
                Guid.NewGuid().ToString("N"),
                _topology,
                permit,
                options with { DryRun = false },
                restore);
        }

        _sounds.Play(UiSound.Arm);

        var detail = preview.Applied
            .Select(k => $"applied   {k}")
            .Concat(preview.Failed.Select(f => $"FAILED    {f.Key}: {f.Error}"))
            .ToImmutableArray();

        ShowReceipt(preview.Explain(), detail);
    }

    private async Task TurnOffAsync()
    {
        var report = await BuildLedger().RevertAsync();

        _sounds.Play(report.IsClean ? UiSound.Disarm : UiSound.Alert);

        var detail = report.Reverted
            .Select(k => $"restored  {k}")
            .Concat(report.Failed.Select(f => $"FAILED    {f.Key}: {f.Error}"))
            .Concat(report.Unresolvable.Select(k => $"UNKNOWN   {k}"))
            .ToImmutableArray();

        ShowReceipt(
            report.IsClean
                ? "Your machine is exactly as it was."
                : "Some changes could not be undone. Rebooting will restore them.",
            detail);
    }

    private AmbientOptions OptionsFromSettings(bool dryRun) => new()
    {
        ConfineToAmbientDomain = _settings.ConfineToAmbientDomain,
        DemoteToEfficiencyMode = _settings.DemoteToEfficiencyMode,
        ManagePowerScheme = _settings.ManagePowerScheme,
        Services = _settings.SuppressBackgroundServices
            ? ["WSearch", "SysMain", "DiagTrack", "wuauserv", "BITS"]
            : [],
        DryRun = dryRun,

        // False, deliberately. This window can be closed while Game Mode stays on, so it
        // cannot hold a job-object handle: the confinement would evaporate the moment the
        // window shut, while the receipt claimed otherwise.
        CallerStaysResident = false,
    };

    private void ShowReceipt(string headline, ImmutableArray<string> detail)
    {
        ReceiptText.Text = headline;
        ReceiptItems.ItemsSource = detail.IsDefaultOrEmpty ? null : detail;
        ReceiptCard.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- library

    private async void Library_Refresh(object sender, RoutedEventArgs e)
    {
        Tick();
        await LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        ClearSelection();
        LibraryItems.Items.Clear();
        LibraryEmpty.Visibility = Visibility.Collapsed;
        LibraryCount.Text = "scanning…";

        ImmutableArray<GameEntry> games;
        try
        {
            games = await new WindowsGameLibrary().ScanAsync(default);
        }
        catch (Exception ex)
        {
            LibraryCount.Text = string.Empty;
            LibraryEmpty.Text = $"The library could not be read: {ex.Message}";
            LibraryEmpty.Visibility = Visibility.Visible;
            return;
        }

        var titles = games.Count(g => g.Source != GameSource.Emulator);
        var emulators = games.Length - titles;

        LibraryCount.Text = $"{titles} game{(titles == 1 ? "" : "s")}, "
                            + $"{emulators} emulator{(emulators == 1 ? "" : "s")}";

        if (games.IsEmpty)
        {
            // Honest rather than blank. An empty grid reads as a broken feature.
            LibraryEmpty.Text =
                "Nothing found. GamerGod looks for Steam, Epic and GOG titles through the "
                + "manifests those stores keep on disk, and for emulators it already knows "
                + "about. If you have games installed somewhere else, they will not appear "
                + "here yet — and GamerGod would rather show you nothing than invent a list.";
            LibraryEmpty.Visibility = Visibility.Visible;
            return;
        }

        foreach (var game in games)
        {
            LibraryItems.Items.Add(GameTile.From(game));
        }

        UpdateLibraryBlurb();

        // Only if the user has already said yes. The first fetch is always a button press.
        if (_settings.FetchCoverArt)
        {
            await FetchMissingCoversAsync();
        }
    }

    // ---- selection --------------------------------------------------------

    /// <summary>
    /// A tile press selects; it never launches.
    ///
    /// <para>
    /// Clicking box art is the most obvious thing to do on this page, and it used to start a
    /// game and silently reconfigure the CPU underneath it. Both of those are worth a
    /// deliberate second press, so the tile only ever picks a title and the tray states the
    /// consequences next to the control that causes them.
    /// </para>
    /// </summary>
    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not GameTile tile)
        {
            return;
        }

        Tick();

        if (ReferenceEquals(_selected, tile))
        {
            ClearSelection();
            return;
        }

        Select(tile);
    }

    private void Select(GameTile tile)
    {
        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = tile;
        tile.IsSelected = true;

        TrayThumb.Background = tile.Fallback;
        TrayInitial.Text = tile.Initial;
        TrayCover.Source = tile.Cover;
        TrayCover.Visibility = tile.HasCover ? Visibility.Visible : Visibility.Collapsed;

        TraySource.Text = tile.SourceLabel;
        TrayName.Text = tile.Name;
        TrayNote.Text = MasterSwitch.IsChecked == true
            ? "Game Mode is already on. Launching hands off to "
              + $"{tile.Entry.SourceLabel} so the game starts the way it expects."
            : "Launching turns Game Mode on first, then hands off to "
              + $"{tile.Entry.SourceLabel}. Rebooting undoes everything either way.";

        LaunchTray.Visibility = Visibility.Visible;
    }

    private void ClearSelection()
    {
        if (_selected is not null)
        {
            _selected.IsSelected = false;
            _selected = null;
        }

        // Released so a large decoded bitmap is not held alive by a hidden tray.
        TrayCover.Source = null;
        LaunchTray.Visibility = Visibility.Collapsed;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        Tick();
        ClearSelection();
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } tile)
        {
            return;
        }

        var game = tile.Entry;
        LaunchButton.IsEnabled = false;

        try
        {
            // Arm first, then launch. The other order would start the game onto a machine that
            // is still busy, which is the moment the confinement is most worth having.
            if (MasterSwitch.IsChecked != true)
            {
                MasterSwitch.IsChecked = true;
                await ApplyAsync(turnOn: true);
            }

            var armed = MasterSwitch.IsChecked == true;

            try
            {
                // UseShellExecute so a steam:// or com.epicgames.launcher:// URI reaches its
                // handler. Launching a store title's executable directly is how third-party
                // launchers break cloud saves and anti-cheat bootstrapping.
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(game.LaunchTarget)
                    {
                        UseShellExecute = true,
                    });

                _sounds.Play(UiSound.Confirm);

                // Says which of the two things happened rather than assuming both did — the
                // preview dialog can be declined, and then the game starts unarmed.
                TrayNote.Text = armed
                    ? $"Game Mode is on and {game.SourceLabel} has been asked to start this."
                    : $"{game.SourceLabel} has been asked to start this. Game Mode was not "
                      + "turned on, so nothing on your machine has changed.";
            }
            catch (Exception ex)
            {
                _sounds.Play(UiSound.Alert);
                MessageBox.Show(
                    $"Could not launch {game.Name}.\n\n{ex.Message}\n\n"
                    + (armed
                        ? "Game Mode is still on — turn it off here, run 'gamergod off', or reboot."
                        : "Nothing on your machine was changed."),
                    "GamerGod",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            LaunchButton.IsEnabled = true;
        }
    }

    // ---- cover art --------------------------------------------------------

    /// <summary>
    /// The explicit initiation Charter Article IV requires. Nothing downloads until this is
    /// pressed and the dialog it raises is accepted, and the preference it sets is the only
    /// thing that lets later refreshes fetch without asking again.
    /// </summary>
    private async void GetArt_Click(object sender, RoutedEventArgs e)
    {
        Tick();

        if (!_settings.FetchCoverArt)
        {
            var consent = MessageBox.Show(
                "GamerGod can download the missing box art from the same public store servers "
                + "your game client uses.\n\n"
                + "This is the only feature that uses the internet. If you say yes:\n\n"
                + "  · art is requested by numeric store id — one image per game\n"
                + "  · for a game with no cover published, the store is asked where its\n"
                + "    header art lives, then that image is fetched\n"
                + "  · no account, cookie, or identifier is attached\n"
                + "  · nothing about your machine, your settings, or your usage is sent\n"
                + "  · each image is saved locally, so it is requested exactly once\n\n"
                + "You can turn this back off in Settings at any time.\n\n"
                + "Download cover art?",
                "GamerGod",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (consent != MessageBoxResult.OK)
            {
                return;
            }

            _settings = _settings with { FetchCoverArt = true };
            _settings.Save();
            OptCoverArt.IsChecked = true;
            UpdateLibraryBlurb();
        }

        await FetchMissingCoversAsync();
    }

    private async Task FetchMissingCoversAsync()
    {
        var tiles = LibraryItems.Items.OfType<GameTile>().Where(t => !t.HasCover).ToImmutableArray();

        if (tiles.IsEmpty)
        {
            return;
        }

        GetArtButton.IsEnabled = false;
        var previous = LibraryCount.Text;
        LibraryCount.Text = $"fetching art for {tiles.Length}…";

        var found = 0;

        try
        {
            // Sequential on purpose. A handful of images is not worth saturating a connection
            // somebody may be gaming on, and the tiles fill in visibly one at a time.
            foreach (var tile in tiles)
            {
                if (await tile.TryFetchCoverAsync(default))
                {
                    found++;

                    if (ReferenceEquals(_selected, tile))
                    {
                        TrayCover.Source = tile.Cover;
                        TrayCover.Visibility = Visibility.Visible;
                    }
                }
            }
        }
        finally
        {
            GetArtButton.IsEnabled = true;

            // The count of what was actually found, not of what was attempted. Plenty of app
            // ids — demos, tools, delisted titles — have no published portrait art at all.
            LibraryCount.Text = found == 0
                ? previous + "  ·  no art published for the remaining " + tiles.Length
                : previous + $"  ·  found art for {found} of {tiles.Length}";
        }
    }

    /// <summary>
    /// The page's own description of itself has to change when the network preference does,
    /// or it becomes a false claim printed above the button that falsifies it.
    /// </summary>
    private void UpdateLibraryBlurb()
    {
        LibraryBlurb.Text = _settings.FetchCoverArt
            ? "Found by reading what your stores already keep on disk — no account is touched "
              + "and nothing about your machine is sent anywhere. Cover art downloading is on, "
              + "so missing art is fetched once per game from a public store CDN. Launching a "
              + "game turns Game Mode on first, then hands off to the store."
            : "Found by reading what your stores already keep on disk. Nothing is scraped, no "
              + "account is touched, and nothing leaves this machine. Games your store has not "
              + "cached art for get a generated tile — or press Get Cover Art to download the "
              + "real thing. Launching a game turns Game Mode on first, then hands off to the "
              + "store.";

        GetArtButton.Visibility = LibraryItems.Items.OfType<GameTile>().Any(t => !t.HasCover)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- get more

    /// <summary>
    /// Builds the catalogue page, then fills in what is already installed once winget answers.
    ///
    /// <para>
    /// Drawn before the lookup completes rather than after. The lookup takes about a second on
    /// a real machine, and a page that appears blank for a second reads as broken — so every
    /// row renders immediately with its state pending, and the chips resolve underneath.
    /// </para>
    /// </summary>
    private async Task LoadCatalogueAsync()
    {
        CatalogueItems.Items.Clear();
        SwitchNote.Text = SoftwareCatalogue.SwitchNote;

        var rows = new List<(CatalogueEntry Entry, Border Card, Button Action, Border Chip, TextBlock ChipText)>();

        foreach (var group in SoftwareCatalogue.Launchers.Concat(SoftwareCatalogue.Emulators))
        {
            CatalogueItems.Items.Add(new TextBlock
            {
                Style = (Style)FindResource("Eyebrow"),
                Text = group.Title.ToUpperInvariant(),
                Margin = new Thickness(0, 18, 0, 8),
            });

            foreach (var entry in group.Entries)
            {
                var row = BuildCatalogueRow(entry);
                CatalogueItems.Items.Add(row.Card);
                rows.Add(row);
            }
        }

        if (!WingetPackageManager.IsAvailable)
        {
            // Stated once, plainly, rather than as a failure on every row.
            CatalogueStatus.Text =
                "The Windows Package Manager is not on this machine, so nothing here can be "
                + "installed for you. Every entry still opens its official download page.";

            foreach (var row in rows)
            {
                Present(row, CatalogueState.Unavailable);
            }

            return;
        }

        CatalogueStatus.Text = "checking what you already have…";

        var ids = SoftwareCatalogue.All
            .Select(e => e.WingetId)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToImmutableArray();

        ImmutableHashSet<string> installed;
        try
        {
            installed = await WingetPackageManager.InstalledIdsAsync(ids, default);
        }
        catch (Exception)
        {
            installed = [];
        }

        foreach (var row in rows)
        {
            Present(row, StateOf(row.Entry, installed));
        }

        var count = rows.Count(r => r.Entry.WingetId is { } id && installed.Contains(id));

        CatalogueStatus.Text =
            $"{SoftwareCatalogue.All.Length} listed  ·  {count} already installed";
    }

    private static CatalogueState StateOf(CatalogueEntry entry, ImmutableHashSet<string> installed) =>
        entry.WingetId is not { } id ? CatalogueState.Unavailable
        : installed.Contains(id) ? CatalogueState.Installed
        : CatalogueState.NotInstalled;

    private (CatalogueEntry Entry, Border Card, Button Action, Border Chip, TextBlock ChipText)
        BuildCatalogueRow(CatalogueEntry entry)
    {
        var chipText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Display"),
            FontSize = 9.5,
            Foreground = (Brush)FindResource("InkFaint"),
            Text = "…",
        };

        var chip = new Border
        {
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(7, 2, 7, 3),
            CornerRadius = new CornerRadius(2),
            BorderBrush = (Brush)FindResource("LineSoft"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = chipText,
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(chip);
        head.Children.Add(new TextBlock
        {
            Text = entry.Name,
            Style = (Style)FindResource("H2"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = entry.Systems,
            Style = (Style)FindResource("BodyText"),
            FontSize = 12.5,
            Margin = new Thickness(0, 6, 0, 0),
        });

        if (entry.Note is { } note)
        {
            body.Children.Add(new TextBlock
            {
                Text = "→ " + note,
                Style = (Style)FindResource("BodyText"),
                FontSize = 12,
                Foreground = (Brush)FindResource("Warn"),
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        var action = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 104,
            Tag = entry,
            Content = "…",
        };

        action.Click += Catalogue_Action;

        var site = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Content = "SITE",
            Tag = entry,
            ToolTip = entry.Homepage,
        };

        site.Click += Catalogue_OpenSite;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        body.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(body, 0);
        grid.Children.Add(body);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(site);
        actions.Children.Add(action);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(14),
            Child = grid,
        };

        return (entry, card, action, chip, chipText);
    }

    private void Present(
        (CatalogueEntry Entry, Border Card, Button Action, Border Chip, TextBlock ChipText) row,
        CatalogueState state)
    {
        switch (state)
        {
            case CatalogueState.Installed:
                row.ChipText.Text = "INSTALLED";
                row.ChipText.Foreground = (Brush)FindResource("Good");
                row.Chip.BorderBrush = (Brush)FindResource("Good");
                row.Action.Content = "REMOVE";
                row.Action.IsEnabled = true;
                break;

            case CatalogueState.NotInstalled:
                row.ChipText.Text = "AVAILABLE";
                row.ChipText.Foreground = (Brush)FindResource("InkFaint");
                row.Chip.BorderBrush = (Brush)FindResource("LineSoft");
                row.Action.Content = "INSTALL";
                row.Action.IsEnabled = true;
                break;

            case CatalogueState.Unavailable:
                // Not a failure. Several of the best emulators only publish from their own
                // site, and the button says which situation this is rather than pretending.
                row.ChipText.Text = "MANUAL";
                row.ChipText.Foreground = (Brush)FindResource("InkFaint");
                row.Chip.BorderBrush = (Brush)FindResource("LineSoft");
                row.Action.Content = "GET IT";
                row.Action.IsEnabled = true;
                break;

            default:
                row.ChipText.Text = "…";
                row.Action.Content = "…";
                row.Action.IsEnabled = false;
                break;
        }

        row.Card.BorderBrush = (Brush)FindResource(
            state == CatalogueState.Installed ? "LineSoft" : "Line");
    }

    private void Catalogue_OpenSite(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is CatalogueEntry entry)
        {
            Tick();
            OpenExternal(entry.Homepage);
        }
    }

    private async void Catalogue_Action(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not CatalogueEntry entry)
        {
            return;
        }

        Tick();

        if (entry.WingetId is not { } wingetId)
        {
            OpenExternal(entry.Homepage);
            return;
        }

        var removing = (string)button.Content == "REMOVE";

        var confirm = MessageBox.Show(
            removing
                ? $"Remove {entry.Name}?\n\nWindows will uninstall it. Anything you saved with "
                  + "it — games, saves, configuration — is left alone; only the program goes.\n\n"
                  + $"  winget uninstall --id {wingetId} --exact"
                : $"Install {entry.Name}?\n\nWindows will download and install it, checking the "
                  + "publisher and the file hash first. GamerGod does not host or modify "
                  + "anything.\n\n"
                  + $"  {WingetPackageManager.DescribeInstall(wingetId)}"
                  + (entry.Note is { } note ? $"\n\nNote: {note}" : string.Empty),
            "GamerGod",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var restore = (string)button.Content;
        button.IsEnabled = false;
        button.Content = removing ? "REMOVING" : "INSTALLING";

        var progress = new Progress<string>(line => CatalogueStatus.Text = line);

        try
        {
            var result = removing
                ? await WingetPackageManager.UninstallAsync(wingetId, progress, default)
                : await WingetPackageManager.InstallAsync(wingetId, progress, default);

            if (result.Succeeded)
            {
                _sounds.Play(UiSound.Confirm);
                CatalogueStatus.Text = removing
                    ? $"{entry.Name} removed."
                    : $"{entry.Name} installed.";

                button.Content = removing ? "INSTALL" : "REMOVE";
            }
            else
            {
                _sounds.Play(UiSound.Alert);
                button.Content = restore;
                CatalogueStatus.Text = $"{entry.Name}: {(removing ? "removal" : "install")} did not complete.";

                MessageBox.Show(
                    $"{entry.Name} was not {(removing ? "removed" : "installed")}.\n\n"
                    + $"{result.Tail()}\n\n"
                    + "Nothing was left half-done — winget rolls back a failed install itself. "
                    + $"The official page is the other route: {entry.Homepage}",
                    "GamerGod",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _sounds.Play(UiSound.Alert);
            button.Content = restore;
            CatalogueStatus.Text = $"{entry.Name}: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // A machine with no default browser is not worth a dialog over.
        }
    }

    // ---------------------------------------------------------------- scan

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        Tick();
        Hazards.Items.Clear();
        ScanEmpty.Visibility = Visibility.Collapsed;

        ImmutableArray<Hazard> hazards;
        try
        {
            hazards = HazardScanner.Scan(new WindowsMachineProbe().Capture());
        }
        catch (Exception ex)
        {
            ScanEmpty.Text = $"The scan could not complete: {ex.Message}. Nothing was changed.";
            ScanEmpty.Visibility = Visibility.Visible;
            return;
        }

        var actionable = hazards.Where(h => h.Severity >= HazardSeverity.Low).ToImmutableArray();

        if (actionable.IsEmpty)
        {
            // A tool that admits it has nothing to report is the one people believe next time.
            ScanEmpty.Text = "Nothing to report. No broken drivers, no conflicting software, and "
                           + "nothing that would stop a game launching. This machine is in good shape.";
            ScanEmpty.Visibility = Visibility.Visible;
            return;
        }

        foreach (var hazard in hazards)
        {
            Hazards.Items.Add(BuildHazardCard(hazard));
        }
    }

    private Border BuildHazardCard(Hazard hazard)
    {
        var (label, brush) = hazard.Severity switch
        {
            HazardSeverity.High => ("HIGH", (Brush)FindResource("Crit")),
            HazardSeverity.Medium => ("MEDIUM", (Brush)FindResource("Warn")),
            HazardSeverity.Low => ("LOW", (Brush)FindResource("Warn")),
            _ => ("INFO", (Brush)FindResource("InkFaint")),
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Chip(label, brush, filled: hazard.Severity >= HazardSeverity.Medium));
        head.Children.Add(new TextBlock
        {
            Text = hazard.Title,
            Style = (Style)FindResource("H2"),
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = hazard.Detail,
            Style = (Style)FindResource("BodyText"),
            FontSize = 12.5,
            Margin = new Thickness(0, 7, 0, 0),
        });

        if (hazard.Remedy is { } remedy)
        {
            body.Children.Add(new TextBlock
            {
                Text = "→ " + remedy,
                Style = (Style)FindResource("BodyText"),
                FontSize = 12.5,
                Foreground = (Brush)FindResource("Ink"),
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(14),
            BorderBrush = hazard.Severity >= HazardSeverity.Medium
                ? brush
                : (Brush)FindResource("Line"),
            Child = body,
        };
    }

    // ---------------------------------------------------------------- settings

    private void ApplySettingsToControls()
    {
        OptConfine.IsChecked = _settings.ConfineToAmbientDomain;
        OptEfficiency.IsChecked = _settings.DemoteToEfficiencyMode;
        OptServices.IsChecked = _settings.SuppressBackgroundServices;
        OptPower.IsChecked = _settings.ManagePowerScheme;

        OptSound.IsChecked = _settings.SoundEnabled;
        OptVolume.Value = _settings.SoundVolume;
        VolumeLabel.Text = _settings.SoundVolume.ToString();
        OptNavSound.IsChecked = _settings.SoundOnNavigation;

        OptConfirm.IsChecked = _settings.ConfirmBeforeApplying;
        OptArmOnLaunch.IsChecked = _settings.ArmOnLaunch;
        OptCoverArt.IsChecked = _settings.FetchCoverArt;

        UpdateLibraryBlurb();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings = _settings with
        {
            ConfineToAmbientDomain = OptConfine.IsChecked == true,
            DemoteToEfficiencyMode = OptEfficiency.IsChecked == true,
            SuppressBackgroundServices = OptServices.IsChecked == true,
            ManagePowerScheme = OptPower.IsChecked == true,
            SoundOnNavigation = OptNavSound.IsChecked == true,
            ConfirmBeforeApplying = OptConfirm.IsChecked == true,
            ArmOnLaunch = OptArmOnLaunch.IsChecked == true,
            FetchCoverArt = OptCoverArt.IsChecked == true,
        };

        _settings.Save();
        UpdateLibraryBlurb();
        _sounds.Play(UiSound.Confirm);
    }

    private void Sound_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings = _settings with { SoundEnabled = OptSound.IsChecked == true };
        _settings.Save();
        _sounds.Enabled = _settings.SoundEnabled;

        // Played after enabling so switching it on demonstrates itself.
        _sounds.Play(UiSound.Confirm);
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var volume = (int)Math.Round(e.NewValue);

        if (VolumeLabel is not null)
        {
            VolumeLabel.Text = volume.ToString();
        }

        if (_loading)
        {
            return;
        }

        _settings = _settings with { SoundVolume = volume };
        _settings.Save();
        _sounds.Volume = volume;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        // Bypasses the navigation-tick preference: pressing Test should always make a sound if
        // sound is on at all, otherwise the button appears broken.
        var wasEnabled = _sounds.Enabled;
        _sounds.Enabled = true;
        _sounds.Play(UiSound.Arm);
        _sounds.Enabled = wasEnabled;
    }

    // ---------------------------------------------------------------- chrome

    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (PageDashboard is null)
        {
            return;
        }

        var page = (sender as RadioButton)?.Tag as string ?? "Dashboard";

        Show(PageDashboard, page == "Dashboard");
        Show(PageLibrary, page == "Library");
        Show(PageGetMore, page == "GetMore");
        Show(PageMachine, page == "Machine");
        Show(PageSettings, page == "Settings");

        Tick();

        // Scanned on first open rather than at startup: reading several stores' manifests
        // should not delay the window appearing.
        if (page == "Library" && LibraryItems.Items.Count == 0 && !_libraryLoaded)
        {
            _libraryLoaded = true;
            _ = LoadLibraryAsync();
        }

        // Same reasoning, and this one shells out to winget as well.
        if (page == "GetMore" && !_catalogueLoaded)
        {
            _catalogueLoaded = true;
            _ = LoadCatalogueAsync();
        }
    }

    /// <summary>
    /// Shows or hides a page, playing the entrance animation on the one being shown.
    ///
    /// <para>
    /// The transform is attached here rather than in XAML because a shared Storyboard cannot
    /// create one, and five pages each declaring their own would be five chances to get it
    /// subtly different.
    /// </para>
    /// </summary>
    private void Show(UIElement page, bool visible)
    {
        if (!visible)
        {
            page.Visibility = Visibility.Collapsed;
            return;
        }

        page.RenderTransform = new TranslateTransform();
        page.Visibility = Visibility.Visible;

        ((Storyboard)FindResource("PageEnter")).Begin((FrameworkElement)page);
    }

    private void Tick()
    {
        if (!_loading && _settings.SoundOnNavigation)
        {
            _sounds.Play(UiSound.Tick);
        }
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        // Double-click toggles maximise, which is what every other title bar on Windows does
        // and the first thing anybody tries on a custom one.
        if (e.ClickCount == 2)
        {
            ToggleMaximise();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Dragging a maximised window has to restore it first, and the restored window
            // must land under the cursor rather than jumping to where it used to be.
            var cursor = PointToScreen(e.GetPosition(this));
            var ratio = RestoreBounds.Width / ActualWidth;

            WindowState = WindowState.Normal;
            Left = cursor.X - (e.GetPosition(this).X * ratio);
            Top = cursor.Y - e.GetPosition(this).Y;

            UpdateMaximiseGlyph();
        }

        DragMove();
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e)
    {
        Tick();
        ToggleMaximise();
    }

    private void ToggleMaximise()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        UpdateMaximiseGlyph();
    }

    /// <summary>
    /// A maximised window with rounded corners and a border leaves a dark seam down each edge
    /// of the screen, because the shape no longer matches the monitor. Squaring it off while
    /// maximised is what makes the window look native rather than like a panel floating on
    /// black.
    /// </summary>
    private void UpdateMaximiseGlyph()
    {
        var maximised = WindowState == WindowState.Maximized;

        MaximiseButton.Content = maximised ? "" : "";
        MaximiseButton.ToolTip = maximised ? "Restore" : "Maximise";

        Shell.CornerRadius = new CornerRadius(maximised ? 0 : 8);
        Shell.BorderThickness = new Thickness(maximised ? 0 : 1);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- helpers

    private TextBlock Label(
        string text, string family, double size, Brush brush, FontWeight? weight = null) => new()
        {
            Text = text,
            FontFamily = (FontFamily)FindResource(family),
            FontSize = size,
            Foreground = brush,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

    private Border Chip(string text, Brush brush, bool filled) => new()
    {
        Margin = new Thickness(0, 0, 10, 0),
        Padding = new Thickness(7, 2, 7, 3),
        CornerRadius = new CornerRadius(2),
        BorderBrush = brush,
        BorderThickness = new Thickness(1),
        Background = filled ? (Brush)FindResource("SignalGlow") : Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)FindResource("Display"),
            FontSize = 9.5,
            Foreground = brush,
        },
    };

    /// <summary>
    /// Rebuilds ambient mutations from the journal so this window can undo a session it did
    /// not itself apply — after a restart, for instance.
    /// </summary>
    private sealed class AmbientResolver(IAmbientOperations os, CpuTopology? topology) : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key)
        {
            if (key.StartsWith("service:", StringComparison.Ordinal))
            {
                return new ServiceSuspensionMutation(os, key["service:".Length..]);
            }

            return key switch
            {
                "ecoqos:background" => new EfficiencyModeMutation(os, []),
                "power:scheme" => new PowerSchemeMutation(os, "GamerGod"),
                "affinity:ambient-domain" when topology is not null =>
                    new AffinityConfinementMutation(os, default, [], topology),
                _ => null,
            };
        }
    }
}

