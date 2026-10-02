// GeoIP load benchmark. Compiles GunWall's real GeoIpService.cs, loads the
// generated tables exactly as GunWall does, and reports memory, time and a hash
// of 250,000 lookups. Run it before and after a change to the loader: the
// numbers show the cost, and an identical hash shows the answers did not move.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GunWall.Services;

if (args.Length < 4) { Console.WriteLine("usage: v4.tsv v6.tsv ips.txt out.txt"); return; }
static double WS() { var p = Process.GetCurrentProcess(); p.Refresh(); return p.WorkingSet64 / 1048576.0; }

long before = GC.GetTotalMemory(true);
long allocBefore = GC.GetTotalAllocatedBytes(true);
var sw = Stopwatch.StartNew();
var geo = new GeoIpService();
geo.LoadFromFile(args[0]);
geo.LoadV6FromFile(args[1]);
sw.Stop();
double wsAfterLoad = WS();
long allocated = GC.GetTotalAllocatedBytes(true) - allocBefore;
GeoIpService.ReturnFreedMemory();
double wsReturned = WS();
long retained = GC.GetTotalMemory(true) - before;

var sb = new StringBuilder();
foreach (var ip in File.ReadLines(args[2]))
{
    var g = geo.Lookup(ip);
    sb.Append(g.Country).Append('\t').Append(g.Asn).Append('\t').Append(g.Owner).Append('\n');
}
File.WriteAllText(args[3], sb.ToString());
string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];

Console.WriteLine($"load_ms={sw.ElapsedMilliseconds} retained_MB={retained / 1048576.0:F1} " +
                  $"allocated_MB={allocated / 1048576.0:F1} ws_after_load_MB={wsAfterLoad:F0} " +
                  $"ws_after_return_MB={wsReturned:F0} lookups_sha256={hash}");
GC.KeepAlive(geo);
