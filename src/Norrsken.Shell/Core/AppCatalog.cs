namespace Norrsken.Shell.Core;

/// <summary>
/// Whether an installer should run elevated: Machine apps go through the single elevated helper,
/// User apps run unelevated (some, like Spotify, refuse to install as administrator).
/// winget still picks each package's own installer type.
/// </summary>
public enum InstallScope { Machine, User }

/// <summary>An app Norrsken can install on first boot. Ids are winget package ids (all verified).</summary>
public sealed record CatalogApp(string Id, string Name, string Category, InstallScope Scope, bool Default = false)
{
    public string Monogram => Name.Length == 0 ? "?" : char.ToUpperInvariant(Name[0]).ToString();
}

/// <summary>
/// The first-boot catalogue. Defaults mirror what's installed on the owner's current PC (taken
/// from `winget list`), so a fresh laptop comes up with the same toolkit; everything else is opt-in.
/// </summary>
public static class AppCatalog
{
    const InstallScope M = InstallScope.Machine, U = InstallScope.User;
    public const string BootMenuId = "norrsken.bootmenu";
    public const string FlickerboxId = "norrsken.flickerbox";

    /// <summary>
    /// Always installed: everything games and ordinary .exe programs expect to find on a PC —
    /// every Visual C++ runtime since 2005, legacy DirectX, .NET, GameInput — plus terminal tools.
    /// </summary>
    public static readonly IReadOnlyList<CatalogApp> Essentials =
    [
        new(BootMenuId, "Norrsken system setup", "Essentials", M), // not a winget package; handled by AppInstaller
        new(FlickerboxId, "Flickerbox (photos, video, music)", "Essentials", M), // ships inside Norrsken; replaces Photos and Media Player
        new("Microsoft.VCRedist.2015+.x64", "Visual C++ 2015–2022 (64-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2015+.x86", "Visual C++ 2015–2022 (32-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2013.x64", "Visual C++ 2013 (64-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2013.x86", "Visual C++ 2013 (32-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2012.x64", "Visual C++ 2012 (64-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2012.x86", "Visual C++ 2012 (32-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2010.x64", "Visual C++ 2010 (64-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2010.x86", "Visual C++ 2010 (32-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2008.x64", "Visual C++ 2008 (64-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2008.x86", "Visual C++ 2008 (32-bit)", "Essentials", M),
        new("Microsoft.VCRedist.2005.x86", "Visual C++ 2005 (32-bit)", "Essentials", M),
        new("Microsoft.DirectX", "DirectX Runtime", "Essentials", M),
        new("Microsoft.GameInput", "Microsoft GameInput", "Essentials", M),
        new("Microsoft.DotNet.DesktopRuntime.6", ".NET Desktop Runtime 6", "Essentials", M),
        new("Microsoft.DotNet.DesktopRuntime.8", ".NET Desktop Runtime 8", "Essentials", M),
        new("Microsoft.DotNet.DesktopRuntime.10", ".NET Desktop Runtime 10", "Essentials", M),
        new("Microsoft.PowerShell", "PowerShell 7", "Essentials", M),
        new("Microsoft.WindowsTerminal", "Windows Terminal", "Essentials", M),
        new("7zip.7zip", "7-Zip", "Essentials", M),
    ];

    public static readonly IReadOnlyList<CatalogApp> Optional =
    [
        // Gaming
        new("Valve.Steam", "Steam", "Gaming", M, Default: true),
        new("EpicGames.EpicGamesLauncher", "Epic Games", "Gaming", M, Default: true),
        new("Discord.Discord", "Discord", "Gaming", U, Default: true),
        new("Blizzard.BattleNet", "Battle.net", "Gaming", M, Default: true),
        new("Ubisoft.Connect", "Ubisoft Connect", "Gaming", M, Default: true),
        new("RockstarGames.Launcher", "Rockstar Games", "Gaming", M, Default: true),
        new("Roblox.Roblox", "Roblox", "Gaming", U, Default: true),
        new("Mojang.MinecraftLauncher", "Minecraft Launcher", "Gaming", M, Default: true),
        new("Modrinth.ModrinthApp", "Modrinth", "Gaming", U, Default: true),
        new("Overwolf.CurseForge", "CurseForge", "Gaming", U, Default: true),
        new("Moonsworth.LunarClient", "Lunar Client", "Gaming", U, Default: true),
        new("MedalB.V.Medal", "Medal", "Gaming", U, Default: true),
        new("ElectronicArts.EADesktop", "EA app", "Gaming", M),
        new("GOG.Galaxy", "GOG Galaxy", "Gaming", M),
        new("PrismLauncher.PrismLauncher", "Prism Launcher", "Gaming", U),
        new("HeroicGamesLauncher.HeroicGamesLauncher", "Heroic", "Gaming", U),
        new("Nvidia.PhysX", "NVIDIA PhysX", "Gaming", M),

        // Browsers
        new("Google.Chrome", "Chrome", "Browsers", M, Default: true),
        new("Opera.Opera", "Opera", "Browsers", U, Default: true),
        new("Mozilla.Firefox", "Firefox", "Browsers", M),
        new("Brave.Brave", "Brave", "Browsers", M),

        // AI
        new("Anthropic.Claude", "Claude", "AI", U, Default: true),
        new("Anthropic.ClaudeCode", "Claude Code", "AI", U, Default: true),
        new("OpenAI.ChatGPT", "ChatGPT", "AI", U, Default: true),
        new("Google.GeminiDesktop", "Gemini", "AI", U, Default: true),
        new("Ollama.Ollama", "Ollama", "AI", U, Default: true),

        // Development
        new("Microsoft.VisualStudioCode", "VS Code", "Development", U, Default: true),
        new("Anysphere.Cursor", "Cursor", "Development", U, Default: true),
        new("Google.AntigravityIDE", "Antigravity", "Development", U, Default: true),
        new("Microsoft.VisualStudio.Community", "Visual Studio Community", "Development", M, Default: true),
        new("Git.Git", "Git", "Development", M, Default: true),
        new("OpenJS.NodeJS.LTS", "Node.js LTS", "Development", M, Default: true),
        new("Python.Python.3.14", "Python 3.14", "Development", U, Default: true),
        new("Python.Python.3.11", "Python 3.11", "Development", U, Default: true),
        new("GoLang.Go", "Go", "Development", M, Default: true),
        new("Kitware.CMake", "CMake", "Development", M, Default: true),
        new("MSYS2.MSYS2", "MSYS2", "Development", M, Default: true),
        new("EclipseAdoptium.Temurin.21.JDK", "Java 21 (Temurin)", "Development", M, Default: true),
        new("EclipseAdoptium.Temurin.25.JDK", "Java 25 (Temurin)", "Development", M, Default: true),
        new("Oracle.JavaRuntimeEnvironment", "Java 8 Runtime", "Development", M, Default: true),
        new("Unity.UnityHub", "Unity Hub", "Development", M, Default: true),
        new("BlenderFoundation.Blender", "Blender", "Development", M, Default: true),
        new("Warp.Warp", "Warp", "Development", U, Default: true),
        new("AutoHotkey.AutoHotkey", "AutoHotkey", "Development", M, Default: true),
        new("Oracle.VirtualBox", "VirtualBox", "Development", M, Default: true),
        new("Notepad++.Notepad++", "Notepad++", "Development", M),
        new("Microsoft.WSL", "WSL", "Development", M), // needs a restart to finish

        // Media & creative
        new("Spotify.Spotify", "Spotify", "Media & creative", U, Default: true), // refuses to install as admin
        new("VideoLAN.VLC", "VLC", "Media & creative", M, Default: true),
        new("OBSProject.OBSStudio", "OBS Studio", "Media & creative", M, Default: true),
        new("ByteDance.CapCut", "CapCut", "Media & creative", U, Default: true),
        new("Gyan.FFmpeg", "FFmpeg", "Media & creative", M, Default: true),

        // Productivity & chat
        new("Telegram.TelegramDesktop", "Telegram", "Productivity & chat", U, Default: true),
        new("Microsoft.Teams", "Teams", "Productivity & chat", U, Default: true),
        new("TheDocumentFoundation.LibreOffice", "LibreOffice", "Productivity & chat", M),
        new("Zoom.Zoom", "Zoom", "Productivity & chat", U),

        // Utilities
        new("RARLab.WinRAR", "WinRAR", "Utilities", M, Default: true),
        new("AppWork.JDownloader", "JDownloader", "Utilities", M, Default: true),
        new("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "Utilities", M, Default: true),
        new("Malwarebytes.Malwarebytes", "Malwarebytes", "Utilities", M, Default: true),
        new("Cloudflare.Warp", "Cloudflare WARP", "Utilities", M, Default: true),
        new("Microsoft.PowerToys", "PowerToys", "Utilities", M, Default: true),
        new("voidtools.Everything", "Everything", "Utilities", M),
        new("Litecoin.Core", "Litecoin Core", "Utilities", M),

        // Hardware software: only useful with that brand's devices, so opt-in on a new laptop.
        new("Logitech.GHUB", "Logitech G HUB", "Hardware", M),
        new("Corsair.iCUE.5", "Corsair iCUE", "Hardware", M),
        new("SteelSeries.GG", "SteelSeries GG", "Hardware", M),
        new("WhirlwindFX.SignalRgb", "SignalRGB", "Hardware", M),
        new("FocusriteAudioEngineeringLtd.FocusriteControl2", "Focusrite Control 2", "Hardware", M),
    ];

    public static IEnumerable<IGrouping<string, CatalogApp>> ByCategory => Optional.GroupBy(a => a.Category);

    public static CatalogApp? Find(string id) =>
        Essentials.Concat(Optional).FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
