using System.IO;
using System.Runtime.InteropServices;
using static GunWall.Services.Wfp.WfpNative;

namespace GunWall.Services.Wfp;

/// <summary>
/// Thrown when an underlying WFP API call returns a non-zero status code.
/// </summary>
public sealed class WfpException : Exception
{
    public uint Code { get; }
    public WfpException(string operation, uint code)
        : base($"WFP operation '{operation}' failed with code 0x{code:X8} ({code}).")
    {
        Code = code;
    }
}

/// <summary>
/// Managed facade over the Windows Filtering Platform.
///
/// Design notes:
///  - We operate a single, persistent sublayer owned by GunWall.
///  - Default policy is ALLOW-ALL (blacklist model): only apps the user
///    explicitly blocks receive BLOCK filters. This avoids accidentally
///    cutting off the user's internet — friendlier than a whitelist default.
///  - Lockdown mode adds higher-weight, condition-less BLOCK filters that
///    override per-app rules until disabled.
///  - All filters are marked PERSISTENT so they survive app close and reboot,
///    as an independent filtering layer. Removing them requires either deleting by the
///    stored filter IDs or tearing down the sublayer.
/// </summary>
public sealed class WfpEngine : IDisposable
{
    // Stable identity for GunWall's sublayer. Generated once for this app;
    // never reuse another product's GUID.
    private static readonly Guid SublayerKey = new("8f1d2b40-7c3e-4a51-9d6f-2a8c5e1b9f00");

    private const ushort SublayerWeight = 0x8000; // mid-high so we sit above default
    // Weights map to the documented WFP hierarchy: highest wins. Infrastructure
    // permits MUST outrank the block-all default, or DNS/DHCP/IPv6 break.
    private const byte InfraPermitWeight = FW_WEIGHT_HIGHEST_IMPORTANT; // 0x0F
    private const byte AppPermitWeight = FW_WEIGHT_RULE_USER;           // 0x0B
    private const byte AppBlockWeight = FW_WEIGHT_RULE_USER_BLOCK;      // 0x0C (beats permit)
    private const byte StrictBaseWeight = FW_WEIGHT_LOWEST;            // 0x08 (block-all default)
    private const byte LockdownWeight = FW_WEIGHT_HIGHEST;            // 0x0E (beats app rules)

    private IntPtr _engine = IntPtr.Zero;
    private bool _initialized;

    public bool IsInitialized => _initialized;

    /// <summary>The open WFP engine handle (for net-event subscription). Zero if not initialized.</summary>
    public IntPtr EngineHandle => _engine;

    /// <summary>
    /// Opens the WFP engine and ensures our persistent sublayer exists.
    /// Safe to call multiple times.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        uint r = FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out _engine);
        if (r != ERROR_SUCCESS) throw new WfpException(nameof(FwpmEngineOpen0), r);

        EnsureSublayer();
        _initialized = true;
    }

    private void EnsureSublayer()
    {
        var sublayer = new FWPM_SUBLAYER0
        {
            subLayerKey = SublayerKey,
            displayData = new FWPM_DISPLAY_DATA0
            {
                name = "GunWall",
                description = "GunWall firewall sublayer"
            },
            flags = (ushort)FWPM_SUBLAYER_FLAG_PERSISTENT,
            providerKey = IntPtr.Zero,
            providerData = default,
            weight = SublayerWeight
        };

        uint r = FwpmSubLayerAdd0(_engine, ref sublayer, IntPtr.Zero);
        // FWP_E_ALREADY_EXISTS (0x80320009) is fine — sublayer persists across runs.
        const uint FWP_E_ALREADY_EXISTS = 0x80320009;
        if (r != ERROR_SUCCESS && r != FWP_E_ALREADY_EXISTS)
            throw new WfpException(nameof(FwpmSubLayerAdd0), r);
    }

    /// <summary>
    /// Blocks all network traffic (in and out, IPv4 and IPv6) for the given
    /// executable. Returns the list of created filter IDs so the caller can
    /// remove them later. The app path must be a normal file path
    /// (e.g. C:\Program Files\App\app.exe).
    /// </summary>
    public List<ulong> BlockApplication(string exePath) =>
        AddAppFilters(exePath, FWP_ACTION_BLOCK, AppBlockWeight, "Block");

    /// <summary>
    /// Permits all traffic for an executable (used by strict mode to punch a
    /// hole through the base block). Returns the created filter IDs.
    /// </summary>
    public List<ulong> PermitApplication(string exePath) =>
        AddAppFilters(exePath, FWP_ACTION_PERMIT, AppPermitWeight, "Allow");

    private List<ulong> AddAppFilters(string exePath, uint action, byte weight, string verb)
    {
        EnsureReady();
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Executable path is required.", nameof(exePath));

        IntPtr appIdPtr = GetAppId(exePath, out FWP_BYTE_BLOB blob);
        try
        {
            var ids = new List<ulong>(4);
            foreach (var layer in AllAppLayers())
                ids.Add(AddAppFilter(layer, blob, weight, action,
                    $"{verb} {Path.GetFileName(exePath)}"));
            return ids;
        }
        finally
        {
            FreeAppId(appIdPtr);
        }
    }

    // Infrastructure ranges that MUST stay reachable for the network to work:
    // loopback, DHCP, link-local, private LANs, CGNAT, multicast/broadcast.
    // Without permitting these, "block everything" also blocks DNS, DHCP renewal
    // and local network discovery — i.e. it kills the internet. (IPv4 here;
    // loopback + IPv6 link-local are handled by the loopback flag + ICMPv6.)
    private static readonly (string Cidr, byte Prefix)[] InfraV4 =
    {
        ("0.0.0.0", 8),       // this network / DHCP
        ("10.0.0.0", 8),      // private
        ("100.64.0.0", 10),   // CGNAT
        ("127.0.0.0", 8),     // loopback
        ("169.254.0.0", 16),  // link-local
        ("172.16.0.0", 12),   // private
        ("192.168.0.0", 16),  // private
        ("224.0.0.0", 4),     // multicast
        ("240.0.0.0", 4),     // reserved/broadcast
        ("255.255.255.255", 32),
    };

    /// <summary>
    /// Engages strict (whitelist) mode the correct way, inside a single WFP
    /// transaction so the whole rule set is applied atomically (all-or-nothing,
    /// no half-applied state that could wedge the network):
    ///   1. Permit loopback (flag-based) at highest importance.
    ///   2. Permit core infrastructure ranges (DHCP/LAN/multicast) so the
    ///      network keeps functioning.
    ///   3. Permit DNS (port 53) so name resolution works for allowed apps.
    ///   4. Add a block-all default at the LOWEST weight.
    /// Each individual permit is best-effort: if one optional permit can't be
    /// added on a given OS, we log and continue rather than abort the takeover.
    /// The block-all is mandatory — if it fails, the whole transaction aborts.
    /// Returns every created filter ID for clean teardown.
    /// </summary>
    public List<ulong> EngageStrictMode()
    {
        EnsureReady();
        var ids = new List<ulong>(16);

        uint tb = FwpmTransactionBegin0(_engine, 0);
        if (tb != ERROR_SUCCESS) throw new WfpException(nameof(FwpmTransactionBegin0), tb);

        try
        {
            // 1) Loopback always permitted (covers localhost IPC). This uses the
            //    loopback condition FLAG, which marshals reliably on all builds.
            foreach (var layer in AllAppLayers())
                TryAdd(ids, () => AddLoopbackPermitFilter(layer));

            // 2) Block-all default at lowest weight (MANDATORY). Every permit
            //    above and every per-app permit added later outranks this.
            foreach (var layer in AllAppLayers())
                ids.Add(AddGlobalBlockFilter(layer, StrictBaseWeight, "GunWall Block Default"));

            uint tc = FwpmTransactionCommit0(_engine);
            if (tc != ERROR_SUCCESS) throw new WfpException(nameof(FwpmTransactionCommit0), tc);
        }
        catch
        {
            FwpmTransactionAbort0(_engine);
            throw;
        }

        return ids;
    }

    /// <summary>
    /// Returns the system executables that must keep network access for the
    /// connection itself to function (DNS, DHCP, etc.). These are permitted by
    /// app-ID, which marshals reliably, instead of by fragile address/port
    /// filters. The caller permits each one after engaging strict mode.
    /// </summary>
    public static IEnumerable<string> CoreSystemApps()
    {
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // svchost hosts the DNS client and DHCP services; lsass/services are core.
        foreach (var name in new[] { "svchost.exe", "services.exe", "lsass.exe", "dnscache.exe" })
        {
            string p = System.IO.Path.Combine(sys32, name);
            if (System.IO.File.Exists(p)) yield return p;
        }
    }

    /// <summary>
    /// Adds an optional filter, swallowing failures so one unsupported permit
    /// doesn't abort the whole takeover.
    /// </summary>
    private static void TryAdd(List<ulong> ids, Func<ulong> add)
    {
        try { ids.Add(add()); }
        catch (WfpException ex)
        {
            System.Diagnostics.Debug.WriteLine($"optional filter skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Engages lockdown: condition-less BLOCK filters that outrank per-app rules
    /// (but NOT the infrastructure permits, so the machine doesn't hard-lock the
    /// local stack). Returns their filter IDs.
    /// </summary>
    public List<ulong> EngageLockdown()
    {
        EnsureReady();
        var ids = new List<ulong>(4);
        foreach (var layer in AllAppLayers())
            ids.Add(AddGlobalBlockFilter(layer, LockdownWeight, "GunWall Lockdown"));
        return ids;
    }

    /// <summary>Removes a set of previously created filters by ID.</summary>
    public void RemoveFilters(IEnumerable<ulong> filterIds)
    {
        EnsureReady();
        foreach (var id in filterIds)
        {
            // Ignore "not found" so removal is idempotent across restarts.
            uint r = FwpmFilterDeleteById0(_engine, id);
            const uint FWP_E_FILTER_NOT_FOUND = 0x80320003;
            if (r != ERROR_SUCCESS && r != FWP_E_FILTER_NOT_FOUND)
                throw new WfpException(nameof(FwpmFilterDeleteById0), r);
        }
    }

    /// <summary>
    /// Tears down GunWall's entire sublayer and every filter inside it.
    /// This is the "Disable all filtering" / clean-uninstall action.
    /// </summary>
    public void RemoveAllFiltering()
    {
        EnsureReady();
        var key = SublayerKey;
        uint r = FwpmSubLayerDeleteByKey0(_engine, ref key);
        const uint FWP_E_SUBLAYER_NOT_FOUND = 0x80320007;
        if (r != ERROR_SUCCESS && r != FWP_E_SUBLAYER_NOT_FOUND)
            throw new WfpException(nameof(FwpmSubLayerDeleteByKey0), r);
        // Recreate an empty sublayer so the engine stays usable.
        EnsureSublayer();
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<Guid> AllAppLayers()
    {
        yield return FWPM_LAYER_ALE_AUTH_CONNECT_V4;
        yield return FWPM_LAYER_ALE_AUTH_CONNECT_V6;
        yield return FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4;
        yield return FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6;
    }

    /// <summary>
    /// Produces a WFP "app ID" blob for an executable. The blob is the file's
    /// NT device path as a lowercase, null-terminated wide string.
    ///
    /// Normally we let the OS build it (FwpmGetAppIdFromFileName0). But that API
    /// opens the file to canonicalize it, which self-protected processes such as
    /// antivirus (e.g. avp.exe) deny with ERROR_ACCESS_DENIED (5). In that case
    /// we build the blob ourselves from the path string — no file handle needed.
    ///
    /// The returned pointer is always owned by us (AllocHGlobal); callers free it
    /// with Marshal.FreeHGlobal, NOT FwpmFreeMemory0.
    /// </summary>
    private IntPtr GetAppId(string exePath, out FWP_BYTE_BLOB blob)
    {
        uint r = FwpmGetAppIdFromFileName0(exePath, out IntPtr apiPtr);
        if (r == ERROR_SUCCESS)
        {
            try
            {
                // Copy the OS-provided blob into memory we own, then free theirs.
                var apiBlob = Marshal.PtrToStructure<FWP_BYTE_BLOB>(apiPtr);
                byte[] data = new byte[apiBlob.size];
                Marshal.Copy(apiBlob.data, data, 0, (int)apiBlob.size);
                return AllocBlob(data, out blob);
            }
            finally { FwpmFreeMemory0(ref apiPtr); }
        }

        const uint ERROR_ACCESS_DENIED = 5;
        if (r == ERROR_ACCESS_DENIED)
        {
            // Self-protected process: build the app ID manually from the path.
            string ntPath = DosToNtPath(exePath);
            byte[] data = System.Text.Encoding.Unicode.GetBytes(ntPath + '\0');
            return AllocBlob(data, out blob);
        }

        throw new WfpException(nameof(FwpmGetAppIdFromFileName0), r);
    }

    /// <summary>Allocates an FWP_BYTE_BLOB in unmanaged memory holding the bytes.</summary>
    private static IntPtr AllocBlob(byte[] data, out FWP_BYTE_BLOB blob)
    {
        IntPtr dataPtr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, dataPtr, data.Length);
        blob = new FWP_BYTE_BLOB { size = (uint)data.Length, data = dataPtr };

        // Store the blob struct itself in unmanaged memory and return that ptr,
        // so the caller's free releases everything (struct ptr + data ptr).
        IntPtr blobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FWP_BYTE_BLOB>());
        Marshal.StructureToPtr(blob, blobPtr, false);
        return blobPtr;
    }

    /// <summary>
    /// Converts a DOS path (C:\dir\file.exe) to its NT device path
    /// (\device\harddiskvolumeN\dir\file.exe), lowercased, without opening the
    /// file. Falls back to the original path if the drive can't be mapped.
    /// </summary>
    private static string DosToNtPath(string dosPath)
    {
        try
        {
            if (dosPath.Length >= 2 && dosPath[1] == ':')
            {
                string drive = dosPath[..2]; // "C:"
                var sb = new System.Text.StringBuilder(1024);
                uint n = QueryDosDeviceW(drive, sb, (uint)sb.Capacity);
                if (n != 0)
                {
                    string device = sb.ToString();          // \Device\HarddiskVolume3
                    string rest = dosPath[2..];             // \dir\file.exe
                    return (device + rest).ToLowerInvariant();
                }
            }
        }
        catch { /* fall through */ }
        return dosPath.ToLowerInvariant();
    }

    private void FreeAppId(IntPtr blobPtr)
    {
        if (blobPtr == IntPtr.Zero) return;
        try
        {
            var blob = Marshal.PtrToStructure<FWP_BYTE_BLOB>(blobPtr);
            if (blob.data != IntPtr.Zero) Marshal.FreeHGlobal(blob.data);
        }
        catch { /* best effort */ }
        Marshal.FreeHGlobal(blobPtr);
    }

    private ulong AddLoopbackPermitFilter(Guid layer)
    {
        IntPtr condPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FWPM_FILTER_CONDITION0>());
        try
        {
            var cond = new FWPM_FILTER_CONDITION0
            {
                fieldKey = FWPM_CONDITION_FLAGS,
                matchType = FWP_MATCH_FLAGS_ALL_SET,
                conditionValue = new FWP_CONDITION_VALUE0
                {
                    type = FWP_UINT32,
                    value = FWP_CONDITION_FLAG_IS_LOOPBACK
                }
            };
            Marshal.StructureToPtr(cond, condPtr, false);

            var filter = new FWPM_FILTER0
            {
                layerKey = layer,
                subLayerKey = SublayerKey,
                flags = FWPM_FILTER_FLAG_PERSISTENT,
                weight = new FWP_VALUE0 { type = FWP_UINT8, value = InfraPermitWeight },
                numFilterConditions = 1,
                filterCondition = condPtr,
                action = new FWPM_ACTION0 { type = FWP_ACTION_PERMIT },
                displayData = new FWPM_DISPLAY_DATA0
                {
                    name = "GunWall Loopback Permit",
                    description = "Keeps localhost traffic working in strict mode"
                }
            };

            uint r = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out ulong id);
            if (r != ERROR_SUCCESS) throw new WfpException(nameof(FwpmFilterAdd0), r);
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
        }
    }

    /// <summary>Permits an outbound/inbound IPv4 CIDR range at highest importance.</summary>
    private ulong AddV4RangePermitFilter(Guid layer, string baseAddress, byte prefix)
    {
        var maskStruct = new FWP_V4_ADDR_AND_MASK
        {
            addr = IpToHost(baseAddress),
            mask = prefix == 0 ? 0 : 0xFFFFFFFF << (32 - prefix)
        };
        IntPtr maskPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FWP_V4_ADDR_AND_MASK>());
        IntPtr condPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FWPM_FILTER_CONDITION0>());
        try
        {
            Marshal.StructureToPtr(maskStruct, maskPtr, false);
            var cond = new FWPM_FILTER_CONDITION0
            {
                fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS,
                matchType = FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE0
                {
                    type = FWP_V4_ADDR_MASK,
                    value = (ulong)maskPtr.ToInt64()
                }
            };
            Marshal.StructureToPtr(cond, condPtr, false);

            var filter = new FWPM_FILTER0
            {
                layerKey = layer,
                subLayerKey = SublayerKey,
                flags = FWPM_FILTER_FLAG_PERSISTENT,
                weight = new FWP_VALUE0 { type = FWP_UINT8, value = InfraPermitWeight },
                numFilterConditions = 1,
                filterCondition = condPtr,
                action = new FWPM_ACTION0 { type = FWP_ACTION_PERMIT },
                displayData = new FWPM_DISPLAY_DATA0
                {
                    name = "GunWall Infrastructure",
                    description = $"Permit {baseAddress}/{prefix}"
                }
            };

            uint r = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out ulong id);
            if (r != ERROR_SUCCESS) throw new WfpException(nameof(FwpmFilterAdd0), r);
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
            Marshal.FreeHGlobal(maskPtr);
        }
    }

    /// <summary>Permits a remote port (e.g. DNS 53) at highest importance.</summary>
    private ulong AddRemotePortPermitFilter(Guid layer, ushort port)
    {
        IntPtr condPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FWPM_FILTER_CONDITION0>());
        try
        {
            var cond = new FWPM_FILTER_CONDITION0
            {
                fieldKey = FWPM_CONDITION_IP_REMOTE_PORT,
                matchType = FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE0
                {
                    type = FWP_UINT16,
                    value = port
                }
            };
            Marshal.StructureToPtr(cond, condPtr, false);

            var filter = new FWPM_FILTER0
            {
                layerKey = layer,
                subLayerKey = SublayerKey,
                flags = FWPM_FILTER_FLAG_PERSISTENT,
                weight = new FWP_VALUE0 { type = FWP_UINT8, value = InfraPermitWeight },
                numFilterConditions = 1,
                filterCondition = condPtr,
                action = new FWPM_ACTION0 { type = FWP_ACTION_PERMIT },
                displayData = new FWPM_DISPLAY_DATA0
                {
                    name = "GunWall DNS Permit",
                    description = $"Permit remote port {port}"
                }
            };

            uint r = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out ulong id);
            if (r != ERROR_SUCCESS) throw new WfpException(nameof(FwpmFilterAdd0), r);
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
        }
    }

    /// <summary>Converts a dotted IPv4 string to a host-order uint for WFP.</summary>
    private static uint IpToHost(string ip)
    {
        var parts = ip.Split('.');
        return ((uint)byte.Parse(parts[0]) << 24) |
               ((uint)byte.Parse(parts[1]) << 16) |
               ((uint)byte.Parse(parts[2]) << 8) |
               byte.Parse(parts[3]);
    }

    private ulong AddAppFilter(Guid layer, FWP_BYTE_BLOB appIdBlob, byte weight, uint action, string name)
    {
        // Marshal one FWPM_FILTER_CONDITION0 (APP_ID == app) into native memory.
        // The condition's byteBlob value must point to an FWP_BYTE_BLOB; we copy
        // the blob into unmanaged memory so its lifetime spans the Add call.
        IntPtr blobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FWP_BYTE_BLOB>());
        IntPtr condPtr = Marshal.AllocHGlobal(Marshal.SizeOf<FWPM_FILTER_CONDITION0>());
        try
        {
            Marshal.StructureToPtr(appIdBlob, blobPtr, false);

            var cond = new FWPM_FILTER_CONDITION0
            {
                fieldKey = FWPM_CONDITION_ALE_APP_ID,
                matchType = FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE0
                {
                    type = FWP_BYTE_BLOB_TYPE,
                    value = (ulong)blobPtr.ToInt64()
                }
            };
            Marshal.StructureToPtr(cond, condPtr, false);

            // Block actions must clear the action right so they can't be
            // overridden by lower-priority permits elsewhere in the system.
            uint flags = FWPM_FILTER_FLAG_PERSISTENT;
            if (action == FWP_ACTION_BLOCK) flags |= FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT;

            var filter = new FWPM_FILTER0
            {
                layerKey = layer,
                subLayerKey = SublayerKey,
                flags = flags,
                weight = new FWP_VALUE0 { type = FWP_UINT8, value = weight },
                numFilterConditions = 1,
                filterCondition = condPtr,
                action = new FWPM_ACTION0 { type = action },
                displayData = new FWPM_DISPLAY_DATA0 { name = name, description = name }
            };

            uint r = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out ulong id);
            if (r != ERROR_SUCCESS) throw new WfpException(nameof(FwpmFilterAdd0), r);
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    private ulong AddGlobalBlockFilter(Guid layer, byte weight, string name)
    {
        var filter = new FWPM_FILTER0
        {
            layerKey = layer,
            subLayerKey = SublayerKey,
            flags = FWPM_FILTER_FLAG_PERSISTENT | FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT,
            weight = new FWP_VALUE0 { type = FWP_UINT8, value = weight },
            numFilterConditions = 0,
            filterCondition = IntPtr.Zero,
            action = new FWPM_ACTION0 { type = FWP_ACTION_BLOCK },
            displayData = new FWPM_DISPLAY_DATA0 { name = name, description = name }
        };

        uint r = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out ulong id);
        if (r != ERROR_SUCCESS) throw new WfpException(nameof(FwpmFilterAdd0), r);
        return id;
    }

    private void EnsureReady()
    {
        if (!_initialized) Initialize();
    }

    public void Dispose()
    {
        if (_engine != IntPtr.Zero)
        {
            FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
        _initialized = false;
        GC.SuppressFinalize(this);
    }
}
