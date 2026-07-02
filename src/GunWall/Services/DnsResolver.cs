using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GunWall.Services;

/// <summary>
/// GunWall's own local DNS resolver. Listens on 127.0.0.1:&lt;port&gt;, forwards
/// queries to a configurable upstream over UDP, caches answers by their TTL, and
/// returns NXDOMAIN for domains on the blocklist.
///
/// It binds to loopback only (never the LAN) and never changes the system's DNS
/// settings by itself — the user points DNS at it. A guided system redirect with
/// a "Gaming Session" toggle is a later phase. A <see cref="Query"/> event fires
/// (off the UI thread) for every lookup so the UI can log it.
///
/// Sockets live here, but the class is free of any WPF types so the engine can be
/// exercised by an offline loopback test harness.
/// </summary>
public sealed class DnsResolver : IDisposable
{
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private IPEndPoint _upstream = new(IPAddress.Parse("1.1.1.1"), 53);
    private readonly object _gate = new();

    // Lowercased exact entries; subdomains are matched in IsBlocked.
    private volatile HashSet<string> _block = new(StringComparer.OrdinalIgnoreCase);

    // key "name|qtype" -> (response bytes, expiry UTC)
    private readonly ConcurrentDictionary<string, (byte[] Resp, DateTime Exp)> _cache = new();

    private long _total, _blocked, _cached, _forwarded, _errors;

    public bool Running { get; private set; }
    public int Port { get; private set; }
    public int BlockedDomainCount => _block.Count;

    public string Upstream => _upstream.Port == 53
        ? _upstream.Address.ToString()
        : $"{_upstream.Address}:{_upstream.Port}";

    public long Total => Interlocked.Read(ref _total);
    public long Blocked => Interlocked.Read(ref _blocked);
    public long Cached => Interlocked.Read(ref _cached);
    public long Forwarded => Interlocked.Read(ref _forwarded);
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Fires (off the UI thread) once per query with what we did.</summary>
    public event Action<DnsLogEntry>? Query;

    /// <summary>Replace the set of blocked domains. Accepts plain domains, blank
    /// lines, '#' comments, and hosts-style "0.0.0.0 domain" lines.</summary>
    public void SetBlocklist(IEnumerable<string>? domains)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (domains != null)
        {
            foreach (var raw in domains)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string d = raw.Trim();
                if (d.StartsWith("#")) continue;
                int sp = d.LastIndexOf(' ');                 // "0.0.0.0 domain"
                if (sp < 0) sp = d.LastIndexOf('\t');
                if (sp >= 0) d = d[(sp + 1)..].Trim();
                d = d.TrimEnd('.').ToLowerInvariant();
                if (d.Length > 0 && !d.StartsWith("#")) set.Add(d);
            }
        }
        _block = set;
    }

    /// <summary>True if the name, or any parent domain of it, is blocked.</summary>
    public bool IsBlocked(string name)
    {
        var set = _block;
        if (set.Count == 0 || string.IsNullOrEmpty(name)) return false;
        string n = name.TrimEnd('.').ToLowerInvariant();
        if (set.Contains(n)) return true;
        int idx = 0;
        while ((idx = n.IndexOf('.', idx)) >= 0)
        {
            idx++;
            if (idx < n.Length && set.Contains(n[idx..])) return true;
        }
        return false;
    }

    /// <summary>Parse "ip" or "ip:port" into an endpoint (defaults to port 53).</summary>
    public static IPEndPoint ParseUpstream(string? text, int defaultPort = 53)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return new IPEndPoint(IPAddress.Parse("1.1.1.1"), defaultPort);

        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon &&        // exactly one colon => IPv4:port
            int.TryParse(text[(colon + 1)..], out int port) && port is > 0 and <= 65535 &&
            IPAddress.TryParse(text[..colon], out var ipp))
        {
            return new IPEndPoint(ipp, port);
        }
        if (IPAddress.TryParse(text, out var ip)) return new IPEndPoint(ip, defaultPort);
        return new IPEndPoint(IPAddress.Parse("1.1.1.1"), defaultPort);
    }

    /// <summary>Start listening on 127.0.0.1:port. Throws if the port can't be bound.</summary>
    public void Start(int port, string upstream)
    {
        lock (_gate)
        {
            if (Running) return;
            _upstream = ParseUpstream(upstream);

            var listener = new UdpClient(AddressFamily.InterNetwork);
            listener.Client.Bind(new IPEndPoint(IPAddress.Loopback, port)); // loopback only
            _listener = listener;
            _cts = new CancellationTokenSource();
            Port = port;
            Running = true;

            var token = _cts.Token;
            _ = Task.Run(() => Loop(listener, token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!Running) return;
            Running = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            _cts = null;
        }
    }

    public void ClearCache() => _cache.Clear();

    private async Task Loop(UdpClient listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult req;
            try { req = await listener.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }
            _ = HandleAsync(listener, req, ct);
        }
    }

    private async Task HandleAsync(UdpClient listener, UdpReceiveResult req, CancellationToken ct)
    {
        byte[] query = req.Buffer;
        string name = ""; ushort qtype = 0; ushort id = 0;
        try
        {
            Interlocked.Increment(ref _total);

            if (!DnsMessage.TryReadQuestion(query, out name, out qtype, out id))
            {
                Interlocked.Increment(ref _errors);
                Emit(name, qtype, DnsAction.Error);
                return;
            }

            // 1) blocklist -> NXDOMAIN
            if (IsBlocked(name))
            {
                byte[] nx = DnsMessage.BuildNxDomain(query);
                await listener.SendAsync(nx, nx.Length, req.RemoteEndPoint);
                Interlocked.Increment(ref _blocked);
                Emit(name, qtype, DnsAction.Blocked);
                return;
            }

            // 2) cache (keyed by name + type; id rewritten per requester)
            string key = name.ToLowerInvariant() + "|" + qtype;
            if (_cache.TryGetValue(key, out var hit) && hit.Exp > DateTime.UtcNow)
            {
                byte[] cached = DnsMessage.WithId(hit.Resp, id);
                await listener.SendAsync(cached, cached.Length, req.RemoteEndPoint);
                Interlocked.Increment(ref _cached);
                Emit(name, qtype, DnsAction.Cached);
                return;
            }

            // 3) forward upstream
            byte[]? answer = await ForwardAsync(query, ct);
            if (answer == null)
            {
                Interlocked.Increment(ref _errors);
                Emit(name, qtype, DnsAction.Error);
                return;
            }
            await listener.SendAsync(answer, answer.Length, req.RemoteEndPoint);

            int ttl = DnsMessage.GetMinTtl(answer);
            _cache[key] = ((byte[])answer.Clone(), DateTime.UtcNow.AddSeconds(ttl));
            Interlocked.Increment(ref _forwarded);
            Emit(name, qtype, DnsAction.Forwarded);
        }
        catch
        {
            Interlocked.Increment(ref _errors);
            Emit(name, qtype, DnsAction.Error);
        }
    }

    private async Task<byte[]?> ForwardAsync(byte[] query, CancellationToken ct)
    {
        using var up = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            await up.SendAsync(query, query.Length, _upstream);
            var recv = up.ReceiveAsync(ct).AsTask();
            var done = await Task.WhenAny(recv, Task.Delay(3000, ct));
            if (done != recv) return null;                  // upstream timed out
            return recv.Result.Buffer;
        }
        catch { return null; }
    }

    private void Emit(string name, ushort qtype, DnsAction action)
    {
        var cb = Query;
        if (cb == null) return;
        try
        {
            cb(new DnsLogEntry(
                DateTime.Now,
                name.Length == 0 ? "(malformed)" : name,
                DnsMessage.TypeName(qtype),
                action));
        }
        catch { /* a logging sink must never break resolution */ }
    }

    public void Dispose() => Stop();
}
