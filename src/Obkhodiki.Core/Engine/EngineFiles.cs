namespace Obkhodiki.Core.Engine;

/// <summary>
/// Antivirus programs often quarantine winws and the WinDivert driver (they are flagged as "hacking tools"),
/// which otherwise shows up as an obscure start error. These checks turn it into a clear message.
/// </summary>
public static class EngineFiles
{
    /// <summary>Files winws cannot start without.</summary>
    public static readonly IReadOnlyList<string> Required = new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" };

    public static IReadOnlyList<string> Missing(string binDir) =>
        Required.Where(f => !File.Exists(Path.Combine(binDir, f))).ToList();

    public static string MissingMessage(IReadOnlyList<string> missing, string binDir) =>
        $"Пропали файлы обхода: {string.Join(", ", missing)}. Скорее всего, их удалил антивирус — WinDivert он считает «хакерским инструментом», хотя это драйвер перехвата пакетов, которым пользуется zapret." +
        Environment.NewLine + Environment.NewLine + RecoverySteps(binDir);

    /// <summary>winws exited at start: a driver or file problem gets the antivirus hint, anything else is left alone.</summary>
    public static string? ExplainStartFailure(string error, string binDir)
    {
        var e = error.ToLowerInvariant();
        var driver = e.Contains("windivert") || e.Contains("error 2") || e.Contains("code 2:") || e.Contains("577") || e.Contains("1275")
                     || e.Contains("access is denied") || e.Contains("отказано в доступе");
        if (!driver) return null;
        return "winws не смог загрузить драйвер WinDivert. Чаще всего его блокирует или удаляет антивирус." +
               Environment.NewLine + Environment.NewLine + RecoverySteps(binDir);
    }

    private static string RecoverySteps(string binDir) =>
        "Что сделать:" + Environment.NewLine +
        "1. Откройте антивирус (в Windows — «Безопасность Windows» → «Защита от вирусов и угроз» → «Журнал защиты») и восстановите файлы из карантина." + Environment.NewLine +
        $"2. Добавьте папку {Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(binDir)))} в исключения антивируса." + Environment.NewLine +
        "3. Включите обход снова. Если файлы не вернуть — удалите Obkhodiki и поставьте заново после шага 2.";
}
