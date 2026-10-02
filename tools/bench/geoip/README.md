# GeoIP load benchmark

Compiles `src/GunWall/Services/GeoIpService.cs` unchanged into a console program,
loads tables shaped like the real iptoasn files, and reports load time, memory and
a hash of 250,000 lookups.

```
python3 gen.py                         # data/v4.tsv, data/v6.tsv, data/ips.txt
dotnet run -c Release -- data/v4.tsv data/v6.tsv data/ips.txt out.txt
```

`gen.py` is deterministic: 538,417 IPv4 and 182,861 IPv6 ranges, owners tied to
75,000 ASNs with a long-tailed distribution, 238 countries, and the
`None / Not routed` rows the real files contain. It needs no network access, and
neither does the build - `nuget.config` clears all package sources, since a
console program needs none.

To prove a loader change is behaviour-preserving, run the benchmark against the
previous and the new source on the same generated data and compare `out.txt` -
the files must be identical. The hash is a quick check of the same thing.

## 0.99.151

Measured with the .NET 8.0.31 runtime, median of five interleaved runs:

| | 0.99.150 | 0.99.151 |
|---|---|---|
| Load time | 1362 ms | 1110 ms |
| Retained after load | 106.6 MB | 33.6 MB |
| Peak working set | 363 MB | 205 MB |
| Working set once settled | 364 MB | 116 MB |
| Garbage allocated by the load | 728 MB | 518 MB |
| Lookup hash | `BF9B055E54EA0950` | `BF9B055E54EA0950` |
