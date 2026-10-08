namespace Clockwork.Overlay;

/// <summary>
/// Common Windows applications for which a performance overlay is not useful.
/// Matching is intentionally by executable name so installs and updates do not
/// change the built-in blacklist. This list is fixed in the application and is
/// not user-editable.
/// </summary>
internal static class IgnoredApplications
{
    public static readonly HashSet<string> ExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows shell / system UI
        "explorer", "ApplicationFrameHost", "SearchHost", "SearchApp", "ShellExperienceHost",
        "StartMenuExperienceHost", "LockApp", "TextInputHost", "SystemSettings", "SystemSettingsBroker",
        "RuntimeBroker", "backgroundTaskHost", "WWAHost", "smartscreen", "sihost", "taskhostw",
        "Taskmgr", "mmc", "control", "rundll32", "regedit", "msconfig", "msinfo32", "dxdiag",
        "perfmon", "resmon", "eventvwr", "compmgmt", "services", "taskschd", "devmgmt", "diskmgmt",
        "cleanmgr", "winver", "charmap", "osk", "Magnify", "Narrator", "mobsync", "optionalfeatures",
        "lusrmgr", "gpedit", "secpol", "wf", "wusa", "sethc", "utilman", "spoolsv", "PrintIsolationHost",
        "DisplaySwitch", "dccw", "ctfmon", "fontview", "ComputerDefaults", "CredUIHost",
        "UserOOBEBroker", "oobe", "msdt", "HelpPane", "FirstLogonAnim", "WinStore.App", "WinStore.App.exe",

        // AI / Desktop Assistants / LLM wrappers
        "Gemini", "ChatGPT", "Claude", "Copilot", "Perplexity", "DeepSeek", "Poe", "LM Studio",
        "Jan", "Ollama", "Chatbox", "TypingMind", "msty", "AnythingLLM", "LocalAI",

        // File managers / archive tools / viewers
        "7zFM", "WinRAR", "UnRAR", "peazip", "Bandizip", "Everything", "EverythingToolbar",
        "Q-Dir", "DirectoryOpus", "xyplorer", "freecommander", "totalcmd", "tc", "Files",
        "OneCommander", "explorerpp", "MultiCommander", "doublecmd", "Clover", "OpenShell",
        "irfanview", "i_view32", "xnview", "xnviewmp", "nomacs", "ImageGlass", "Honeyview",
        "FastStoneImageViewer", "ACDSee", "qimgv", "JPEGView", "Imagine", "gwenview",
        "sumatrapdf", "AcroRd32", "Acrobat", "FoxitReader", "FoxitPDFReader", "PDFXEdit",

        // Web browsers / browser shells
        "chrome", "chrome_proxy", "GoogleCrashHandler", "firefox", "firefoxdeveloperedition", "librewolf",
        "waterfox", "waterfoxclassic", "brave", "brave_crash_handler", "msedge", "msedgewebview2",
        "opera", "opera_gx", "opera_autoupdate", "vivaldi", "vivaldi_crash_handler", "yandex",
        "yandexbrowser", "palemoon", "floorp", "thorium", "ungoogled-chromium", "chromium",
        "arc", "zen", "sidekick", "maxthon", "avastbrowser", "epicwebbrowser", "comodo_dragon",
        "iridium", "centbrowser", "srware_iron", "duckduckgo", "startpageshell",

        // Messaging / collaboration / social apps
        "Discord", "DiscordPTB", "DiscordCanary", "slack", "Teams", "ms-teams", "MSTeams",
        "Skype", "SkypeApp", "Zoom", "Webex", "CiscoCollabHost", "WhatsApp", "WhatsAppBeta",
        "Telegram", "Signal", "Element", "ElementCall", "Viber", "LINE", "LineLauncher",
        "WeChat", "WeChatApp", "ICQ", "thunderbird", "Mailbird", "eMClient", "Outlook",
        "Mattermost", "Rocket.Chat", "Ferdium", "Rambox", "Franz", "Station", "Wavebox",
        "FacebookMessenger", "Instagram", "Messenger", "Reddit", "X", "Twitter", "Threads",

        // Media players / music / streaming clients
        "vlc", "vlc-cache-gen", "mpv", "mpvnet", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64",
        "PotPlayerMini", "PotPlayerMini64", "kodi", "KodiSetup", "SMPlayer",
        "foobar2000", "MusicBee", "AIMP", "Winamp", "Audacious", "Clementine", "DeaDBeeF",
        "Spotify", "SpotifyWebHelper", "TIDAL", "Deezer", "Pandora", "AmazonMusic", "AppleMusic",
        "YouTubeMusic", "MediaMonkey", "JRiver", "plex", "Plexamp", "Jellyfin", "Emby",
        "Netflix", "PrimeVideo", "DisneyPlus", "Max", "Hulu", "Peacock", "ParamountPlus",

        // Game launchers / storefronts / publisher clients (not games themselves)
        "steam", "steamwebhelper", "SteamService", "EpicGamesLauncher", "EpicWebHelper",
        "Battle.net", "Agent", "BlizzardUpdateAgent", "UbisoftConnect", "upc", "Uplay",
        "EA", "EADesktop", "EALauncher", "Origin", "OriginWebHelperService", "GalaxyClient",
        "GOGGalaxy", "GogGalaxyCommunication", "RockstarGamesLauncher", "RockstarService",
        "LauncherPatcher", "BethesdaNetLauncher", "2KLauncher", "Paradox Launcher",
        "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "LeagueClient", "LeagueClientUx",
        "MinecraftLauncher", "MinecraftLauncherBeta", "PrismLauncher", "LunarClient", "BadlionClient",
        "ModrinthApp", "CurseForge", "Overwolf", "Playnite", "Amazon Games", "AmazonGames",
        "itch", "itchio", "NVIDIA Share", "GeForceExperience", "NVIDIA App",
        "NVContainer", "NVCPLUI", "nvsphelper64", "RadeonSoftware", "AMDRSServ",
        "amdow", "IGCC", "IntelGraphicsSoftware", "IntelGraphicsCommandCenter",

        // Recording / streaming / production tools
        "obs64", "obs32", "Streamlabs", "slobs", "XSplit", "XSplitBroadcaster", "XSplitCore",
        "LightstreamStudio", "vMix", "Wirecast", "PRISMLiveStudio", "NVIDIA Broadcast", "NVBroadcastContainer",
        "ShareX", "Greenshot", "SnippingTool", "ScreenClippingHost", "Snipaste", "Flameshot",
        "Bandicam", "bdcam", "Fraps", "GameRecorder", "Action", "MirillisAction", "Dxtory",
        "ReLive", "RadeonSoftwareSlimmer", "InstantReplay",

        // Office / productivity / document applications
        "WINWORD", "EXCEL", "POWERPNT", "ONENOTE", "ONENOTEM", "MSPUB", "MSACCESS", "VISIO",
        "WINPROJ", "soffice", "soffice.bin", "swriter", "scalc", "simpress", "sdraw", "sbase",
        "smath", "OpenOffice", "LibreOffice", "notepad", "Notepad3", "Notepad++", "wordpad",
        "write", "mspaint", "calc", "sticky notes", "StikyNot", "TeamsClassic",
        "Notion", "Evernote", "Obsidian", "Logseq", "Joplin", "StandardNotes",
        "Todoist", "ticktick", "Trello", "Asana", "ClickUp", "Monday", "Linear",
        "FoxitPDFEditor", "drawboard", "Kindle", "Calibre", "Zotero", "Mendeley",

        // Developer tools / terminals / IDEs
        "devenv", "VSLauncher", "ServiceHub.Host.netfx.x64", "ServiceHub.RoslynCodeAnalysisService",
        "code", "Code - Insiders", "cursor", "Windsurf", "sublime_text",
        "sublime_text_4", "rider64", "rider", "idea64", "clion64", "webstorm64",
        "pycharm64", "pycharm", "goland64", "goland", "phpstorm64", "rubymine64", "datagrip64",
        "appcode", "resharper", "devenv.exe", "eclipse", "eclipse-workspace", "qtcreator", "qcreator",
        "android-studio", "studio64", "studio", "xamarin", "UnityHub", "Godot",
        "Godot_v4", "GameMakerStudio", "GameMaker", "Blender", "maya", "houdini", "Cinema4D",
        "3dsmax", "SketchUp", "Fusion360", "FreeCAD", "FreeCADCmd", "solidworks",
        "terminal", "wt", "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell",
        "pwsh", "powershell_ise", "bash", "zsh", "fish", "mintty", "ConEmu", "ConEmu64",
        "cmder", "alacritty", "wezterm", "kitty", "WindowsPowerShell", "ubuntu", "ubuntu2204",
        "ubuntu2404", "debian", "kali", "wsl", "wslhost", "wslservice", "docker", "Docker Desktop",
        "com.docker.backend", "com.docker.proxy", "podman", "Podman Desktop", "rancher-desktop",
        "kubernetes-dashboard", "minikube", "kind", "vagrant", "vagrant-vmware-utility",

        // Remote desktop / remote access / VM clients
        "mstsc", "msrdc", "RemoteDesktop", "TeamViewer", "tv_w32", "tv_x64", "AnyDesk",
        "AnyDeskMSI", "RustDesk", "parsec", "ParsecService", "Moonlight",
        "Sunshine", "Splashtop", "SplashtopSOS", "dwagent", "ZohoMeeting",
        "ChromeRemoteDesktopHost", "remotedesktophost", "VirtualBox", "VirtualBoxVM", "VBoxSVC",
        "VBoxHeadless", "vmware", "vmware-vmx", "vmware-remotemks", "vmware-authd", "vmware-usbarbitrator",
        "vmplayer", "vmwaretray", "qemu-system-x86_64", "qemu-system-aarch64", "Hyper-V",
        "vmconnect", "VirtualMachineConnection", "Parallels Client", "Parallels",

        // Hardware monitoring / tuning utilities (Clockwork overlay is not useful over these)
        "HWiNFO64", "HWiNFO32", "HWiNFO", "HWMonitor", "OpenHardwareMonitor", "LibreHardwareMonitor",
        "FanControl", "FanControlService", "RTSS", "RTSSHooksLoader64", "RivaTunerStatisticsServer",
        "MSIAfterburner", "MSIAfterburnerStartupTask", "GPU-Z", "GPU-Z.2", "CPU-Z", "cpuz",
        "CoreTemp", "CoreTemp64", "ThrottleStop", "IntelXTU", "XTUService", "RyzenMaster",
        "NZXT CAM", "NZXTCAM", "Corsair.Service", "iCUE", "iCUEService", "ArmouryCrate",
        "ArmouryCrate.UserSessionHelper", "Aac3572MbHal", "GHelper", "LenovoVantage", "DragonCenter",
        "MSI Center", "MSI.CentralServer", "GigabyteControlCenter", "GCCService", "ASUS",
        "AlienwareCommandCenter", "OMEN Gaming Hub", "OMENCommandCenter", "AcerSense", "PredatorSense",

        // Cloud storage / sync clients
        "OneDrive", "Dropbox", "GoogleDriveFS", "drivefs", "iCloudDrive", "iCloudDriveFS",
        "Box", "BoxDrive", "MegaClient", "MEGAsync", "pCloud", "Sync", "SyncTrayzor", "Syncthing",
        "Nextcloud", "ownCloud", "AmazonDrive", "Tresorit",

        // Creative / design / audio production applications
        "Photoshop", "PhotoshopElements", "Illustrator", "InDesign", "AfterFX", "Premiere Pro",
        "Premiere", "MediaEncoder", "Animate", "Audition", "Lightroom", "LightroomClassic",
        "AcrobatDistiller", "CorelDRW", "CorelDRAW", "PaintToolSAI", "SAI", "krita", "GIMP",
        "Inkscape", "Affinity", "AffinityPhoto", "AffinityDesigner", "AffinityPublisher", "DaVinciResolve",
        "Resolve", "Ableton Live", "AbletonLive", "FLStudio", "FL Studio", "Studio One", "REAPER", "Cakewalk", "Cubase", "ProTools", "nuendo", "reason", "BitwigStudio", "LMMS",

        // Security / endpoint / admin applications
        "msmpeng", "SecurityHealthSystray", "SecurityHealthService", "WindowsDefender",
        "ESET", "egui", "avgui", "avastui", "Kaspersky", "KasperskyUI", "mbam",
        "Malwarebytes", "Sophos", "SophosUI", "NortonSecurity", "Norton", "Bitdefender",
        "BitdefenderAgent", "McUICnt", "McAfee", "TrendMicro", "Webroot", "CrowdStrike",
        "CSFalconService", "ProcessHacker", "procexp", "procexp64", "procmon",
        "autoruns", "Autoruns64", "tcpview", "rammap", "vmmap", "diskmon", "filemon", "regmon",

        // Update / installer / maintenance shells
        "wuauclt", "UsoClient", "MoUsoCoreWorker", "TiWorker", "TrustedInstaller", "WindowsUpdateBox",
        "setup", "setuphost", "install", "installer", "unins000", "updater", "update",
        "AdobeARMservice", "GoogleUpdater", "GoogleUpdate", "MicrosoftEdgeUpdate", "OneDriveSetup",
        "DropboxUpdate", "SpotifyUpdate", "SteamUpdate", "EpicGamesLauncherUpdate",

        // OCR / screen / accessibility / utility front ends
        "PowerToys", "PowerToysSettings", "PowerToysRun", "FancyZones", "FlowLauncher", "Wox",
        "Everything64", "Everything32", "Listary", "Ditto", "AutoHotkey", "AutoHotkeyU64",
        "DisplayFusion", "ActualMultipleMonitors", "f.lux", "Flux", "TwinkleTray", "Lively",
        "WallpaperEngine", "wallpaper32", "Rainmeter", "TranslucentTB", "EarTrumpet", "StartAllBack",
        "Start11", "Open-Shell", "TaskbarX", "ExplorerPatcher",
    };
}