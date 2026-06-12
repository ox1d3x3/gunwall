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
    private const byte StrictBaseWeight = 6;      // strict-mode block-everything
    private const byte AppPermitWeight = 8;       // per-app allow (strict mode)
    private const byte LoopbackWeight = 9;        // keep loopback alive in strict
    private const byte AppBlockWeight = 10;       // per-app block beats permits
    private const byte LockdownWeight = 15;       // lockdown beats everything

    private IntPtr _engine = IntPtr.Zero;
    private bool _initialized;

    public bool IsInitialized => _initialized;

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
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Executable not found.", exePath);

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
            FwpmFreeMemory0(ref appIdPtr);
        }
    }

    /// <summary>
    /// Engages strict (whitelist) mode: block everything by default, but keep
    /// loopback traffic alive so local IPC keeps working. Per-app PERMIT
    /// filters (higher weight) punch through; explicit per-app BLOCK filters
    /// (higher still) always win. Returns all created filter IDs.
    /// </summary>
    public List<ulong> EngageStrictMode()
    {
        EnsureReady();
        var ids = new List<ulong>(8);
        foreach (var layer in AllAppLayers())
        {
            ids.Add(AddGlobalBlockFilter(layer, StrictBaseWeight, "GunWall Strict Base Block"));
            ids.Add(AddLoopbackPermitFilter(layer));
        }
        return ids;
    }

    /// <summary>
    /// Engages lockdown: condition-less BLOCK filters on every ALE layer that
    /// outrank per-app rules. Returns their filter IDs.
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

    private IntPtr GetAppId(string exePath, out FWP_BYTE_BLOB blob)
    {
        uint r = FwpmGetAppIdFromFileName0(exePath, out IntPtr appIdPtr);
        if (r != ERROR_SUCCESS) throw new WfpException(nameof(FwpmGetAppIdFromFileName0), r);
        blob = Marshal.PtrToStructure<FWP_BYTE_BLOB>(appIdPtr);
        return appIdPtr;
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
                weight = new FWP_VALUE0 { type = FWP_UINT8, value = LoopbackWeight },
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

            var filter = new FWPM_FILTER0
            {
                layerKey = layer,
                subLayerKey = SublayerKey,
                flags = FWPM_FILTER_FLAG_PERSISTENT,
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
            flags = FWPM_FILTER_FLAG_PERSISTENT,
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
