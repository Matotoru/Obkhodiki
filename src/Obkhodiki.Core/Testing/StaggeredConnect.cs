using System.Net;
using System.Net.Sockets;

namespace Obkhodiki.Core.Testing;

/// <summary>
/// Connects the way browsers and most apps do ("Happy Eyeballs", RFC 8305): when the first address of a host does
/// not answer quickly, the next one is tried in parallel instead of waiting for the first to time out. Otherwise a
/// single blocked IP among several (common for Cloudflare-fronted hosts) fails the whole check while the service
/// works fine for the user.
/// </summary>
public static class StaggeredConnect
{
    public static readonly TimeSpan DefaultStagger = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Starts <paramref name="connect"/> for the first candidate, then for the next one after
    /// <paramref name="stagger"/> or as soon as an attempt fails. The first success wins; the others are cancelled
    /// and their results disposed.
    /// </summary>
    public static async Task<T> FirstAsync<TCandidate, T>(
        IReadOnlyList<TCandidate> candidates, Func<TCandidate, CancellationToken, Task<T>> connect, TimeSpan stagger, CancellationToken ct)
        where T : class, IDisposable
    {
        if (candidates.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new List<Task<T>>();
        var next = 0;
        Exception? last = null;
        T? winner = null;
        try
        {
            while (winner is null)
            {
                if (next < candidates.Count) running.Add(connect(candidates[next++], race.Token));
                if (running.Count == 0) break;

                var wait = next < candidates.Count ? Task.Delay(stagger, race.Token) : null;
                var finished = wait is null
                    ? await Task.WhenAny(running).ConfigureAwait(false)
                    : await Task.WhenAny(running.Append(wait)).ConfigureAwait(false);
                if (finished == wait) continue; // stagger elapsed: start the next address alongside

                var attempt = (Task<T>)finished;
                running.Remove(attempt);
                try
                {
                    winner = await attempt.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    last = ex;
                }
            }
        }
        finally
        {
            race.Cancel();
            foreach (var other in running)
            {
                _ = other.ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose();
                    else _ = t.Exception;
                }, TaskScheduler.Default);
            }
        }
        ct.ThrowIfCancellationRequested();
        return winner ?? throw (last ?? new SocketException((int)SocketError.HostUnreachable));
    }

    /// <summary>A ConnectCallback for <see cref="SocketsHttpHandler"/>.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        var socket = await FirstAsync(addresses, async (address, token) =>
        {
            var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await s.ConnectAsync(new IPEndPoint(address, endpoint.Port), token).ConfigureAwait(false);
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }, DefaultStagger, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
