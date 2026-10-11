using System.Reflection;
using System.Text.Json;

namespace GunWall.Services.Remote;

/// <summary>
/// Every <see cref="IEngine"/> member the pipe can carry, with its access tag
/// (stage 4a). Built once by reflection over the interface, so the allowlist IS
/// the interface: a member that is not on IEngine cannot be called, whatever a
/// client sends.
/// </summary>
public sealed class EngineCatalog
{

    public sealed record Entry(string Name, Access Access, MethodInfo? Method, PropertyInfo? Property);

    private readonly Dictionary<string, Entry> _byName = new(StringComparer.Ordinal);

    /// <summary>Read properties sent in every state message: small values the
    /// window reads constantly (StrictMode is read 22 times, many every second).
    /// Lists, and the two below, are fetched when asked for instead.</summary>
    public IReadOnlyList<PropertyInfo> Mirrored { get; }

    /// <summary>Scalars that are not broadcast: a fingerprint that hashes every
    /// rule, and the VirusTotal key, which crosses only when asked for.</summary>
    public static readonly string[] OnDemand = { "VirusTotalApiKey" };   // RulesetFingerprint: ReadProfile since 0.99.221

    /// <summary>After <see cref="OnDemand"/>: static fields start in order.</summary>
    public static readonly EngineCatalog Instance = new();

    private EngineCatalog()
    {
        var mirrored = new List<PropertyInfo>();
        foreach (var m in typeof(IEngine).GetMembers())
        {
            if (m is MethodInfo { IsSpecialName: true }) continue;   // property and event accessors
            var tag = m.GetCustomAttribute<EngineAccessAttribute>()?.Access
                      ?? throw new InvalidOperationException($"IEngine.{m.Name} has no access tag");
            switch (m)
            {
                case PropertyInfo p:
                    _byName.Add(p.Name, new Entry(p.Name, tag, null, p));
                    if (tag == Access.Read && IsScalar(p.PropertyType) && Array.IndexOf(OnDemand, p.Name) < 0)
                        mirrored.Add(p);
                    break;
                case MethodInfo mi when !mi.IsSpecialName:
                    // One method per name: overloads would make the call ambiguous.
                    _byName.Add(mi.Name, new Entry(mi.Name, tag, mi, null));
                    break;
                case EventInfo e:
                    _byName.Add(e.Name, new Entry(e.Name, tag, null, null));
                    break;
            }
        }
        Mirrored = mirrored;
    }

    public bool TryGet(string name, out Entry entry) => _byName.TryGetValue(name, out entry!);

    public IEnumerable<Entry> All => _byName.Values;

    public static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(TimeSpan);
    }

    /// <summary>The state message: every mirrored property, read now. Runs on the
    /// engine thread.</summary>
    public Dictionary<string, JsonElement> CaptureState(IEngine engine)
    {
        var d = new Dictionary<string, JsonElement>(Mirrored.Count, StringComparer.Ordinal);
        foreach (var p in Mirrored)
        {
            try { d[p.Name] = EngineWire.ToElement(p.GetValue(engine), p.PropertyType); }
            catch (Exception ex) { DiagnosticLog.LogException("EngineState/" + p.Name, ex); }
        }
        return d;
    }
}
