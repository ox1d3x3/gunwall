using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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

    // Resolved-IPv4 memory for P2P/direct detection: every A record this
    // resolver has handed out. Bounded; on overflow the whole set resets
    // (a brief blind spot beats unbounded growth).
    private readonly HashSet<uint> _resolvedV4 = new();
    private readonly object _resolvedLock = new();
    private const int MaxResolvedIps = 30000;

    // ---- §3a Secure DNS (DNS-over-HTTPS, RFC 8484) ----
    // The endpoint is addressed by IP wherever possible, so enabling DoH never
    // needs a plaintext lookup to bootstrap itself (no chicken-and-egg).
    private string _dohUrl = "";
    private bool _dohFallback;          // allow plaintext if DoH fails (off = fail closed)
    private HttpClient? _http;
    private long _dohOk, _dohFail;

    private long _total, _blocked, _cached, _forwarded, _errors;

    public bool Running { get; private set; }
    public int Port { get; private set; }
    public int BlockedDomainCount => _block.Count;

    public string Upstream => _upstream.Port == 53
        ? _upstream.Address.ToString()
        : $"{_upstream.Address}:{_upstream.Port}";

    /// <summary>True when queries leave the machine encrypted over HTTPS.</summary>
    public bool SecureDns => _dohUrl.Length > 0;

    /// <summary>The active DoH endpoint, or "" when forwarding in plaintext.</summary>
    public string DohUrl => _dohUrl;

    /// <summary>Whether a DoH failure may silently fall back to plain UDP.</summary>
    public bool DohFallbackAllowed => _dohFallback;

    public long DohSuccess => Interlocked.Read(ref _dohOk);
    public long DohFailures => Interlocked.Read(ref _dohFail);

    /// <summary>What the UI shows as the effective upstream.</summary>
    public string UpstreamLabel => SecureDns ? $"{_dohUrl} (encrypted)" : $"{Upstream} (plaintext)";

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

    /// <summary>
    /// Built-in DoH endpoints. Each is addressed by IP and each provider's
    /// certificate covers that IP, so no plaintext lookup is needed to reach
    /// them. Item 1 is the display name, item 2 the URL, item 3 the matching
    /// plaintext address used when fallback is permitted.
    /// </summary>
    public static readonly (string Name, string Url, string PlainIp)[] DohPresets =
    {
        ("Cloudflare",                  "https://1.1.1.1/dns-query",       "1.1.1.1"),
        ("Cloudflare (block malware)",  "https://1.1.1.2/dns-query",       "1.1.1.2"),
        ("Google",                      "https://8.8.8.8/dns-query",       "8.8.8.8"),
        ("Quad9 (block malware)",       "https://9.9.9.9/dns-query",       "9.9.9.9"),
        ("AdGuard (block ads)",         "https://94.140.14.14/dns-query",  "94.140.14.14"),
    };

    /// <summary>
    /// Validates a DoH endpoint. HTTPS is mandatory - the whole point is that
    /// the query is encrypted - and the URL must be well formed. Returns the
    /// normalised URL, or null with a reason.
    /// </summary>
    public static string? ValidateDohUrl(string? url, out string error)
    {
        error = "";
        string u = (url ?? "").Trim();
        if (u.Length == 0) { error = "Enter a DoH URL, e.g. https://1.1.1.1/dns-query"; return null; }
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri))
        { error = "That isn't a valid URL."; return null; }
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        { error = "Secure DNS requires an https:// endpoint."; return null; }
        if (uri.Host.Length == 0) { error = "The URL has no host."; return null; }
        return uri.ToString();
    }

    /// <summary>
    /// The plaintext address matching a DoH URL, when one is known. Used so
    /// that a hostname-free bootstrap is possible and so permitted fallback
    /// stays with the same provider instead of silently changing operators.
    /// </summary>
    public static string PlainPeerFor(string dohUrl)
    {
        foreach (var p in DohPresets)
            if (string.Equals(p.Url, dohUrl, StringComparison.OrdinalIgnoreCase)) return p.PlainIp;
        // A custom endpoint given as https://<ip>/... can serve as its own peer.
        if (Uri.TryCreate(dohUrl ?? "", UriKind.Absolute, out var uri) &&
            IPAddress.TryParse(uri.Host, out _)) return uri.Host;
        return "";
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

    /// <summary>
    /// Start listening on 127.0.0.1:port. Throws if the port can't be bound.
    /// When <paramref name="dohUrl"/> is a valid https endpoint, queries are
    /// forwarded encrypted over HTTPS; <paramref name="dohFallback"/> decides
    /// whether a DoH failure may fall back to plaintext (off = fail closed).
    /// </summary>
    public void Start(int port, string upstream, string? dohUrl = null, bool dohFallback = false)
    {
        lock (_gate)
        {
            if (Running) return;
            _upstream = ParseUpstream(upstream);

            _dohUrl = ValidateDohUrl(dohUrl, out _) ?? "";
            _dohFallback = dohFallback;
            Interlocked.Exchange(ref _dohOk, 0);
            Interlocked.Exchange(ref _dohFail, 0);
            if (_dohUrl.Length > 0)
            {
                _http?.Dispose();
                _http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(5),
                    DefaultRequestVersion = new Version(2, 0)
                };
                _http.DefaultRequestHeaders.Accept.Clear();
                _http.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-message"));
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("GunWall/1.0");
            }

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
            TrackResolvedIps(answer);
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
        if (_dohUrl.Length > 0)
        {
            byte[]? secure = await ForwardDohAsync(query, ct);
            if (secure != null) { Interlocked.Increment(ref _dohOk); return secure; }

            Interlocked.Increment(ref _dohFail);
            // Fail closed unless the user explicitly permitted plaintext fallback:
            // silently downgrading would defeat the point of encrypting queries.
            if (!_dohFallback) return null;
        }
        return await ForwardPlainAsync(query, ct);
    }

    /// <summary>
    /// RFC 8484 DoH: POST the raw DNS wire query as application/dns-message and
    /// read the wire response back. Never throws; null means "failed", which
    /// the caller turns into either fallback or an error per policy.
    /// </summary>
    private async Task<byte[]?> ForwardDohAsync(byte[] query, CancellationToken ct)
    {
        var http = _http;
        if (http == null) return null;
        try
        {
            using var content = new ByteArrayContent(query);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
            using var resp = await http.PostAsync(_dohUrl, content, ct);
            if (!resp.IsSuccessStatusCode) return null;

            byte[] body = await resp.Content.ReadAsByteArrayAsync(ct);
            // A DNS response is at least a 12-byte header; anything shorter is
            // a captive portal or error page, not an answer.
            return body.Length >= 12 ? body : null;
        }
        catch { return null; }
    }

    private async Task<byte[]?> ForwardPlainAsync(byte[] query, CancellationToken ct)
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

    public void Dispose()
    {
        Stop();
        try { _http?.Dispose(); } catch { }
        _http = null;
    }

    private void TrackResolvedIps(byte[] answer)
    {
        var ips = DnsMessage.ExtractARecords(answer);
        if (ips.Count == 0) return;
        lock (_resolvedLock)
        {
            if (_resolvedV4.Count + ips.Count > MaxResolvedIps) _resolvedV4.Clear();
            foreach (uint ip in ips) _resolvedV4.Add(ip);
        }
    }

    /// <summary>True if this resolver has ever answered with the given IPv4 -
    /// i.e. some app looked it up by name. A public IP an app dials WITHOUT
    /// this being true is a direct/P2P connection.</summary>
    public bool WasResolved(string ipv4)
    {
        if (!System.Net.IPAddress.TryParse(ipv4, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return true; // non-v4: don't accuse what we can't check
        var b = ip.GetAddressBytes();
        uint v = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        lock (_resolvedLock) return _resolvedV4.Contains(v);
    }

    public int ResolvedIpCount { get { lock (_resolvedLock) return _resolvedV4.Count; } }
}
