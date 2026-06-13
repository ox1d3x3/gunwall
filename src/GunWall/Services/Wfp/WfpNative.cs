using System.Runtime.InteropServices;

namespace GunWall.Services.Wfp;

// =============================================================================
//  WfpNative.cs
//  Low-level P/Invoke bindings for the Windows Filtering Platform (fwpuclnt.dll).
//
//  This is the most delicate file in the project: several WFP structs contain
//  C unions, which we collapse to their largest member. The layouts below match
//  the x64 ABI. If you hit a marshalling AccessViolation at runtime, this file
//  is the first place to inspect (check struct field order and alignment).
//
//  References:
//    https://learn.microsoft.com/windows/win32/api/fwpmu/
//    https://learn.microsoft.com/windows/win32/api/fwptypes/
// =============================================================================

internal static class WfpNative
{
    internal const string FwpuclntDll = "fwpuclnt.dll";

    // ----- Error / auth constants -------------------------------------------
    internal const uint ERROR_SUCCESS = 0;
    internal const uint RPC_C_AUTHN_WINNT = 10;

    // ----- FWP_DATA_TYPE (subset we use) ------------------------------------
    internal const uint FWP_EMPTY = 0;
    internal const uint FWP_UINT8 = 1;
    internal const uint FWP_UINT16 = 2;
    internal const uint FWP_UINT32 = 3;
    internal const uint FWP_UINT64 = 4;
    internal const uint FWP_V4_ADDR_MASK = 0x100;  // FWP_V4_ADDR_AND_MASK*
    internal const uint FWP_V6_ADDR_MASK = 0x101;  // FWP_V6_ADDR_AND_MASK*
    internal const uint FWP_BYTE_BLOB_TYPE = 12;

    // ----- Filter flags -----------------------------------------------------
    internal const uint FWPM_FILTER_FLAG_PERSISTENT = 0x00000001;
    internal const uint FWPM_FILTER_FLAG_BOOTTIME = 0x00000002;
    internal const uint FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT = 0x00000008;
    internal const uint FWPM_FILTER_FLAG_INDEXED = 0x00000020;

    // ----- Weight hierarchy (UINT8), highest wins ---------------------------
    internal const byte FW_WEIGHT_HIGHEST_IMPORTANT = 0x0F; // infrastructure permits
    internal const byte FW_WEIGHT_HIGHEST = 0x0E;
    internal const byte FW_WEIGHT_RULE_USER_BLOCK = 0x0C;   // explicit user block
    internal const byte FW_WEIGHT_RULE_USER = 0x0B;         // explicit user allow
    internal const byte FW_WEIGHT_APP = 0x09;               // per-app rules
    internal const byte FW_WEIGHT_LOWEST = 0x08;            // block-all default

    // ----- FWP_MATCH_TYPE ---------------------------------------------------
    internal const uint FWP_MATCH_EQUAL = 0;
    internal const uint FWP_MATCH_FLAGS_ALL_SET = 6;

    // ----- Condition flag bits ------------------------------------------------
    internal const uint FWP_CONDITION_FLAG_IS_LOOPBACK = 0x00000001;

    // ----- FWP_ACTION_TYPE --------------------------------------------------
    internal const uint FWP_ACTION_FLAG_TERMINATING = 0x00001000;
    internal const uint FWP_ACTION_BLOCK = 0x00000001 | FWP_ACTION_FLAG_TERMINATING;  // 0x1001
    internal const uint FWP_ACTION_PERMIT = 0x00000002 | FWP_ACTION_FLAG_TERMINATING; // 0x1002

    // ----- Persistence flags ------------------------------------------------
    internal const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0x00000001;

    // ----- Well-known layer GUIDs -------------------------------------------
    // Outbound connection authorization
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 =
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 =
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    // Inbound connection acceptance
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 =
        new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 =
        new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    // ----- Condition field GUIDs --------------------------------------------
    internal static readonly Guid FWPM_CONDITION_ALE_APP_ID =
        new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    internal static readonly Guid FWPM_CONDITION_FLAGS =
        new("632ce23b-5167-435c-86d7-e903684aa80c");
    internal static readonly Guid FWPM_CONDITION_IP_REMOTE_ADDRESS =
        new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    internal static readonly Guid FWPM_CONDITION_IP_PROTOCOL =
        new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    internal static readonly Guid FWPM_CONDITION_ICMP_TYPE =
        new("b9f4e088-cb98-4efb-a2c7-ad07332643db");
    internal static readonly Guid FWPM_CONDITION_IP_REMOTE_PORT =
        new("c35a604d-d22b-48b1-a8dc-1377b9824a35");

    // Address-and-mask structures for FWPM_CONDITION_IP_REMOTE_ADDRESS.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_V4_ADDR_AND_MASK
    {
        public uint addr;
        public uint mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_V6_ADDR_AND_MASK
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] addr;
        public byte prefixLength;
    }

    // =========================================================================
    //  Structures
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_DISPLAY_DATA0
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? description;
    }

    // FWP_VALUE0 { FWP_DATA_TYPE type; union { ... 8-byte ... } value; }
    // Natural alignment places 'value' at offset 8 on x64 (size = 16).
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_VALUE0
    {
        public uint type;
        public ulong value; // inline scalar OR pointer to blob, depending on 'type'
    }

    // FWP_CONDITION_VALUE0 has the same shape as FWP_VALUE0.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_CONDITION_VALUE0
    {
        public uint type;
        public ulong value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_FILTER_CONDITION0
    {
        public Guid fieldKey;
        public uint matchType;
        public FWP_CONDITION_VALUE0 conditionValue;
    }

    // FWPM_ACTION0 { FWP_ACTION_TYPE type; union { GUID filterType; GUID calloutKey; }; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_ACTION0
    {
        public uint type;
        public Guid guid; // unused for block/permit; left zeroed
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public ushort flags;
        public IntPtr providerKey;     // GUID* (null here)
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    // FWPM_FILTER0 — fields in exact C declaration order so natural alignment
    // reproduces the native layout. The two C unions are collapsed:
    //   weight/effectiveWeight -> FWP_VALUE0
    //   rawContext/providerContextKey -> Guid (the larger member)
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_FILTER0
    {
        public Guid filterKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;             // GUID*
        public FWP_BYTE_BLOB providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FWP_VALUE0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition;         // FWPM_FILTER_CONDITION0*
        public FWPM_ACTION0 action;
        public Guid providerContextKey;        // union { UINT64 rawContext; GUID ...; }
        public IntPtr reserved;                // GUID*
        public ulong filterId;
        public FWP_VALUE0 effectiveWeight;
    }

    // =========================================================================
    //  Functions
    // =========================================================================

    [DllImport(FwpuclntDll, CharSet = CharSet.Unicode)]
    internal static extern uint FwpmEngineOpen0(
        string? serverName,
        uint authnService,
        IntPtr authIdentity,
        IntPtr session,
        out IntPtr engineHandle);

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmSubLayerAdd0(
        IntPtr engineHandle,
        ref FWPM_SUBLAYER0 subLayer,
        IntPtr sd);

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmSubLayerDeleteByKey0(
        IntPtr engineHandle,
        ref Guid key);

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmFilterAdd0(
        IntPtr engineHandle,
        ref FWPM_FILTER0 filter,
        IntPtr sd,
        out ulong id);

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmFilterDeleteById0(
        IntPtr engineHandle,
        ulong id);

    [DllImport(FwpuclntDll, CharSet = CharSet.Unicode)]
    internal static extern uint FwpmGetAppIdFromFileName0(
        string fileName,
        out IntPtr appId); // FWP_BYTE_BLOB**

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmFreeMemory0(ref IntPtr p);

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport(FwpuclntDll)]
    internal static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    // Maps a drive letter (e.g. "C:") to its NT device path (e.g.
    // "\Device\HarddiskVolume3"). Used to build a WFP app ID without opening
    // the target file — essential for self-protected processes (antivirus).
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint QueryDosDeviceW(
        string lpDeviceName,
        System.Text.StringBuilder lpTargetPath,
        uint ucchMax);
}
