using System.Collections.Immutable;

namespace GamerGod.Core.Catalogue;

/// <summary>
/// Everything GamerGod knows how to point you at: stores, library managers, and emulators from
/// the arcade era to hardware still being reverse-engineered.
///
/// <para>
/// Every <c>WingetId</c> here was checked against the live winget source rather than guessed,
/// because a plausible-looking identifier that does not resolve produces an install button that
/// fails, which is worse than no button. Entries winget does not carry keep a null id and open
/// their official page instead — that is a real state, not a gap to be papered over.
/// </para>
///
/// <para>
/// Emulators are legal software. What you run on them is your business and your responsibility,
/// and GamerGod will not help with it: there are no ROM sources here, no BIOS files, and no
/// links to either. Several systems need a BIOS dumped from hardware you own, and the entries
/// that do say so, because an emulator that boots to a black screen looks broken to somebody
/// who was never told why.
/// </para>
/// </summary>
public static class SoftwareCatalogue
{
    /// <summary>
    /// Stated rather than left as a hole somebody notices and wonders about.
    ///
    /// <para>
    /// It is the single most common question this list would otherwise raise, and the honest
    /// answer is short. Pretending the console does not exist would be the evasive version.
    /// </para>
    /// </summary>
    public const string SwitchNote =
        "There is no Switch emulator here. Yuzu and Ryujinx were both discontinued in 2024 "
        + "following legal action, and GamerGod will not point you at anonymous re-uploads of "
        + "software whose distribution is disputed. If a maintained one appears, it gets an "
        + "entry.";

    public static ImmutableArray<CatalogueGroup> Launchers { get; } =
    [
        new()
        {
            Title = "Stores",
            Entries =
            [
                new()
                {
                    Id = "steam", Name = "Steam", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "The big one. Also the best controller support on Windows.",
                    WingetId = "Valve.Steam", Homepage = "https://store.steampowered.com/about/",
                },
                new()
                {
                    Id = "epic", Name = "Epic Games Launcher", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "Fortnite, Unreal titles, and a free game most weeks.",
                    WingetId = "EpicGames.EpicGamesLauncher", Homepage = "https://store.epicgames.com/",
                },
                new()
                {
                    Id = "gog", Name = "GOG GALAXY", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "DRM-free, and strong on older PC games that run nowhere else.",
                    WingetId = "GOG.Galaxy", Homepage = "https://www.gog.com/galaxy",
                },
                new()
                {
                    Id = "ea", Name = "EA app", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "Battlefield, Apex, FC, The Sims, Mass Effect.",
                    WingetId = "ElectronicArts.EADesktop", Homepage = "https://www.ea.com/ea-app",
                },
                new()
                {
                    Id = "ubisoft", Name = "Ubisoft Connect", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "Assassin's Creed, Far Cry, Rainbow Six.",
                    WingetId = "Ubisoft.Connect", Homepage = "https://ubisoftconnect.com/",
                },
                new()
                {
                    Id = "battlenet", Name = "Battle.net", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "Blizzard: Overwatch, Diablo, WoW, StarCraft, and Call of Duty.",
                    WingetId = "Blizzard.BattleNet", Homepage = "https://battle.net/",
                },
                new()
                {
                    Id = "rockstar", Name = "Rockstar Games Launcher", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "GTA and Red Dead.",
                    WingetId = "RockstarGames.Launcher", Homepage = "https://socialclub.rockstargames.com/rockstar-games-launcher",
                },
                new()
                {
                    Id = "amazon", Name = "Amazon Games", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "Free monthly games if you already pay for Prime.",
                    WingetId = "Amazon.Games", Homepage = "https://gaming.amazon.com/",
                },
                new()
                {
                    Id = "itch", Name = "itch", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "Where indie games actually live. Thousands of them free.",
                    WingetId = "ItchIo.Itch", Homepage = "https://itch.io/app",
                },
                new()
                {
                    Id = "roblox", Name = "Roblox", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "Millions of user-made games.",
                    WingetId = "Roblox.Roblox", Homepage = "https://www.roblox.com/",
                },
                new()
                {
                    Id = "xbox", Name = "Xbox / PC Game Pass", Kind = CatalogueKind.Launcher,
                    Group = "Stores", Systems = "Game Pass, Xbox Play Anywhere, cloud streaming.",
                    Homepage = "https://www.xbox.com/apps/xbox-app-for-pc",
                    Note = "Ships with Windows and updates through the Microsoft Store, so winget "
                         + "does not install it. If it is missing, the Store page is the route back.",
                },
            ],
        },
        new()
        {
            Title = "One library for all of them",
            Entries =
            [
                new()
                {
                    Id = "playnite", Name = "Playnite", Kind = CatalogueKind.Launcher,
                    Group = "One library for all of them",
                    Systems = "Every store and every emulator in a single library, with a controller-friendly big-picture mode. Open source.",
                    WingetId = "Playnite.Playnite", Homepage = "https://playnite.link/",
                },
                new()
                {
                    Id = "heroic", Name = "Heroic Games Launcher", Kind = CatalogueKind.Launcher,
                    Group = "One library for all of them",
                    Systems = "Epic, GOG and Amazon without their official clients. Open source.",
                    WingetId = "HeroicGamesLauncher.HeroicGamesLauncher", Homepage = "https://heroicgameslauncher.com/",
                },
                new()
                {
                    Id = "launchbox", Name = "LaunchBox", Kind = CatalogueKind.Launcher,
                    Group = "One library for all of them",
                    Systems = "The best-looking retro front end there is. Big Box mode turns a PC into an arcade cabinet.",
                    Homepage = "https://www.launchbox-app.com/",
                    Note = "Free version is fully usable; the arcade front end is the paid part.",
                },
                new()
                {
                    Id = "vortex", Name = "Vortex", Kind = CatalogueKind.Launcher,
                    Group = "One library for all of them",
                    Systems = "Nexus Mods' mod manager. Not a store — it manages mods for games you already own.",
                    WingetId = "NexusMods.Vortex", Homepage = "https://www.nexusmods.com/about/vortex/",
                },
            ],
        },
    ];

    /// <summary>Ordered oldest hardware first, which is also roughly easiest to hardest to run.</summary>
    public static ImmutableArray<CatalogueGroup> Emulators { get; } =
    [
        new()
        {
            Title = "Start here — many systems at once",
            Entries =
            [
                new()
                {
                    Id = "retroarch", Name = "RetroArch", Kind = CatalogueKind.Emulator,
                    Group = "Start here — many systems at once",
                    Systems = "Dozens of consoles and computers behind one interface, one controller setup, one save system.",
                    WingetId = "Libretro.RetroArch", Homepage = "https://www.retroarch.com/",
                    Note = "Download the cores you want from inside it after installing — it ships nearly empty on purpose.",
                },
                new()
                {
                    Id = "bizhawk", Name = "BizHawk", Kind = CatalogueKind.Emulator,
                    Group = "Start here — many systems at once",
                    Systems = "NES, SNES, N64, Game Boy, GBA, PlayStation, Genesis, Saturn, C64, ZX Spectrum.",
                    WingetId = "TASEmulators.BizHawk", Homepage = "https://tasvideos.org/BizHawk",
                    Note = "Built for frame-perfect tool-assisted runs, which makes it the most precise of the multi-system emulators.",
                },
                new()
                {
                    Id = "ares", Name = "ares", Kind = CatalogueKind.Emulator,
                    Group = "Start here — many systems at once",
                    Systems = "Accuracy above all: NES, SNES, N64, Mega Drive, Saturn, PlayStation, Game Boy, and more.",
                    WingetId = "ares-emulator.ares", Homepage = "https://ares-emu.net/",
                },
            ],
        },
        new()
        {
            Title = "Arcade cabinets and home computers",
            Entries =
            [
                new()
                {
                    Id = "mame", Name = "MAME", Kind = CatalogueKind.Emulator,
                    Group = "Arcade cabinets and home computers",
                    Systems = "The arcade. Thousands of boards, documented as a preservation project rather than a toy.",
                    Homepage = "https://www.mamedev.org/release.html",
                },
                new()
                {
                    Id = "scummvm", Name = "ScummVM", Kind = CatalogueKind.Emulator,
                    Group = "Arcade cabinets and home computers",
                    Systems = "Point-and-click adventures: Monkey Island, Day of the Tentacle, Broken Sword, Simon the Sorcerer.",
                    WingetId = "ScummVM.ScummVM", Homepage = "https://www.scummvm.org/",
                },
                new()
                {
                    Id = "dosbox-staging", Name = "DOSBox Staging", Kind = CatalogueKind.Emulator,
                    Group = "Arcade cabinets and home computers",
                    Systems = "MS-DOS. The actively maintained DOSBox, with better sound and sane defaults.",
                    WingetId = "DOSBoxStaging.DOSBoxStaging", Homepage = "https://dosbox-staging.github.io/",
                },
                new()
                {
                    Id = "dosbox-x", Name = "DOSBox-X", Kind = CatalogueKind.Emulator,
                    Group = "Arcade cabinets and home computers",
                    Systems = "MS-DOS and early Windows, emulating far more period hardware than DOSBox ever did.",
                    WingetId = "joncampbell123.DOSBox-X", Homepage = "https://dosbox-x.com/",
                },
                new()
                {
                    Id = "winuae", Name = "WinUAE", Kind = CatalogueKind.Emulator,
                    Group = "Arcade cabinets and home computers",
                    Systems = "Commodore Amiga, from the A500 to the A4000.",
                    WingetId = "WinUAE.WinUAE", Homepage = "https://www.winuae.net/",
                    Note = "Needs a Kickstart ROM. Legally licensed ones are sold with Cloanto's Amiga Forever.",
                },
            ],
        },
        new()
        {
            Title = "8- and 16-bit consoles",
            Entries =
            [
                new()
                {
                    Id = "mesen2", Name = "Mesen 2", Kind = CatalogueKind.Emulator,
                    Group = "8- and 16-bit consoles",
                    Systems = "NES, SNES, Game Boy, Game Boy Color, PC Engine, Master System. Cycle-accurate.",
                    WingetId = "SourMesen.Mesen2", Homepage = "https://www.mesen.ca/",
                },
                new()
                {
                    Id = "fceux", Name = "FCEUX", Kind = CatalogueKind.Emulator,
                    Group = "8- and 16-bit consoles",
                    Systems = "NES and Famicom, with the debugger romhackers actually use.",
                    WingetId = "FCEUX.FCEUX", Homepage = "https://fceux.com/",
                },
                new()
                {
                    Id = "snes9x", Name = "Snes9x", Kind = CatalogueKind.Emulator,
                    Group = "8- and 16-bit consoles",
                    Systems = "SNES. Thirty years old, still the light and fast option.",
                    Homepage = "https://www.snes9x.com/",
                },
            ],
        },
        new()
        {
            Title = "The 32- and 64-bit generation",
            Entries =
            [
                new()
                {
                    Id = "duckstation", Name = "DuckStation", Kind = CatalogueKind.Emulator,
                    Group = "The 32- and 64-bit generation",
                    Systems = "PlayStation 1. Widescreen, upscaling, and PGXP to fix the wobbling polygons.",
                    WingetId = "Stenzek.DuckStation", Homepage = "https://www.duckstation.org/",
                    Note = "Runs without a BIOS, but a real one dumped from your own console is noticeably more compatible.",
                },
                new()
                {
                    Id = "project64", Name = "Project64", Kind = CatalogueKind.Emulator,
                    Group = "The 32- and 64-bit generation",
                    Systems = "Nintendo 64.",
                    WingetId = "Project64.Project64", Homepage = "https://www.pj64-emu.com/",
                },
                new()
                {
                    Id = "redream", Name = "redream", Kind = CatalogueKind.Emulator,
                    Group = "The 32- and 64-bit generation",
                    Systems = "Dreamcast. The one that just works — no BIOS, no configuration.",
                    WingetId = "redream.redream", Homepage = "https://redream.io/",
                },
                new()
                {
                    Id = "flycast", Name = "Flycast", Kind = CatalogueKind.Emulator,
                    Group = "The 32- and 64-bit generation",
                    Systems = "Dreamcast, NAOMI and Atomiswave arcade hardware.",
                    Homepage = "https://github.com/flyinghead/flycast/releases",
                    Note = "Needs a Dreamcast BIOS for full compatibility.",
                },
            ],
        },
        new()
        {
            Title = "The DVD generation",
            Entries =
            [
                new()
                {
                    Id = "pcsx2", Name = "PCSX2", Kind = CatalogueKind.Emulator,
                    Group = "The DVD generation",
                    Systems = "PlayStation 2. Twenty years of work, and most of the library runs.",
                    WingetId = "PCSX2Team.PCSX2", Homepage = "https://pcsx2.net/",
                    Note = "Requires a BIOS dumped from a PS2 you own. It will not boot anything without one.",
                },
                new()
                {
                    Id = "dolphin", Name = "Dolphin", Kind = CatalogueKind.Emulator,
                    Group = "The DVD generation",
                    Systems = "GameCube and Wii, at resolutions the hardware never managed.",
                    WingetId = "DolphinEmulator.Dolphin", Homepage = "https://dolphin-emu.org/",
                },
                new()
                {
                    Id = "xemu", Name = "xemu", Kind = CatalogueKind.Emulator,
                    Group = "The DVD generation",
                    Systems = "The original Xbox. Halo, Ninja Gaiden Black, Jet Set Radio Future.",
                    WingetId = "xemu-project.xemu", Homepage = "https://xemu.app/",
                    Note = "Needs a BIOS and a hard disk image from your own console — the fiddliest setup on this page.",
                },
            ],
        },
        new()
        {
            Title = "The HD generation",
            Entries =
            [
                new()
                {
                    Id = "rpcs3", Name = "RPCS3", Kind = CatalogueKind.Emulator,
                    Group = "The HD generation",
                    Systems = "PlayStation 3. Demon's Souls, the Metal Gear collection, Persona 5.",
                    Homepage = "https://rpcs3.net/download",
                    Note = "Needs the official PS3 firmware, which Sony publishes free.",
                },
                new()
                {
                    Id = "xenia", Name = "Xenia", Kind = CatalogueKind.Emulator,
                    Group = "The HD generation",
                    Systems = "Xbox 360. Compatibility varies wildly by title.",
                    WingetId = "xenia-project.xenia", Homepage = "https://xenia.jp/",
                },
                new()
                {
                    Id = "cemu", Name = "Cemu", Kind = CatalogueKind.Emulator,
                    Group = "The HD generation",
                    Systems = "Wii U. Breath of the Wild runs better here than it did on the console.",
                    WingetId = "Cemu.Cemu", Homepage = "https://cemu.info/",
                },
                new()
                {
                    Id = "shadps4", Name = "shadPS4", Kind = CatalogueKind.Emulator,
                    Group = "The HD generation",
                    Systems = "PlayStation 4. The newest serious emulation project there is.",
                    WingetId = "shadps4-emu.shadPS4", Homepage = "https://shadps4.net/",
                    Note = "Early. A growing number of titles boot and some are playable; most are not yet.",
                },
            ],
        },
        new()
        {
            Title = "Handhelds",
            Entries =
            [
                new()
                {
                    Id = "mgba", Name = "mGBA", Kind = CatalogueKind.Emulator,
                    Group = "Handhelds",
                    Systems = "Game Boy, Game Boy Color and Game Boy Advance. Accurate and tiny.",
                    WingetId = "JeffreyPfau.mGBA", Homepage = "https://mgba.io/",
                },
                new()
                {
                    Id = "melonds", Name = "melonDS", Kind = CatalogueKind.Emulator,
                    Group = "Handhelds",
                    Systems = "Nintendo DS and DSi, including local wireless between two instances.",
                    WingetId = "melonDS.melonDS", Homepage = "https://melonds.kuribo64.net/",
                },
                new()
                {
                    Id = "azahar", Name = "Azahar", Kind = CatalogueKind.Emulator,
                    Group = "Handhelds",
                    Systems = "Nintendo 3DS. The maintained continuation of Citra and Lime3DS.",
                    WingetId = "AzaharEmu.Azahar", Homepage = "https://azahar-emu.org/",
                },
                new()
                {
                    Id = "ppsspp", Name = "PPSSPP", Kind = CatalogueKind.Emulator,
                    Group = "Handhelds",
                    Systems = "PSP, upscaled. One of the most complete emulators of any system.",
                    WingetId = "PPSSPPTeam.PPSSPP", Homepage = "https://www.ppsspp.org/",
                },
                new()
                {
                    Id = "vita3k", Name = "Vita3K", Kind = CatalogueKind.Emulator,
                    Group = "Handhelds",
                    Systems = "PlayStation Vita.",
                    WingetId = "Vita3K.Vita3K", Homepage = "https://vita3k.org/",
                    Note = "Experimental. Plenty runs, plenty does not.",
                },
            ],
        },
        new()
        {
            Title = "Android on your PC",
            Entries =
            [
                new()
                {
                    Id = "google-play-games", Name = "Google Play Games on PC", Kind = CatalogueKind.Emulator,
                    Group = "Android on your PC",
                    Systems = "Google's own Android runtime. Mouse and keyboard support, and progress syncs with your phone.",
                    WingetId = "Google.PlayGames", Homepage = "https://play.google.com/googleplaygames",
                    Note = "Needs hardware virtualisation switched on in your firmware. The catalogue is curated, so not every Android game is in it.",
                },
                new()
                {
                    Id = "bluestacks", Name = "BlueStacks", Kind = CatalogueKind.Emulator,
                    Group = "Android on your PC",
                    Systems = "The widest Android compatibility on Windows — effectively the whole Play Store.",
                    WingetId = "BlueStack.BlueStacks", Homepage = "https://www.bluestacks.com/",
                    Note = "Installs its own hypervisor, which can conflict with Hyper-V and with the memory integrity setting some anti-cheats require. GamerGod will not turn either of those off for it.",
                },
            ],
        },
    ];

    public static ImmutableArray<CatalogueEntry> All { get; } =
    [
        .. Launchers.SelectMany(g => g.Entries),
        .. Emulators.SelectMany(g => g.Entries),
    ];
}
