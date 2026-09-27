using System.Security.Cryptography;
using System.Text;

namespace Obkhodiki.App;

/// <summary>A saved (inactive) subscription or server as the VPS page shows it; no URL, links or passwords.</summary>
public sealed record SavedVpnSourceView(string Id, string Title, string Details);

/// <summary>Several subscriptions: the replaced one is kept, and the user can switch back to it.</summary>
internal sealed partial class AppController
{
    private List<VpnSource> _saved = new();

    public IReadOnlyList<SavedVpnSourceView> SavedVpnSources { get; private set; } = Array.Empty<SavedVpnSourceView>();

    // Stable handle for the UI that does not reveal the subscription URL.
    private static string IdOf(VpnSource source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Key)))[..16];

    private void UseSaved(List<VpnSource> saved)
    {
        _saved = saved;
        SavedVpnSources = saved.Select(s => new SavedVpnSourceView(IdOf(s), s.Describe(), SavedDetails(s))).ToList();
    }

    private static string SavedDetails(VpnSource s)
    {
        if (!s.IsSubscription) return "Один сервер";
        var parts = new List<string> { "Подписка" };
        if (s.Expire is { } exp) parts.Add($"до {exp.ToLocalTime():dd.MM.yyyy}");
        if (s.FetchedAt is { } at) parts.Add($"обновлена {at.ToLocalTime():dd.MM HH:mm}");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Makes <paramref name="source"/> the active one. The previous active source (with its chosen server) moves
    /// to the saved list; the new one leaves it. Runs inside <see cref="Serialized"/>.
    /// </summary>
    private void Activate(VpnSource source)
    {
        var saved = _saved.Where(s => s.Key != source.Key).ToList();
        if (VpnSourceStore.Load() is { } current && current.Key != source.Key && current.Key.Length > 0)
        {
            current.SelectedServer = Settings.VpnSelectedServer;
            saved.RemoveAll(s => s.Key == current.Key);
            saved.Insert(0, current);
        }
        var selected = source.SelectedServer;
        source.SelectedServer = null;
        VpnSourceStore.SaveSaved(saved);
        VpnSourceStore.Save(source);

        Settings.VpnSelectedServer = selected;
        _settingsStore.Save(Settings);

        UseSaved(saved);
        UseSource(source);
        _appliedSignature = null; // force a restart with the new servers
        _pings.Clear();
        _lastSubscriptionAttempt = DateTime.MinValue;
    }

    public Task SwitchVpnSourceAsync(string id) => Serialized("Смена подписки VPS…", silentErrors: false, async () =>
    {
        if (_saved.FirstOrDefault(s => IdOf(s) == id) is not { } source) return;
        Activate(source);
        Log.Info($"VPS source switched to {source.Describe()}");
        await ApplyVpnCoreAsync(interactive: true, needProbe: false);
    });

    public Task DeleteSavedVpnSourceAsync(string id) => Serialized("Удаление подписки…", silentErrors: false, () =>
    {
        var saved = _saved.Where(s => IdOf(s) != id).ToList();
        VpnSourceStore.SaveSaved(saved);
        UseSaved(saved);
        return Task.CompletedTask;
    });
}
