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
    private UdpClient? _listener;    // IPv4 loopback (127.0.0.1)
    private UdpClient? _listener6;   // IPv6 loopback (::1)
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
    // Maps each resolved IPv4 to the name that produced it. The set membership
    // still answers "was this looked up?" (P2P detection); the value additionally
    // answers "what was it called?", which is what lets an access rule match a
    // domain even though the connection itself only carries an address.
    private readonly Dictionary<uint, string> _resolvedV4 = new();
    private readonly object _resolvedLock = new();
    private const int MaxResolvedIps = 30000;

    // ---- §3a Secure DNS (DNS-over-HTTPS, RFC 8484) ----
    // The endpoint is addressed by IP wherever possible, so enabling DoH never
    // needs a plaintext lookup to bootstrap itself (no chicken-and-egg).
    private string _dohUrl = "";
    private bool _dohFallback;          // allow plaintext if DoH fails (off = fail closed)
    private HttpClient? _http;
    private long _dohOk, _dohFail;

    // Upstream answered, but with a failure code (SERVFAIL / REFUSED / ...).
    // Tracked separately from transport failures because the two need very
    // different diagnoses.
    private long _upstreamRefused;

    // Which loopback family queries actually arrive on, and whether replies
    // leave successfully. Without these, a resolver that is answering perfectly
    // and a resolver nothing can reach look identical from the outside.
    private long _recvV4, _recvV6, _sendOk, _sendFail;
    private string _lastSendError = "";

    /// <summary>How long a "name does not exist" answer may be remembered.</summary>
    private const int NegativeTtlSeconds = 30;

    // ---- §3b CNAME-cloaking defense ----
    private bool _blockCloaked = true;
    private long _cloaked;



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

    /// <summary>Whether CNAME chains are checked against the blocklist.</summary>
    public bool BlockCloakedCnames => _blockCloaked;

    /// <summary>Lookups denied because a name in their CNAME chain was blocked.</summary>
    public long CloakedBlocked => Interlocked.Read(ref _cloaked);

    /// <summary>Most recent cloak caught, as "queried -> hidden target" (for logs).</summary>
    public string LastCloak { get; private set; } = "";

    /// <summary>Queries received on 127.0.0.1 this session.</summary>
    public long ReceivedV4 => Interlocked.Read(ref _recvV4);

    /// <summary>Queries received on ::1 this session.</summary>
    public long ReceivedV6 => Interlocked.Read(ref _recvV6);

    /// <summary>Replies successfully handed to the socket.</summary>
    public long RepliesSent => Interlocked.Read(ref _sendOk);

    /// <summary>Replies that failed to send.</summary>
    public long ReplySendFailures => Interlocked.Read(ref _sendFail);

    public string LastSendError => _lastSendError;

    /// <summary>Which loopback endpoints are actually bound, for diagnostics.</summary>
    public string ListenerStatus { get; private set; } = "not started";

    /// <summary>Upstream responses carrying a DNS failure code this session.</summary>
    public long UpstreamRefused => Interlocked.Read(ref _upstreamRefused);

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

    /// <summary>What became of one blocklist line.</summary>
    /// <param name="Original">The line as the user wrote it.</param>
    /// <param name="Domain">The domain that will actually be blocked, or "".</param>
    /// <param name="Problem">Why the line was unusable, or "" if it was fine.</param>
    public readonly record struct BlocklistEntry(string Original, string Domain, string Problem)
    {
        public bool Ignored => Domain.Length == 0 && Problem.Length == 0;   // blank or comment
        public bool Rejected => Domain.Length == 0 && Problem.Length > 0;
        public bool Rewritten => Domain.Length > 0 &&
            !string.Equals(Domain, Original.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns one written line into the domain it means, or "" if it cannot mean
    /// one. DNS blocking matches NAMES, so a pasted URL, a hosts-file line, or
    /// an adblock-style wildcard all have to be reduced to the bare hostname
    /// first - previously a line like "https://example.com/" was stored verbatim
    /// and could never match anything, with nothing to say so.
    /// </summary>
    public static string NormalizeBlocklistEntry(string? raw, out string problem)
    {
        problem = "";
        string d = (raw ?? "").Trim();
        if (d.Length == 0) return "";
        if (d.StartsWith("#") || d.StartsWith("!")) return "";   // comment styles

        int hash = d.IndexOf('#');                                // trailing comment
        if (hash >= 0) d = d[..hash].Trim();
        if (d.Length == 0) return "";

        // Hosts-file form: "0.0.0.0 example.com" / "127.0.0.1  example.com".
        var parts = d.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            // Only the hosts-file shape is unambiguous. Anything else with
            // spaces is guesswork, and guessing here silently stores a fragment
            // that can never match - better to say the line wasn't understood.
            if (!IPAddress.TryParse(parts[0], out _))
            { problem = "one domain per line (this line has several words)"; return ""; }
            d = parts[1];
        }
        else if (parts.Length == 1)
            d = parts[0];

        if (d.Contains("://", StringComparison.Ordinal))          // scheme
            d = d[(d.IndexOf("://", StringComparison.Ordinal) + 3)..];
        int at = d.LastIndexOf('@');                              // user:pass@
        if (at >= 0) d = d[(at + 1)..];
        int slash = d.IndexOf('/');                               // path
        if (slash >= 0) d = d[..slash];
        int q = d.IndexOfAny(new[] { '?', '#' });                 // query / fragment
        if (q >= 0) d = d[..q];
        if (d.StartsWith("*.", StringComparison.Ordinal)) d = d[2..];   // adblock wildcard
        d = d.TrimStart('.').TrimEnd('.').Trim();
        int colon = d.IndexOf(':');                               // :port
        if (colon >= 0) d = d[..colon];
        d = d.ToLowerInvariant();

        if (d.Length == 0) { problem = "no hostname in this line"; return ""; }
        if (IPAddress.TryParse(d, out _))
        {
            problem = "that's an IP address - DNS blocking matches names, not addresses";
            return "";
        }
        if (d.Length > 253) { problem = "hostname is too long"; return ""; }

        // Every hosts file opens by mapping these names to loopback. They are
        // the file's own plumbing, not entries to block, and treating them as
        // blocks is actively harmful: "localhost" resolves to 127.0.0.1, so
        // blocking it aims a rule at the machine itself.
        if (LoopbackNames.Contains(d))
        {
            problem = "that's a loopback name from the top of a hosts file, not a site to block";
            return "";
        }
        foreach (var label in d.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63)
            { problem = "not a valid hostname"; return ""; }
            foreach (char c in label)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                { problem = $"'{c}' isn't valid in a hostname"; return ""; }
        }
        return d;
    }

    /// <summary>
    /// Reports what every line would do, so the interface can tell the user
    /// which entries were rewritten and which were thrown away rather than
    /// silently accepting input that can never match.
    /// </summary>
    public static List<BlocklistEntry> InspectBlocklist(IEnumerable<string>? lines)
    {
        var result = new List<BlocklistEntry>();
        if (lines == null) return result;
        foreach (var raw in lines)
        {
            string d = NormalizeBlocklistEntry(raw, out string problem);
            result.Add(new BlocklistEntry(raw ?? "", d, problem));
        }
        return result;
    }

    /// <summary>Replace the set of blocked domains. Accepts plain domains, blank
    /// lines, comments, hosts-style lines, pasted URLs and wildcard forms.</summary>
    public void SetBlocklist(IEnumerable<string>? domains)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (domains != null)
            foreach (var raw in domains)
            {
                string d = NormalizeBlocklistEntry(raw, out _);
                if (d.Length > 0) set.Add(d);
            }
        _block = set;
        // A name resolved before the rule existed would keep being served from
        // cache, so the block would appear not to work at all.
        _cache.Clear();
    }

    /// <summary>Names every hosts file defines for the local machine.</summary>
    private static readonly HashSet<string> LoopbackNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost",
        "ip6-localhost", "ip6-loopback", "ip6-localnet", "ip6-mcastprefix",
        "ip6-allnodes", "ip6-allrouters", "ip6-allhosts", "0.0.0.0"
    };

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
    public void Start(int port, string upstream, string? dohUrl = null, bool dohFallback = false,
                      bool blockCloakedCnames = true)
    {
        lock (_gate)
        {
            if (Running) return;
            _upstream = ParseUpstream(upstream);
            _blockCloaked = blockCloakedCnames;
            Interlocked.Exchange(ref _cloaked, 0);

            _dohUrl = ValidateDohUrl(dohUrl, out _) ?? "";
            _dohFallback = dohFallback;
            Interlocked.Exchange(ref _dohOk, 0);
            Interlocked.Exchange(ref _dohFail, 0);
            Interlocked.Exchange(ref _upstreamRefused, 0);
            _cache.Clear();   // never carry answers across a configuration change
            if (_dohUrl.Length > 0)
            {
                _http?.Dispose();
                // A SocketsHttpHandler with pooled, kept-warm connections. The
                // previous default handler let a single slow request's timeout
                // abort the shared connection, which then broke the next few
                // in-flight lookups - the cause of the intermittent failures
                // seen against slower DoH endpoints. Pinning the pool lifetime
                // and keep-alive holds the TLS session open between queries.
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                    MaxConnectionsPerServer = 8,
                    EnableMultipleHttp2Connections = true,
                    ConnectTimeout = TimeSpan.FromSeconds(5)
                };
                _http = new HttpClient(handler)
                {
                    // No global Timeout: a single slow lookup must fail on its
                    // OWN cancellation token, never by tearing down the client.
                    // Each request is bounded by PerQueryTimeout below.
                    Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                    DefaultRequestVersion = new Version(2, 0),
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                _http.DefaultRequestHeaders.Accept.Clear();
                _http.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-message"));
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("GunWall/1.0");
            }

            var listener = new UdpClient(AddressFamily.InterNetwork);
            listener.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));   // 127.0.0.1
            _listener = listener;

            // Windows resolves against BOTH loopback families, and with a VPN
            // active it often prefers ::1. Binding v4 only meant those queries
            // hit nothing and the name "could not be found" even though the
            // resolver was healthy - the reported outage. Bind ::1 as well.
            // If v6 is unavailable on the machine, that's not fatal: v4 still
            // serves, so the failure is logged and swallowed.
            try
            {
                var l6 = new UdpClient(AddressFamily.InterNetworkV6);
                l6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));  // ::1
                _listener6 = l6;
            }
            catch (Exception ex)
            {
                _listener6 = null;
                DiagnosticLog.Log($"DNS resolver: IPv6 loopback bind FAILED, serving IPv4 only ({ex.Message}).");
            }

            // Positive confirmation, not just failure logging: previously a
            // successful bind produced no evidence at all, so a diagnostics
            // bundle couldn't distinguish "listening on both" from "v4 only".
            ListenerStatus = _listener6 != null
                ? $"127.0.0.1:{port} and [::1]:{port}"
                : $"127.0.0.1:{port} only (no IPv6)";
            DiagnosticLog.Log($"DNS resolver listening on {ListenerStatus}.");
            Interlocked.Exchange(ref _recvV4, 0);
            Interlocked.Exchange(ref _recvV6, 0);
            Interlocked.Exchange(ref _sendOk, 0);
            Interlocked.Exchange(ref _sendFail, 0);
            _lastSendError = "";

            _cts = new CancellationTokenSource();
            Port = port;
            Running = true;

            var token = _cts.Token;
            _ = Task.Run(() => Loop(listener, token));
            if (_listener6 != null)
                _ = Task.Run(() => Loop(_listener6, token));
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
            try { _listener6?.Close(); } catch { }
            _listener = null;
            _listener6 = null;
            _cts = null;
        }
    }

    public void ClearCache() => _cache.Clear();

    /// <summary>Outcome of one loopback probe.</summary>
    public readonly record struct PathProbe(string Endpoint, bool Ok, string Detail);

    /// <summary>
    /// Asks this resolver a real question over the loopback socket, exactly as
    /// Windows would, and reports what came back. This is the one test that
    /// distinguishes the two possibilities the counters cannot: a resolver that
    /// answers correctly but whose replies never reach the client, versus one
    /// the client never reaches at all. Both families are probed, because
    /// Windows prefers IPv6 and will use whichever it is configured with.
    /// </summary>
    /// <summary>
    /// Sends a UDP datagram between two sockets in this process over loopback,
    /// with no involvement from the resolver at all. This separates the two
    /// possibilities cleanly: if even this fails, plain UDP loopback delivery is
    /// broken on the machine - by another network filter driver, not by
    /// anything in the resolver - and no local DNS server of any kind could
    /// work here.
    /// </summary>
    public static async Task<PathProbe> TestRawLoopbackAsync(AddressFamily family)
    {
        string label = family == AddressFamily.InterNetworkV6
            ? "raw UDP loopback [::1]" : "raw UDP loopback 127.0.0.1";
        IPAddress addr = family == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        try
        {
            using var receiver = new UdpClient(family);
            receiver.Client.Bind(new IPEndPoint(addr, 0));       // any free port
            int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;

            using var sender = new UdpClient(family);
            byte[] payload = System.Text.Encoding.ASCII.GetBytes("GUNWALL-LOOPBACK-PROBE");

            using var cts = new CancellationTokenSource(2000);
            var receiving = receiver.ReceiveAsync(cts.Token).AsTask();
            await sender.SendAsync(payload, payload.Length, new IPEndPoint(addr, port));

            try
            {
                var got = await receiving;
                bool same = got.Buffer.Length == payload.Length;
                return new PathProbe(label, same,
                    same ? $"delivered {got.Buffer.Length} bytes"
                         : $"delivered but altered ({got.Buffer.Length} bytes)");
            }
            catch (OperationCanceledException)
            {
                return new PathProbe(label, false,
                    "NOT DELIVERED - a plain UDP packet between two sockets in this " +
                    "process never arrived. Something outside GunWall is dropping " +
                    "loopback UDP; no local DNS server can work until that stops.");
            }
        }
        catch (Exception ex)
        {
            return new PathProbe(label, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Same as the raw probe, but the payload is a real DNS message and the
    /// listening socket sends its reply FROM a port that looks like a DNS
    /// server. Comparing this against the plain probe isolates what a third
    /// party is reacting to: if arbitrary bytes cross loopback but DNS-shaped
    /// bytes do not, something is inspecting DNS specifically.
    /// </summary>
    public static async Task<PathProbe> TestDnsShapedLoopbackAsync(bool useDnsPort)
    {
        string label = useDnsPort
            ? "DNS-shaped reply from port 53"
            : "DNS-shaped reply from a high port";
        try
        {
            using var server = new UdpClient(AddressFamily.InterNetwork);
            int serverPort = useDnsPort ? 53 : 0;
            try { server.Client.Bind(new IPEndPoint(IPAddress.Loopback, serverPort)); }
            catch (SocketException) when (useDnsPort)
            {
                return new PathProbe(label, false, "port 53 already in use (the resolver holds it)");
            }
            serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            byte[] query = DnsMessage.BuildQuery("example.com", 1, 0x5151);
            var serverEp = new IPEndPoint(IPAddress.Loopback, serverPort);

            using var serverCts = new CancellationTokenSource(2000);
            var serverRecv = server.ReceiveAsync(serverCts.Token).AsTask();
            await client.SendAsync(query, query.Length, serverEp);

            UdpReceiveResult got;
            try { got = await serverRecv; }
            catch (OperationCanceledException)
            { return new PathProbe(label, false, "the query itself never reached the listener"); }

            // Answer it: a minimal DNS response (QR set, no answers).
            byte[] reply = (byte[])query.Clone();
            reply[2] = 0x81; reply[3] = 0x80;          // QR=1, RD=1, RA=1, NOERROR

            using var clientCts = new CancellationTokenSource(2000);
            var clientRecv = client.ReceiveAsync(clientCts.Token).AsTask();
            await server.SendAsync(reply, reply.Length, got.RemoteEndPoint);

            try
            {
                var back = await clientRecv;
                return new PathProbe(label, back.Buffer.Length == reply.Length,
                    $"reply delivered ({back.Buffer.Length} bytes)");
            }
            catch (OperationCanceledException)
            {
                return new PathProbe(label, false,
                    "the REPLY was dropped - the query crossed loopback fine, so something is " +
                    "discarding DNS responses");
            }
        }
        catch (Exception ex)
        {
            return new PathProbe(label, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<List<PathProbe>> TestLoopbackPathAsync(string probeName = "example.com")
    {
        var results = new List<PathProbe>();

        // Establish first whether loopback UDP works at all on this machine.
        results.Add(await TestRawLoopbackAsync(AddressFamily.InterNetwork));
        results.Add(await TestRawLoopbackAsync(AddressFamily.InterNetworkV6));

        // Then whether DNS-shaped traffic survives the same trip. This is the
        // comparison that identifies DNS-aware interception.
        results.Add(await TestDnsShapedLoopbackAsync(useDnsPort: false));

        if (!Running)
        {
            results.Add(new PathProbe("resolver", false, "not running"));
            return results;
        }

        foreach (var (label, addr) in new[]
                 { ($"127.0.0.1:{Port}", IPAddress.Loopback), ($"[::1]:{Port}", IPAddress.IPv6Loopback) })
        {
            if (addr.Equals(IPAddress.IPv6Loopback) && _listener6 == null)
            {
                results.Add(new PathProbe(label, false, "not listening on IPv6"));
                continue;
            }
            try
            {
                using var probe = new UdpClient(addr.AddressFamily);
                probe.Client.ReceiveTimeout = 3000;
                byte[] query = DnsMessage.BuildQuery(probeName, 1, 0x4747);
                var target = new IPEndPoint(addr, Port);

                long recvBefore = ReceivedV4 + ReceivedV6;
                long sentBefore = RepliesSent;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await probe.SendAsync(query, query.Length, target);

                using var cts = new CancellationTokenSource(3000);
                byte[] reply;
                try
                {
                    var r = await probe.ReceiveAsync(cts.Token);
                    reply = r.Buffer;
                }
                catch (OperationCanceledException)
                {
                    // Did the resolver even see it? That single fact decides
                    // whether the request or the reply is the broken half.
                    await Task.Delay(150);
                    bool sawIt = (ReceivedV4 + ReceivedV6) > recvBefore;
                    bool replied = RepliesSent > sentBefore;
                    results.Add(new PathProbe(label, false, sawIt
                        ? (replied
                            ? "resolver RECEIVED this query and SENT a reply, but the reply never " +
                              "arrived - the return path is being dropped"
                            : "resolver received this query but sent no reply")
                        : "resolver never received the query - it is being dropped on the way in"));
                    continue;
                }
                sw.Stop();

                if (reply.Length < 12)
                {
                    results.Add(new PathProbe(label, false, $"malformed reply ({reply.Length} bytes)"));
                    continue;
                }
                ushort gotId = (ushort)((reply[0] << 8) | reply[1]);
                int rcode = DnsMessage.Rcode(reply);
                int answers = DnsMessage.AnswerCount(reply);

                if (gotId != 0x4747)
                {
                    results.Add(new PathProbe(label, false,
                        $"reply id {gotId:X4} does not match query id 4747 - Windows would discard this"));
                    continue;
                }
                results.Add(new PathProbe(label, rcode == 0 && answers > 0,
                    $"rcode={rcode}, answers={answers}, {sw.ElapsedMilliseconds} ms"));
            }
            catch (Exception ex)
            {
                results.Add(new PathProbe(label, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return results;
    }


    /// <summary>
    /// Sends one reply, recording whether it actually left. A reply that fails
    /// to send is invisible to every other counter: the query is logged as
    /// answered, the client sees nothing, and retries - which looks exactly
    /// like a healthy resolver serving a broken machine.
    /// </summary>
    private async Task SendReplyAsync(UdpClient listener, byte[] payload, IPEndPoint to)
    {
        try
        {
            int sent = await listener.SendAsync(payload, payload.Length, to);
            if (sent == payload.Length) Interlocked.Increment(ref _sendOk);
            else
            {
                Interlocked.Increment(ref _sendFail);
                _lastSendError = $"short send: {sent}/{payload.Length} bytes to {to}";
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _sendFail);
            _lastSendError = $"{ex.GetType().Name}: {ex.Message} (to {to})";
        }
    }

    private async Task Loop(UdpClient listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult req;
            try { req = await listener.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }
            if (req.RemoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                Interlocked.Increment(ref _recvV6);
            else
                Interlocked.Increment(ref _recvV4);
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
                await SendReplyAsync(listener, nx, req.RemoteEndPoint);
                Interlocked.Increment(ref _blocked);
                Emit(name, qtype, DnsAction.Blocked);
                return;
            }

            // 2) cache (keyed by name + type; id rewritten per requester)
            string key = name.ToLowerInvariant() + "|" + qtype;
            if (_cache.TryGetValue(key, out var hit) && hit.Exp > DateTime.UtcNow)
            {
                byte[] cached = DnsMessage.WithId(hit.Resp, id);
                await SendReplyAsync(listener, cached, req.RemoteEndPoint);
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
            // 3b) CNAME-cloaking defense. A tracker can dodge a domain blocklist
            // by having a clean first-party name alias to it, so check every hop
            // of the returned chain too. The lookup had to complete to reveal the
            // chain, but the answer is withheld - the app never learns the
            // tracker's address and never connects to it.
            if (_blockCloaked)
            {
                foreach (string hop in DnsMessage.ExtractCnames(answer))
                {
                    if (!IsBlocked(hop)) continue;
                    byte[] nxc = DnsMessage.BuildNxDomain(query);
                    await SendReplyAsync(listener, nxc, req.RemoteEndPoint);
                    Interlocked.Increment(ref _blocked);
                    Interlocked.Increment(ref _cloaked);
                    LastCloak = $"{name} -> {hop}";
                    Emit(name, qtype, DnsAction.Cloaked);
                    return;   // deliberately not cached: never serve a cloaked answer
                }
            }

            await SendReplyAsync(listener, answer, req.RemoteEndPoint);

            // Only remember genuine answers. A failure response must never enter
            // the cache: it would be replayed to every retry for the lifetime of
            // the entry, and clients retry hard on failure - so a single blip
            // turns into a self-sustaining outage for that name.
            if (DnsMessage.IsCacheable(answer))
            {
                int ttl = DnsMessage.GetMinTtl(answer);
                // "This name does not exist" is a real answer and worth caching,
                // but only briefly - names appear, and a stale NXDOMAIN looks
                // exactly like a broken internet.
                if (DnsMessage.Rcode(answer) == DnsMessage.RcodeNameError)
                    ttl = Math.Min(ttl, NegativeTtlSeconds);
                _cache[key] = ((byte[])answer.Clone(), DateTime.UtcNow.AddSeconds(ttl));
            }
            TrackResolvedIps(answer, name);
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

            // Nothing on this PC depends on GunWall for name resolution unless
            // the user pointed it here deliberately, so fail-closed can mean
            // exactly that: a failed encrypted lookup fails, rather than
            // silently going out in plaintext.
            if (_dohFallback) return await ForwardPlainAsync(query, ct);
            return null;
        }
        return await ForwardPlainAsync(query, ct);
    }

    /// <summary>
    /// RFC 8484 DoH: POST the raw DNS wire query as application/dns-message and
    /// read the wire response back. Never throws; null means "failed", which
    /// the caller turns into either fallback or an error per policy.
    /// </summary>
    private const int PerQueryTimeoutMs = 4000;

    private async Task<byte[]?> ForwardDohAsync(byte[] query, CancellationToken ct)
    {
        var http = _http;
        if (http == null) return null;

        // One quick retry on the SAME warm client. A first attempt can lose the
        // race to open a cold TLS connection; the second reuses the now-open
        // pooled connection and almost always succeeds. Each attempt is bounded
        // by its own linked token, so a slow endpoint fails that one request
        // rather than stalling or aborting the shared client.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var perQuery = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perQuery.CancelAfter(PerQueryTimeoutMs);
            try
            {
                using var content = new ByteArrayContent(query);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
                using var resp = await http.PostAsync(_dohUrl, content, perQuery.Token);
                if (!resp.IsSuccessStatusCode) return null;   // a real HTTP error won't fix on retry

                byte[] body = await resp.Content.ReadAsByteArrayAsync(perQuery.Token);
                if (body.Length < 12) return null;

                // HTTP 200 does NOT mean the lookup succeeded. A SERVFAIL or
                // REFUSED arrives as a perfectly valid HTTP response carrying a
                // DNS failure. Treating that as success meant the failure was
                // handed to the client, counted as "ok", and cached - so one
                // upstream hiccup became a lasting outage for that name.
                if (!DnsMessage.IsAuthoritativeResult(body))
                {
                    Interlocked.Increment(ref _upstreamRefused);
                    if (attempt == 0) continue;   // a retry often lands on a healthy node
                    return null;                  // let the caller fall back
                }
                return body;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;                                   // resolver is stopping
            }
            catch when (attempt == 0)
            {
                // First attempt failed (cold connection, transient reset) - loop
                // once more on the warm pool before giving up.
            }
            catch
            {
                return null;
            }
        }
        return null;
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

            byte[] body = recv.Result.Buffer;
            // Same rule as the encrypted path: a SERVFAIL is not an answer.
            if (!DnsMessage.IsAuthoritativeResult(body))
            {
                Interlocked.Increment(ref _upstreamRefused);
                return null;
            }
            return body;
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

    private void TrackResolvedIps(byte[] answer, string queriedName)
    {
        var ips = DnsMessage.ExtractARecords(answer);
        if (ips.Count == 0) return;
        string name = (queriedName ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        lock (_resolvedLock)
        {
            if (_resolvedV4.Count + ips.Count > MaxResolvedIps) _resolvedV4.Clear();
            // Last writer wins: a CDN address reused by another host should carry
            // the most recent name, which is the one the app just asked for.
            foreach (uint ip in ips) _resolvedV4[ip] = name;
        }
        // Also publish to the shared memory, so a lookup answered here counts
        // the same as one merely observed - domain rules read one source.
        DnsObservations.Record(name, ips, null, DnsObservations.Source.Resolver);
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
        lock (_resolvedLock) return _resolvedV4.ContainsKey(v);
    }

    /// <summary>
    /// The domain this address was resolved from, or "" if this resolver never
    /// handed it out. Lets a rule about a NAME be enforced against a connection
    /// that only carries an ADDRESS.
    /// </summary>
    public string DomainForIp(string ipv4)
    {
        if (!System.Net.IPAddress.TryParse(ipv4, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return "";
        var b = ip.GetAddressBytes();
        uint v = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        lock (_resolvedLock) return _resolvedV4.TryGetValue(v, out var name) ? name : "";
    }

    public int ResolvedIpCount { get { lock (_resolvedLock) return _resolvedV4.Count; } }
}
