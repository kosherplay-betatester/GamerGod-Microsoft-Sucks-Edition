using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamerGod.Ui.Settings;

/// <summary>
/// Everything the user can change, and nothing they cannot.
///
/// <para>
/// Note what is absent: there is no setting that weakens a safety list, no override for the
/// anti-cheat policy, and no way to force contact with a protected title. Those are not
/// oversights. A toggle that let somebody bypass the thermal denylist would, in practice, be
/// the toggle that cooked a laptop, and a "force" flag on the integrity policy would be the
/// one that got an account flagged. Options exist for preferences, not for guarantees.
/// </para>
///
/// <para>
/// Stored per user, so it needs no elevation to change and cannot be used to influence what
/// the privileged service does.
/// </para>
/// </summary>
public sealed record UiSettings
{
    // ---- Levers -------------------------------------------------------

    /// <summary>Move background work off the game's cores. The headline lever.</summary>
    public bool ConfineToAmbientDomain { get; init; } = true;

    /// <summary>Set background processes to Windows' efficiency mode.</summary>
    public bool DemoteToEfficiencyMode { get; init; } = true;

    /// <summary>
    /// Stop the search indexer and update stack for the session. Off by default: its measured
    /// effect on a healthy machine is close to zero, so it is opt-in rather than inherited.
    /// </summary>
    public bool SuppressBackgroundServices { get; init; }

    /// <summary>Activate a duplicated high-performance power scheme. Off by default; small effect.</summary>
    public bool ManagePowerScheme { get; init; }

    // ---- Audio --------------------------------------------------------

    public bool SoundEnabled { get; init; } = true;

    /// <summary>0 to 100.</summary>
    public int SoundVolume { get; init; } = 45;

    /// <summary>Play the near-silent tick on navigation and hover. Some people hate this.</summary>
    public bool SoundOnNavigation { get; init; } = true;

    // ---- Behaviour ----------------------------------------------------

    /// <summary>Show what would change and wait for confirmation before applying anything.</summary>
    public bool ConfirmBeforeApplying { get; init; }

    /// <summary>Close to the notification area rather than exiting.</summary>
    public bool MinimiseToTray { get; init; } = true;

    /// <summary>Turn Game Mode on as soon as GamerGod starts.</summary>
    public bool ArmOnLaunch { get; init; }

    // ---- Overlay ------------------------------------------------------

    /// <summary>Off by default, per the design. It is opt-in and instant either way.</summary>
    public bool OverlayEnabled { get; init; }

    public OverlayDetail OverlayDetail { get; init; } = OverlayDetail.Minimal;

    // ---- Persistence --------------------------------------------------

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GamerGod",
        "settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new UiSettings();
            }

            return JsonSerializer.Deserialize(File.ReadAllText(Path), SettingsJson.Default.UiSettings)
                   ?? new UiSettings();
        }
        catch (Exception)
        {
            // A corrupt or hand-edited settings file must never stop the application from
            // starting. Defaults are always safe, because every default is the conservative
            // choice.
            return new UiSettings();
        }
    }

    public void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(Path, JsonSerializer.Serialize(this, SettingsJson.Default.UiSettings));
        }
        catch (Exception)
        {
            // Losing a preference is a nuisance. Crashing over one is not acceptable.
        }
    }
}

public enum OverlayDetail
{
    Minimal,
    Full,
    Graph,
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(UiSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
