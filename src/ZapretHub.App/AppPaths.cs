namespace ZapretHub.App;

/// <summary>
/// The app runs elevated, so everything it executes or trusts lives under %ProgramData%\ZapretHub,
/// which <see cref="SecureStorage"/> locks down to Administrators/SYSTEM. Only the "user" subfolder
/// (hand-edited lists and probe targets — plain data) stays writable for normal users.
/// </summary>
internal static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretHub");

    public static readonly string EngineRoot = Path.Combine(Root, "engine");
    public static readonly string Settings = Path.Combine(Root, "settings.json");
    public static readonly string Log = Path.Combine(Root, "logs", "app.log");

    /// <summary>Deliberately empty ipset (= every address) used only while learning a game; admin-only location.</summary>
    public static readonly string LearningAnyIpset = Path.Combine(Root, "ipset-learning-any.txt");

    /// <summary>Game profile address lists. Admin-only: winws reads them as admin and an emptied file
    /// would mean "every address", so users edit them only through the app (elevated notepad).</summary>
    public static readonly string GamesDir = Path.Combine(Root, "games");

    /// <summary>Normalized copies winws actually reads (see GameRuleCompiler).</summary>
    public static readonly string GamesRuntimeDir = Path.Combine(Root, "games", "runtime");

    /// <summary>Downloaded tg-ws-proxy builds (admin-only folder; the proxy itself runs as the normal user).</summary>
    public static readonly string TgRoot = Path.Combine(Root, "telegram");

    /// <summary>VPS tunnel: sing-box builds, generated config and the encrypted server link.
    /// Locked to Administrators/SYSTEM only (see SecureStorage) — unlike the rest of Root, not readable by users.</summary>
    public static readonly string VpnRoot = Path.Combine(Root, "vpn");
    public static readonly string SingBoxRoot = Path.Combine(VpnRoot, "engine");
    public static readonly string VpnConfig = Path.Combine(VpnRoot, "config.json");
    public static readonly string VpnServerFile = Path.Combine(VpnRoot, "server.bin");
    public static readonly string SingBoxLog = Path.Combine(VpnRoot, "sing-box.log");

    public static readonly string UserData = Path.Combine(Root, "user");
    public static readonly string UserLists = Path.Combine(UserData, "lists");
    public static readonly string Targets = Path.Combine(UserData, "targets.txt");

    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZapretHub");

    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static string WindowsTool(string exe) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe);
}
