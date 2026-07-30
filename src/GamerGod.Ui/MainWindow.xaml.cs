using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GamerGod.Core.Catalogue;
using GamerGod.Core.Diagnostics;
using GamerGod.Core.Engine;
using GamerGod.Core.FreeGames;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Library;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using GamerGod.Core.Search;
using GamerGod.Core.Updates;
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
    private AvailableRelease? _update;
    private ImmutableArray<FreeGame> _freeGames = [];
    private FreeGameSort _freeSort = FreeGameSort.Popularity;
    private bool _freeLoaded;
    private ImmutableArray<GameTile> _libraryTiles = [];
    private ImmutableArray<AppTile> _appTiles = [];
    private string _catalogueSummary = string.Empty;

    /// <summary>Catalogue headings with their rows, so a search can hide both together.</summary>
    private readonly List<(TextBlock Heading, List<(CatalogueEntry Entry, Border Card)> Entries)> _catalogueGroups = [];

    /// <summary>Catalogue row icons, by entry id, so installed programs can upgrade in place.</summary>
    private readonly Dictionary<string, Image> _catalogueIcons = [];

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

        VersionLabel.Text = "v" + (System.Reflection.Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0] ?? "unknown");

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

        // Last, and only if asked. Nothing about the window depends on it, so a slow or
        // unreachable GitHub delays nothing the user can see.
        if (_settings.CheckForUpdates)
        {
            await CheckForUpdatesAsync(announceResult: false);
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

    /// <summary>
    /// Applies or reverts through the elevated command-line tool.
    ///
    /// <para>
    /// Arming writes the revert journal, which lives in a directory only administrators may
    /// write to, and sets scheduling state on processes owned by other accounts. This window has
    /// neither right, and should not: everything else it does — the core map, the library, the
    /// catalogue — needs none, so holding administrator for all of it to serve one switch would
    /// be the wrong trade. The consent prompt appears at the moment the machine changes.
    /// </para>
    ///
    /// <para>
    /// Returns true when the caller should stop, because the work was brokered and reported.
    /// </para>
    /// </summary>
    private async Task<bool> BrokerAsync(bool turnOn)
    {
        if (Elevation.IsElevated)
        {
            return false;
        }

        var verb = turnOn ? "on" : "off";

        // The preview setting has to keep its promise here too. The itemised list the in-process
        // path shows needs rights this window does not have, so what is confirmed is the set of
        // levers rather than the exact process count — stated as such, rather than quietly
        // dropping a setting the user switched on.
        if (turnOn && _settings.ConfirmBeforeApplying)
        {
            var levers = new List<string>();

            if (_settings.ConfineToAmbientDomain)
            {
                levers.Add("  · move background apps off your game's cores");
            }

            if (_settings.DemoteToEfficiencyMode)
            {
                levers.Add("  · set background apps to efficiency mode");
            }

            if (_settings.SuppressBackgroundServices)
            {
                levers.Add("  · pause the search indexer and update checks");
            }

            if (_settings.ManagePowerScheme)
            {
                levers.Add("  · activate a copy of your power plan, tuned for performance");
            }

            var proceed = MessageBox.Show(
                "Turning Game Mode on will:\n\n"
                + string.Join("\n", levers)
                + "\n\nAnti-cheat services, fan and thermal software, audio, input devices and "
                + "system-critical processes are never touched.\n\n"
                + "Windows will ask for permission first — the journal that restores your "
                + "machine lives where only administrators can write.\n\n"
                + "Apply these changes?",
                "GamerGod",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (proceed != MessageBoxResult.OK)
            {
                MasterSwitch.IsChecked = false;
                ShowReceipt("Nothing was changed.", []);
                return true;
            }
        }

        var result = await Elevation.RunAsync(verb, default);

        switch (result.Outcome)
        {
            case ElevationOutcome.Succeeded:
                _sounds.Play(turnOn ? UiSound.Arm : UiSound.Disarm);

                // Reported from the journal rather than from parsed console output — the
                // resulting state is better evidence than the text describing it.
                ShowReceipt(
                    turnOn
                        ? "Game Mode is on. Background work has been moved out of your games' way."
                        : "Your machine is exactly as it was.",
                    [$"ran      gamergod {verb}   (as administrator)"]);
                break;

            case ElevationOutcome.Declined:
                // Not a failure. Saying so plainly is the difference between a user who
                // understands what happened and one who thinks the application is broken.
                MasterSwitch.IsChecked = !turnOn;
                ShowReceipt(
                    "Nothing was changed.",
                    [
                        "You closed the Windows permission prompt.",
                        turnOn
                            ? "Turning Game Mode on needs administrator rights, because it writes the"
                            : "Turning Game Mode off needs administrator rights, because it rewrites the",
                        "journal that restores your machine — a file other users must not be able to edit.",
                    ]);
                break;

            case ElevationOutcome.ToolMissing:
                _sounds.Play(UiSound.Alert);
                ShowReceipt(
                    "Nothing was changed.",
                    [
                        result.Problem ?? "gamergod.exe is missing.",
                        "Reinstalling GamerGod puts it back.",
                    ]);
                break;

            default:
                _sounds.Play(UiSound.Alert);
                ShowReceipt(
                    "Nothing was changed.",
                    [result.Problem ?? "the elevated command failed", "Rebooting undoes anything that did apply."]);
                break;
        }

        return true;
    }

    private async Task TurnOnAsync()
    {
        if (_topology is null)
        {
            _sounds.Play(UiSound.Alert);
            ShowReceipt("Cannot start.", ["This machine's processor layout could not be read."]);
            return;
        }

        if (await BrokerAsync(turnOn: true))
        {
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
        if (await BrokerAsync(turnOn: false))
        {
            return;
        }

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

        // Kept whole so searching filters a list rather than re-scanning every store on each
        // keystroke.
        _libraryTiles = [.. games.Select(GameTile.From)];

        RenderLibrary();
        UpdateLibraryBlurb();

        // Only if the user has already said yes. The first fetch is always a button press.
        if (_settings.FetchCoverArt)
        {
            await FetchMissingCoversAsync();
        }
    }

    /// <summary>
    /// Draws the library grid, filtered by whatever is in the search box.
    ///
    /// <para>
    /// Search covers the source and the emulated systems as well as the title, so "steam" or
    /// "gamecube" are both useful queries in a grid of box art.
    /// </para>
    /// </summary>
    private void RenderLibrary()
    {
        var query = LibrarySearch?.Text ?? string.Empty;
        var searching = !string.IsNullOrWhiteSpace(query);

        ClearSelection();
        LibraryItems.Items.Clear();
        LibraryEmpty.Visibility = Visibility.Collapsed;

        var matched = _libraryTiles
            .Select(t => (Tile: t, Score: FuzzySearch.Best(query, t.Name, t.SourceLabel, t.Subtitle)))
            .Where(x => !searching || x.Score is not null)
            .ToList();

        if (searching)
        {
            matched = [.. matched.OrderByDescending(x => x.Score!.Value)];
        }

        foreach (var (tile, _) in matched)
        {
            LibraryItems.Items.Add(tile);
        }

        if (_libraryTiles.IsEmpty)
        {
            // Honest rather than blank. An empty grid reads as a broken feature.
            LibraryCount.Text = string.Empty;
            LibraryEmpty.Text =
                "Nothing found. GamerGod looks for Steam, Epic and GOG titles through the "
                + "manifests those stores keep on disk, and for emulators it already knows "
                + "about. If you have games installed somewhere else, they will not appear "
                + "here yet — and GamerGod would rather show you nothing than invent a list.";
            LibraryEmpty.Visibility = Visibility.Visible;
            return;
        }

        if (searching)
        {
            LibraryCount.Text = $"{matched.Count} of {_libraryTiles.Length} match “{query.Trim()}”";

            if (matched.Count == 0)
            {
                LibraryEmpty.Text =
                    $"Nothing matched “{query.Trim()}”, including allowing for misspellings.";
                LibraryEmpty.Visibility = Visibility.Visible;
            }

            return;
        }

        var titles = _libraryTiles.Count(t => !t.IsEmulator);
        var emulators = _libraryTiles.Length - titles;

        LibraryCount.Text = $"{titles} game{(titles == 1 ? "" : "s")}, "
                            + $"{emulators} emulator{(emulators == 1 ? "" : "s")}";
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
                "GamerGod can download the artwork it is missing: box art for your games, from "
                + "the same public store servers your game client uses, and logos for emulators "
                + "and launchers, from each project's own website.\n\n"
                + "This is the only feature that uses the internet. If you say yes:\n\n"
                + "  · game art is requested by numeric store id — one image per game\n"
                + "  · for a game with no cover published, the store is asked where its\n"
                + "    header art lives, then that image is fetched\n"
                + "  · a program's logo comes from its own site, the same one its entry links to\n"
                + "  · no account, cookie, or identifier is attached\n"
                + "  · nothing about your machine, your settings, or your usage is sent\n"
                + "  · every image is saved locally, so it is requested exactly once\n\n"
                + "Programs you already have need none of this — their icon is read out of "
                + "their own executable, with no network involved.\n\n"
                + "You can turn this back off in Settings at any time.\n\n"
                + "Download artwork?",
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

    // ---------------------------------------------------------------- search

    /// <summary>
    /// Escape clears whichever box has focus, from inside it.
    ///
    /// <para>
    /// The only way out of a filtered list that does not require selecting text and deleting it,
    /// and the reason the field draws an ESC hint rather than a clear button — a button that only
    /// matters while typing is a button your hand is nowhere near.
    /// </para>
    /// </summary>
    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is TextBox box && box.Text.Length > 0)
        {
            box.Clear();
            e.Handled = true;
        }
    }

    private void LibrarySearch_Changed(object sender, TextChangedEventArgs e) => RenderLibrary();

    private async void FreeSearch_Changed(object sender, TextChangedEventArgs e)
    {
        RenderFreeGames();
        await LoadFreeArtAsync();
    }

    private void InstalledSearch_Changed(object sender, TextChangedEventArgs e) => RenderInstalledApps();

    /// <summary>
    /// Filters the catalogue in place.
    ///
    /// <para>
    /// Rows are hidden rather than rebuilt, because rebuilding would discard the installed state
    /// and the real icons the page spent a second working out. Group headings follow their
    /// contents — a heading with nothing under it reads as a section that failed to load.
    /// </para>
    /// </summary>
    private void CatalogueSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var query = CatalogueSearch.Text;
        var shown = 0;

        foreach (var (heading, entries) in _catalogueGroups)
        {
            var visible = 0;

            foreach (var (entry, card) in entries)
            {
                var matches = FuzzySearch.Matches(
                    query, entry.Name, entry.Systems, entry.Group, entry.Note);

                card.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;

                if (matches)
                {
                    visible++;
                }
            }

            heading.Visibility = visible > 0 ? Visibility.Visible : Visibility.Collapsed;
            shown += visible;
        }

        CatalogueStatus.Text = string.IsNullOrWhiteSpace(query)
            ? _catalogueSummary
            : shown == 0
                ? $"nothing matches “{query.Trim()}”"
                : $"{shown} of {SoftwareCatalogue.All.Length} match “{query.Trim()}”";
    }

    // ---------------------------------------------------------------- free games

    /// <summary>
    /// Opens the page with whatever was last fetched, then asks before going online.
    ///
    /// <para>
    /// Both halves matter. Showing the cached list first means the page is useful immediately
    /// rather than gated behind a prompt, and asking before refreshing is what Charter Article
    /// IV requires of an outbound connection — which is also exactly how it was requested.
    /// </para>
    /// </summary>
    private async Task LoadFreeGamesAsync(bool promptToRefresh)
    {
        var cached = FreeGameSource.LoadCached();
        _freeGames = cached.Games;

        RenderFreeGames();

        if (!promptToRefresh)
        {
            return;
        }

        var age = cached.RetrievedUtc is { } when
            ? DescribeAge(DateTimeOffset.UtcNow - when)
            : null;

        var question = _freeGames.IsEmpty
            ? "Look for free games now?\n\n"
              + "GamerGod will ask a public games catalogue for its current list. It needs no "
              + "account and sends nothing about you or your machine — just a request for the "
              + "list, and one for each game's artwork.\n\n"
              + "No game is downloaded. Each entry opens that game's page so you can get it "
              + "from the publisher yourself."
            : $"You have {_freeGames.Length} free games from {age}.\n\n"
              + "Check the catalogue for new ones? It needs no account and sends nothing about "
              + "you or your machine.";

        if (MessageBox.Show(question, "GamerGod", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
        {
            if (_freeGames.IsEmpty)
            {
                FreeEmpty.Text =
                    "Nothing fetched yet. Press Find free games whenever you want the list — "
                    + "GamerGod will not go looking on its own.";
                FreeEmpty.Visibility = Visibility.Visible;
            }

            return;
        }

        await RefreshFreeGamesAsync();
    }

    private static string DescribeAge(TimeSpan age) => age.TotalMinutes switch
    {
        < 2 => "a moment ago",
        < 60 => $"{(int)age.TotalMinutes} minutes ago",
        < 48 * 60 => $"{(int)age.TotalHours} hours ago",
        _ => $"{(int)age.TotalDays} days ago",
    };

    private async Task RefreshFreeGamesAsync()
    {
        FreeRefresh.IsEnabled = false;
        FreeCount.Text = "asking the catalogue…";

        try
        {
            var result = await FreeGameSource.RefreshAsync(default);

            if (result.Problem is { } problem)
            {
                _sounds.Play(UiSound.Alert);

                // Says which of the two happened rather than blaming the network for a shape
                // change, or the other way round.
                FreeCount.Text = result.Games.IsEmpty
                    ? $"could not fetch the list: {problem}"
                    : $"could not refresh ({problem}) — showing the last list";
            }

            _freeGames = result.Games;
            RenderFreeGames();

            if (result.Problem is null)
            {
                _sounds.Play(UiSound.Confirm);
            }

            await LoadFreeArtAsync();
        }
        finally
        {
            FreeRefresh.IsEnabled = true;
        }
    }

    private void RenderFreeGames()
    {
        FreeItems.Items.Clear();
        FreeEmpty.Visibility = Visibility.Collapsed;
        FreeSortButton.Content = "SORT: " + FreeGameFeed.Describe(_freeSort);

        var query = FreeSearch?.Text ?? string.Empty;
        var searching = !string.IsNullOrWhiteSpace(query);

        var matched = FreeGameFeed.Sort(_freeGames, _freeSort)
            .Select(g => (Game: g, Score: FuzzySearch.Best(query, g.Title, g.Genre, g.Publisher)))
            .Where(x => !searching || x.Score is not null)
            .ToList();

        // While searching, relevance replaces the chosen ordering — a sort that ignored the
        // query would bury the one game somebody typed the name of.
        if (searching)
        {
            matched = [.. matched.OrderByDescending(x => x.Score!.Value)];
        }

        foreach (var (game, _) in matched)
        {
            FreeItems.Items.Add(FreeGameTile.From(game));
        }

        if (_freeGames.IsEmpty)
        {
            return;
        }

        if (searching)
        {
            FreeCount.Text = matched.Count == 0
                ? $"none of {_freeGames.Length} match “{query.Trim()}”"
                : $"{matched.Count} of {_freeGames.Length} match “{query.Trim()}”";

            if (matched.Count == 0)
            {
                FreeEmpty.Text =
                    $"Nothing matched “{query.Trim()}”, including allowing for misspellings. "
                    + "Try fewer letters — searching also covers genre and publisher.";
                FreeEmpty.Visibility = Visibility.Visible;
            }

            return;
        }

        var genres = _freeGames.Select(g => g.Genre).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        FreeCount.Text = $"{_freeGames.Length} free games  ·  {genres} genres";
    }

    /// <summary>
    /// Fills in key art, sequentially, updating each tile as it arrives.
    ///
    /// <para>
    /// Sequential on purpose: several hundred parallel image requests would saturate a
    /// connection somebody may be gaming on, and the grid filling in visibly reads better than
    /// a frozen page followed by everything at once.
    /// </para>
    /// </summary>
    private async Task LoadFreeArtAsync()
    {
        var tiles = FreeItems.Items.OfType<FreeGameTile>().ToImmutableArray();

        if (tiles.IsEmpty)
        {
            return;
        }

        var restore = FreeCount.Text;
        var loaded = 0;

        foreach (var tile in tiles)
        {
            if (await tile.TryLoadArtAsync(default))
            {
                loaded++;

                if (loaded % 12 == 0)
                {
                    FreeCount.Text = $"{restore}  ·  loading art {loaded}/{tiles.Length}";
                }
            }
        }

        FreeCount.Text = restore;
    }

    private async void Free_Refresh(object sender, RoutedEventArgs e)
    {
        Tick();

        if (MessageBox.Show(
                "Ask the catalogue for its current list of free games?\n\n"
                + "No account is used and nothing about you or your machine is sent. No game is "
                + "downloaded — each entry opens its page so you can get it from the publisher.",
                "GamerGod",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RefreshFreeGamesAsync();
    }

    /// <summary>
    /// Cycles the ordering.
    ///
    /// <para>
    /// A button rather than a dropdown because there are four of them and every one is one
    /// press away — a combo box for four options is a menu built to look like a setting.
    /// </para>
    /// </summary>
    private async void Free_CycleSort(object sender, RoutedEventArgs e)
    {
        Tick();

        _freeSort = _freeSort switch
        {
            FreeGameSort.Popularity => FreeGameSort.Newest,
            FreeGameSort.Newest => FreeGameSort.Alphabetical,
            FreeGameSort.Alphabetical => FreeGameSort.Genre,
            _ => FreeGameSort.Popularity,
        };

        RenderFreeGames();

        // Re-rendering rebuilds every tile, so art already on disk has to be re-attached.
        await LoadFreeArtAsync();
    }

    private void Free_Open(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FreeGameTile tile)
        {
            return;
        }

        Tick();

        // Validated in Core before it ever reaches here, and again on the way out. This is a
        // navigation built from a remote server's response.
        if (FreeGameFeed.IsCataloguePage(tile.Game.PageUrl))
        {
            OpenExternal(tile.Game.PageUrl);
        }
    }

    // ---------------------------------------------------------------- updates

    /// <summary>The running build, as the assembly records it.</summary>
    private static string RunningVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    /// <summary>
    /// The startup check. Silent unless something is genuinely newer.
    ///
    /// <para>
    /// Nothing here reports a failure. A machine that is offline, rate-limited, or behind a
    /// proxy has no update problem, and a dialog at startup about a check nobody was waiting on
    /// would be worse than the missing information.
    /// </para>
    /// </summary>
    private async Task CheckForUpdatesAsync(bool announceResult)
    {
        if (announceResult)
        {
            UpdateStatus.Text = "checking…";
        }

        AvailableRelease? release;
        try
        {
            release = await UpdateChecker.CheckAsync(RunningVersion, default);
        }
        catch (Exception)
        {
            release = null;
        }

        _update = release;

        if (release is null)
        {
            UpdateCard.Visibility = Visibility.Collapsed;

            if (announceResult)
            {
                // Only ever said in response to a press. Deliberately not distinguishing "up to
                // date" from "could not reach GitHub" would be dishonest, so it says both.
                UpdateStatus.Text = $"nothing newer than {ShortVersion()} was found";
            }

            return;
        }

        UpdateHeadline.Text = release.Title;

        UpdateDetail.Text = release.CanDownload
            ? $"You are running {ShortVersion()}. The download is checked against the fingerprint "
              + "GitHub published before anything is run, and installing it is still your decision."
            : $"You are running {ShortVersion()}. This release published no verifiable installer, "
              + "so the release page is the way to get it.";

        UpdateDownloadButton.Visibility = release.CanDownload ? Visibility.Visible : Visibility.Collapsed;
        UpdateFingerprint.Visibility = Visibility.Collapsed;
        UpdateCard.Visibility = Visibility.Visible;

        if (announceResult)
        {
            UpdateStatus.Text = $"{release.Version} is available — see the Dashboard";
        }

        _sounds.Play(UiSound.Confirm);
    }

    private static string ShortVersion() =>
        ReleaseVersion.TryParse(RunningVersion, out var v) ? v.ToString() : RunningVersion;

    private async void Update_CheckNow(object sender, RoutedEventArgs e)
    {
        Tick();
        CheckNowButton.IsEnabled = false;

        try
        {
            await CheckForUpdatesAsync(announceResult: true);
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    private void Update_Notes(object sender, RoutedEventArgs e)
    {
        if (_update is { } release)
        {
            Tick();
            OpenExternal(release.PageUrl);
        }
    }

    /// <summary>
    /// Downloads the installer, verifies it, and hands it over.
    ///
    /// <para>
    /// Deliberately stops short of running it. A program that fetches an executable from the
    /// internet and runs it is one compromised account away from installing an attacker's
    /// software on every machine that has it — so the elevation prompt and the installer's own
    /// "here is what this does" dialog both still happen, with a human in front of them.
    /// </para>
    /// </summary>
    private async void Update_Download(object sender, RoutedEventArgs e)
    {
        if (_update is not { CanDownload: true } release)
        {
            return;
        }

        Tick();
        UpdateDownloadButton.IsEnabled = false;
        var restore = (string)UpdateDownloadButton.Content;

        var progress = new Progress<double>(fraction =>
            UpdateDownloadButton.Content = $"{fraction * 100:0}%");

        try
        {
            var result = await UpdateChecker.DownloadAsync(release, progress, default);

            if (!result.Verified)
            {
                _sounds.Play(UiSound.Alert);
                UpdateDetail.Text = result.Problem ?? "The download could not be verified.";
                UpdateDetail.Foreground = (Brush)FindResource("Crit");
                return;
            }

            _sounds.Play(UiSound.Confirm);

            UpdateDetail.Text =
                $"Downloaded and verified against GitHub's published fingerprint. Running it will "
                + "ask for administrator rights and show you what it changes before doing anything.";

            UpdateFingerprint.Text = "sha256  " + result.ActualSha256;
            UpdateFingerprint.Visibility = Visibility.Visible;

            var run = MessageBox.Show(
                $"{release.InstallerName} was downloaded and its fingerprint matches the one "
                + "GitHub published for it.\n\n"
                + $"sha256\n{result.ActualSha256}\n\n"
                + "Run the installer now? GamerGod will close first — an installer cannot "
                + "replace files that are in use.\n\n"
                + "Nothing on your machine has been changed yet.",
                "GamerGod",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (run != MessageBoxResult.Yes)
            {
                UpdateDownloadButton.Content = "SHOW FILE";
                UpdateDownloadButton.Click -= Update_Download;
                UpdateDownloadButton.Click += (_, _) => OpenExternal(
                    System.IO.Path.GetDirectoryName(result.Path!) ?? result.Path!);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.Path!)
            {
                UseShellExecute = true,
            });

            Close();
        }
        catch (Exception ex)
        {
            _sounds.Play(UiSound.Alert);
            UpdateDetail.Text = ex.Message;
            UpdateDetail.Foreground = (Brush)FindResource("Crit");
        }
        finally
        {
            UpdateDownloadButton.IsEnabled = true;

            if ((string)UpdateDownloadButton.Content is not ("SHOW FILE" or "DOWNLOAD"))
            {
                UpdateDownloadButton.Content = restore;
            }
        }
    }

    // ---------------------------------------------------------------- apps

    /// <summary>
    /// The catalogue, filtered to what is actually on this machine, with real icons and a way
    /// to start each one.
    ///
    /// <para>
    /// Matched against the uninstall registry rather than winget, because winget knows a
    /// package is installed but not where it was put — and a launch button needs a path.
    /// </para>
    /// </summary>
    private void LoadInstalledApps()
    {
        InstalledItems.Items.Clear();
        InstalledEmpty.Visibility = Visibility.Collapsed;

        ImmutableArray<InstalledApp> present;
        try
        {
            present = InstalledApps.Scan();
        }
        catch (Exception ex)
        {
            InstalledCount.Text = string.Empty;
            InstalledEmpty.Text = $"The installed-programs list could not be read: {ex.Message}";
            InstalledEmpty.Visibility = Visibility.Visible;
            return;
        }

        var tiles = new List<AppTile>();

        foreach (var entry in SoftwareCatalogue.All)
        {
            var match = present.FirstOrDefault(a => InstalledMatch.IsSameProgram(entry.Name, a.Name));

            if (match is not null)
            {
                tiles.Add(AppTile.For(entry, match));
            }
        }

        // Launchers first, then emulators; alphabetical within each. A grid whose order changed
        // between visits would be unusable, and registry enumeration order is arbitrary.
        _appTiles =
        [
            .. tiles
                .OrderBy(t => t.Entry.Kind == CatalogueKind.Launcher ? 0 : 1)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
        ];

        RenderInstalledApps();
    }

    private void RenderInstalledApps()
    {
        var query = InstalledSearch?.Text ?? string.Empty;
        var searching = !string.IsNullOrWhiteSpace(query);

        InstalledItems.Items.Clear();
        InstalledEmpty.Visibility = Visibility.Collapsed;

        var matched = _appTiles
            .Select(t => (Tile: t, Score: FuzzySearch.Best(query, t.Name, t.Systems, t.KindLabel)))
            .Where(x => !searching || x.Score is not null)
            .ToList();

        if (searching)
        {
            matched = [.. matched.OrderByDescending(x => x.Score!.Value)];
        }

        foreach (var (tile, _) in matched)
        {
            InstalledItems.Items.Add(tile);
        }

        if (_appTiles.IsEmpty)
        {
            InstalledCount.Text = string.Empty;
            InstalledEmpty.Text =
                "None of the launchers or emulators GamerGod knows about are installed here. "
                + "Get more has the full list — everything installs through the Windows Package "
                + "Manager, and anything installed there appears on this page.";
            InstalledEmpty.Visibility = Visibility.Visible;
            return;
        }

        if (searching)
        {
            InstalledCount.Text = $"{matched.Count} of {_appTiles.Length} match “{query.Trim()}”";

            if (matched.Count == 0)
            {
                InstalledEmpty.Text =
                    $"Nothing matched “{query.Trim()}”, including allowing for misspellings.";
                InstalledEmpty.Visibility = Visibility.Visible;
            }

            return;
        }

        var launchers = _appTiles.Count(t => t.Entry.Kind == CatalogueKind.Launcher);
        var emulators = _appTiles.Length - launchers;

        InstalledCount.Text =
            $"{launchers} launcher{(launchers == 1 ? "" : "s")}, "
            + $"{emulators} emulator{(emulators == 1 ? "" : "s")}";
    }

    private void Installed_Rescan(object sender, RoutedEventArgs e)
    {
        Tick();
        LoadInstalledApps();
    }

    private async void App_Launch(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AppTile tile)
        {
            return;
        }

        if (tile.LaunchTarget is not { } target)
        {
            // The program is installed but no executable could be identified from the registry.
            // Saying that is better than a button that appears to do nothing.
            MessageBox.Show(
                $"{tile.Name} is installed, but GamerGod could not work out which executable to "
                + "start — its installer did not record one. Start it from the Start menu; "
                + "Game Mode can be armed from the Dashboard first.",
                "GamerGod",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Tick();

        // Same order as a game launch, for the same reason: arming after the process has
        // started is the moment the confinement is least useful.
        if (MasterSwitch.IsChecked != true)
        {
            MasterSwitch.IsChecked = true;
            await ApplyAsync(turnOn: true);
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,

                // Several launchers resolve their own data relative to the working directory
                // and misbehave when started from somewhere else.
                WorkingDirectory = System.IO.Path.GetDirectoryName(target) ?? string.Empty,
            });

            _sounds.Play(UiSound.Confirm);
        }
        catch (Exception ex)
        {
            _sounds.Play(UiSound.Alert);
            MessageBox.Show(
                $"Could not start {tile.Name}.\n\n{ex.Message}\n\n"
                + "Game Mode is still on — turn it off here, run 'gamergod off', or reboot.",
                "GamerGod",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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
        _catalogueIcons.Clear();
        SwitchNote.Text = SoftwareCatalogue.SwitchNote;

        var rows = new List<(CatalogueEntry Entry, Border Card, Button Action, Border Chip, TextBlock ChipText)>();

        _catalogueGroups.Clear();

        foreach (var group in SoftwareCatalogue.AllGroups)
        {
            var heading = new TextBlock
            {
                Style = (Style)FindResource("Eyebrow"),
                Text = group.Title.ToUpperInvariant(),
                Margin = new Thickness(0, 18, 0, 8),
            };

            CatalogueItems.Items.Add(heading);

            var members = new List<(CatalogueEntry Entry, Border Card)>();

            foreach (var entry in group.Entries)
            {
                var row = BuildCatalogueRow(entry);
                CatalogueItems.Items.Add(row.Card);
                rows.Add(row);
                members.Add((entry, row.Card));
            }

            // Held so searching can hide a heading along with everything under it — a heading
            // above nothing reads as a section that failed to load.
            _catalogueGroups.Add((heading, members));
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

        ApplyRealIcons();

        var count = rows.Count(r => r.Entry.WingetId is { } id && installed.Contains(id));

        _catalogueSummary = $"{SoftwareCatalogue.All.Length} listed  ·  {count} already installed";
        CatalogueStatus.Text = _catalogueSummary;

        await FetchMissingAppIconsAsync();
    }

    /// <summary>
    /// Fills in logos for software that is not installed, from each project's own site.
    ///
    /// <para>
    /// Only for rows still showing the generated mark. Anything installed already has real
    /// artwork from its own executable, which costs nothing and is always current.
    /// </para>
    /// </summary>
    private async Task FetchMissingAppIconsAsync()
    {
        var pending = SoftwareCatalogue.All
            .Where(e => _catalogueIcons.TryGetValue(e.Id, out var image)
                        && image.Visibility != Visibility.Visible)
            .ToImmutableArray();

        if (pending.IsEmpty)
        {
            return;
        }

        // Anything already downloaded is drawn immediately, with no network involved.
        var remaining = new List<CatalogueEntry>();

        foreach (var entry in pending)
        {
            if (AppIconCache.FindCached(entry.Id) is { } cached)
            {
                ShowIcon(entry.Id, cached);
            }
            else
            {
                remaining.Add(entry);
            }
        }

        if (remaining.Count == 0 || !_settings.FetchCoverArt)
        {
            return;
        }

        var restore = CatalogueStatus.Text;
        var found = 0;

        foreach (var entry in remaining)
        {
            CatalogueStatus.Text = $"finding the logo for {entry.Name}…";

            var path = await AppIconCache.TryFetchAsync(entry.Id, entry.Homepage, default);

            if (path is not null && ShowIcon(entry.Id, path))
            {
                found++;
            }
        }

        CatalogueStatus.Text = found == 0
            ? restore
            : $"{restore}  ·  {found} logo{(found == 1 ? "" : "s")} found";
    }

    private bool ShowIcon(string entryId, string path)
    {
        if (!_catalogueIcons.TryGetValue(entryId, out var image))
        {
            return false;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();

            // Multi-frame .ico files are common here, and WPF picks the frame nearest the
            // requested width — which is the whole reason to state one.
            bitmap.DecodePixelWidth = 64;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();

            image.Source = bitmap;
            image.Visibility = Visibility.Visible;

            return true;
        }
        catch (Exception)
        {
            // An SVG, or a format WPF will not decode. The generated mark stays.
            return false;
        }
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

        var text = new StackPanel();
        text.Children.Add(head);

        // The generated mark, with the real icon over it once one is found. A row that shows
        // nothing until winget answers reads as unfinished, so this is drawn immediately and
        // upgraded in place if the program turns out to be installed.
        var mark = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 13, 0),
            Background = TileArt.Gradient(entry.Name),
        };

        var markIcon = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
        };

        RenderOptions.SetBitmapScalingMode(markIcon, BitmapScalingMode.HighQuality);

        var markGrid = new Grid();
        markGrid.Children.Add(new TextBlock
        {
            Text = TileArt.Initial(entry.Name),
            FontFamily = (FontFamily)FindResource("Display"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        markGrid.Children.Add(markIcon);
        mark.Child = markGrid;

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(mark);
        body.Children.Add(text);

        // Kept so the installed pass can drop a real icon in without rebuilding the row.
        _catalogueIcons[entry.Id] = markIcon;

        text.Children.Add(new TextBlock
        {
            Text = entry.Systems,
            Style = (Style)FindResource("BodyText"),
            FontSize = 12.5,
            Margin = new Thickness(0, 6, 0, 0),
        });

        if (entry.Note is { } note)
        {
            text.Children.Add(new TextBlock
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

    /// <summary>
    /// Swaps the generated mark for the program's own icon on rows whose software is installed.
    ///
    /// <para>
    /// Only installed programs have an icon to read — it lives inside their executable — so a
    /// row that keeps its generated mark is telling you something true rather than failing to
    /// load.
    /// </para>
    /// </summary>
    private void ApplyRealIcons()
    {
        ImmutableArray<InstalledApp> present;
        try
        {
            present = InstalledApps.Scan();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var entry in SoftwareCatalogue.All)
        {
            if (!_catalogueIcons.TryGetValue(entry.Id, out var image))
            {
                continue;
            }

            var app = present.FirstOrDefault(a => InstalledMatch.IsSameProgram(entry.Name, a.Name));

            if (app is null || AppTile.For(entry, app).Icon is not { } icon)
            {
                continue;
            }

            image.Source = icon;
            image.Visibility = Visibility.Visible;
        }
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
        OptUpdates.IsChecked = _settings.CheckForUpdates;

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
            CheckForUpdates = OptUpdates.IsChecked == true,
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
        Show(PageFree, page == "Free");
        Show(PageInstalled, page == "Installed");
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

        // Asks before going online, once per session. Pressing Find free games is how you ask
        // again — the prompt is not repeated every time the tab is opened.
        if (page == "Free" && !_freeLoaded)
        {
            _freeLoaded = true;
            _ = LoadFreeGamesAsync(promptToRefresh: true);
        }

        // Rescanned every visit rather than cached: something installed on the Get more page
        // must appear here without the user having to work out that a refresh exists.
        if (page == "Installed")
        {
            LoadInstalledApps();
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

