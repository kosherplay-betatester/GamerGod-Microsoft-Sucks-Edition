using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GamerGod.Core.Diagnostics;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Library;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using GamerGod.Ui.Audio;
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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
            LibraryItems.Items.Add(BuildGameCard(game));
        }
    }

    private Border BuildGameCard(GameEntry game)
    {
        var isEmulator = game.Source == GameSource.Emulator;
        var accent = (Brush)FindResource(isEmulator ? "Trace" : "Signal");

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Chip(game.SourceLabel.ToUpperInvariant(), accent, filled: false));
        head.Children.Add(new TextBlock
        {
            Text = game.Name,
            Style = (Style)FindResource("H2"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var body = new StackPanel();
        body.Children.Add(head);

        if (!game.Systems.IsDefaultOrEmpty)
        {
            body.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", game.Systems),
                Style = (Style)FindResource("Mono"),
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        // Emulators carry no anti-cheat, so every lever is available to them. Saying so is
        // useful: it is the one place GamerGod can do its most aggressive work safely.
        body.Children.Add(new TextBlock
        {
            Text = isEmulator
                ? "No anti-cheat, so GamerGod can use every lever on this."
                : "Game Mode turns on, then your store launches it.",
            Style = (Style)FindResource("BodyText"),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var play = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "PLAY",
            Padding = new Thickness(18, 8, 18, 8),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = game,
        };
        play.Click += Play_Click;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        body.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(body, 0);
        Grid.SetColumn(play, 1);
        layout.Children.Add(body);
        layout.Children.Add(play);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(15),
            Child = layout,
        };
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not GameEntry game)
        {
            return;
        }

        // Arm first, then launch. The other order would start the game onto a machine that is
        // still busy, which is the moment the confinement is most worth having.
        if (MasterSwitch.IsChecked != true)
        {
            MasterSwitch.IsChecked = true;
            await ApplyAsync(turnOn: true);
        }

        try
        {
            // UseShellExecute so a steam:// or com.epicgames.launcher:// URI reaches its handler.
            // Launching a store title's executable directly is how third-party launchers break
            // cloud saves and anti-cheat bootstrapping.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(game.LaunchTarget)
            {
                UseShellExecute = true,
            });

            _sounds.Play(UiSound.Confirm);
        }
        catch (Exception ex)
        {
            _sounds.Play(UiSound.Alert);
            MessageBox.Show(
                $"Could not launch {game.Name}.\n\n{ex.Message}\n\n"
                + "Game Mode is still on — 'gamergod off' or rebooting undoes it.",
                "GamerGod",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
        };

        _settings.Save();
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

        PageDashboard.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        PageLibrary.Visibility = page == "Library" ? Visibility.Visible : Visibility.Collapsed;
        PageMachine.Visibility = page == "Machine" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        Tick();

        // Scanned on first open rather than at startup: reading several stores' manifests
        // should not delay the window appearing.
        if (page == "Library" && LibraryItems.Items.Count == 0 && !_libraryLoaded)
        {
            _libraryLoaded = true;
            _ = LoadLibraryAsync();
        }
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
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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
