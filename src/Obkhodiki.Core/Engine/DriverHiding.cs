using System.Security.Cryptography;

namespace Obkhodiki.Core.Engine;

/// <summary>
/// bol-van's windivert-hide (zapret-win-bundle): a WinDivert.dll patched to install the unchanged, signed driver as
/// service "Monkey" from file Monkey64.sys. It only hides from software that looks for the "WinDivert" service or the
/// WinDivert64.sys file; the device the driver creates keeps its original name. Flowseal's README points anti-cheat
/// complaints here.
/// winws runs from a separate folder holding its exe and dlls with the patched dll and the renamed driver; the
/// engine folder itself stays untouched, so turning hiding off is just starting from the engine folder again.
/// </summary>
public static class DriverHiding
{
    public const string StandardService = "WinDivert";
    public const string HiddenService = "Monkey";
    public const string DllName = "WinDivert.dll";
    public const string StandardDriverFile = "WinDivert64.sys";
    public const string HiddenDriverFile = "Monkey64.sys";
    public const long MaxDllBytes = 1024 * 1024;

    /// <summary>The bundle repository is re-pushed as a single commit, so the pinned commit URL eventually disappears;
    /// master is the fallback. Either way only the file with <see cref="PatchedDllSha256"/> is accepted.</summary>
    public static readonly IReadOnlyList<Uri> PatchedDllUrls = new[]
    {
        new Uri("https://raw.githubusercontent.com/bol-van/zapret-win-bundle/de9795d71287fdb21cf8c20b8af1a09d0eb10ab2/windivert-hide/WinDivert.dll"),
        new Uri("https://raw.githubusercontent.com/bol-van/zapret-win-bundle/master/windivert-hide/WinDivert.dll"),
    };

    public const string PatchedDllSha256 = "944368b7a6eb0a97701e152a9171267db66377cc31bde415ab5da22c7a09cc43";

    /// <summary>The driver the patched dll was built against (bol-van's Monkey64.sys is a byte copy of it; Flowseal 1.10.3 ships it).
    /// A different driver from a newer Flowseal could disagree with the patched dll, so hiding refuses to run with it.</summary>
    public const string SupportedDriverSha256 = "8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2";

    public const string UnsupportedDriverMessage =
        "Скрытие драйвера не подходит к этой версии Flowseal: в ней другой драйвер WinDivert, а скрытая версия от bol-van собрана под прежний. " +
        "Выключите скрытие или дождитесь обновления Obkhodiki.";

    /// <summary>The driver service a start in this mode must find stopped: two copies of the driver cannot be loaded at once
    /// (they create the same device), and a loaded "WinDivert" would defeat hiding.</summary>
    public static string ServiceToStop(bool hide) => hide ? StandardService : HiddenService;

    public static bool IsPatchedDll(byte[] bytes) => Sha256(bytes) == PatchedDllSha256;

    public static bool HasSupportedDriver(string engineBinDir) => HasSupportedDriver(engineBinDir, SupportedDriverSha256);

    internal static bool HasSupportedDriver(string engineBinDir, string driverSha256)
    {
        var driver = Path.Combine(engineBinDir, StandardDriverFile);
        return File.Exists(driver) && Sha256(File.ReadAllBytes(driver)) == driverSha256;
    }

    /// <summary>Fills <paramref name="hiddenBinDir"/>: winws and its dlls from the engine, the patched dll, the driver renamed.
    /// Files already in place are left alone, so a running winws from this folder does not block a repeated call.</summary>
    /// <exception cref="InvalidOperationException">The patched dll is missing or altered, or the engine driver is not the supported one.</exception>
    public static void Prepare(string engineBinDir, string hiddenBinDir, string patchedDllPath) =>
        Prepare(engineBinDir, hiddenBinDir, patchedDllPath, SupportedDriverSha256, PatchedDllSha256);

    internal static void Prepare(string engineBinDir, string hiddenBinDir, string patchedDllPath, string driverSha256, string dllSha256)
    {
        if (!File.Exists(patchedDllPath) || Sha256(File.ReadAllBytes(patchedDllPath)) != dllSha256)
        {
            throw new InvalidOperationException(
                "Файл скрытого драйвера пропал или изменён (возможно, его удалил антивирус). Выключите и снова включите скрытие драйвера, чтобы скачать его заново.");
        }
        if (!HasSupportedDriver(engineBinDir, driverSha256)) throw new InvalidOperationException(UnsupportedDriverMessage);

        Directory.CreateDirectory(hiddenBinDir);
        foreach (var source in Directory.EnumerateFiles(engineBinDir))
        {
            var name = Path.GetFileName(source);
            var ext = Path.GetExtension(name);
            if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals(DllName, StringComparison.OrdinalIgnoreCase)) continue;
            CopyIfDifferent(source, Path.Combine(hiddenBinDir, name));
        }
        CopyIfDifferent(patchedDllPath, Path.Combine(hiddenBinDir, DllName));
        CopyIfDifferent(Path.Combine(engineBinDir, StandardDriverFile), Path.Combine(hiddenBinDir, HiddenDriverFile));
    }

    /// <summary>Downloads the patched dll into <paramref name="targetPath"/>, trying each mirror, and keeps it only if it
    /// hashes to <see cref="PatchedDllSha256"/>. A file already there with the right hash is kept without a download.</summary>
    public static async Task DownloadAsync(HttpClient http, string targetPath, CancellationToken ct)
    {
        if (File.Exists(targetPath) && IsPatchedDll(await File.ReadAllBytesAsync(targetPath, ct).ConfigureAwait(false))) return;

        Exception? last = null;
        foreach (var url in PatchedDllUrls)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxDllBytes) throw new InvalidDataException("Patched WinDivert.dll is larger than expected.");

                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!IsPatchedDll(bytes)) throw new InvalidDataException("Downloaded WinDivert.dll does not match the expected build.");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                var temp = targetPath + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                File.Move(temp, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(
            "Не удалось скачать скрытую версию драйвера (windivert-hide от bol-van). Проверьте соединение и попробуйте снова.", last);
    }

    private static void CopyIfDifferent(string source, string target)
    {
        if (File.Exists(target) && Sha256(File.ReadAllBytes(target)) == Sha256(File.ReadAllBytes(source))) return;
        var temp = target + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
