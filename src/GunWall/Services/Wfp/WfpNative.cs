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
    internal const uint FWP_MATCH_FLAGS_NONE_SET = 8;

    // ----- Condition flag bits ------------------------------------------------
    internal const uint FWP_CONDITION_FLAG_IS_LOOPBACK = 0x00000001;
    internal const uint FWP_CONDITION_FLAG_IS_APPCONTAINER_LOOPBACK = 0x00000400;

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
    // Inbound listen authorization — governs an application's ability to OPEN a
    // listening socket (accept inbound). Blocking here stops a process from
    // listening at all, one step earlier than ALE_AUTH_RECV_ACCEPT. Used by the
    // optional "block listening sockets" hardening preset.
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_LISTEN_V4 =
        new("88bb5dad-76d7-4227-9c71-df0a3ed7be7e");
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_LISTEN_V6 =
        new("7ac9de24-17dd-4814-b4bd-a9fbc95a321b");
    // Transport layers — carry ICMP, raw sockets, and other connectionless
    // traffic that never passes through the ALE_AUTH_CONNECT layer. Filtering
    // here is what makes ping, traceroute, and DNS visible/controllable.
    internal static readonly Guid FWPM_LAYER_OUTBOUND_TRANSPORT_V4 =
        new("09e61aea-d214-46e2-9b21-b26b0b2f28c8");
    internal static readonly Guid FWPM_LAYER_OUTBOUND_TRANSPORT_V6 =
        new("e1735bde-013f-4655-b351-a49e15762df0");
    internal static readonly Guid FWPM_LAYER_INBOUND_TRANSPORT_V4 =
        new("5926dfc8-e3cf-4426-a283-dc393f5d0f9d");
    internal static readonly Guid FWPM_LAYER_INBOUND_TRANSPORT_V6 =
        new("634a869f-fc23-4b90-b0c1-bf620a36ae6f");
    // IP forwarding layers — traffic ROUTED THROUGH this machine rather than
    // sent to or from it. Relevant whenever a VM bridge, ICS, or a mesh VPN
    // could turn the PC into a transit hop for someone else's traffic.
    internal static readonly Guid FWPM_LAYER_IPFORWARD_V4 =
        new("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4");
    internal static readonly Guid FWPM_LAYER_IPFORWARD_V6 =
        new("7b964818-19c7-493a-b71f-832c3684d28c");
    // Resource assignment — governs bind(), i.e. an application claiming a local
    // port at all. Blocking here stops a process becoming a server one step
    // earlier than ALE_AUTH_LISTEN, and unlike LISTEN it also covers UDP.
    internal static readonly Guid FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4 =
        new("1247d66d-0b60-4a15-8d44-7155d0f53a0c");
    internal static readonly Guid FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6 =
        new("55a650e1-5f0a-4eca-a653-88f53b26aa8c");
    // Outbound ICMP error layers — used by stealth mode to suppress the
    // "destination unreachable" replies that reveal closed ports to scanners.
    internal static readonly Guid FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4 =
        new("41390100-564c-4b32-bc1d-718048354d7c");
    internal static readonly Guid FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6 =
        new("7fb03b60-7b8d-4dfa-badd-980176fc4e12");

    // ----- Condition field GUIDs --------------------------------------------
    internal static readonly Guid FWPM_CONDITION_ALE_APP_ID =
        new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    internal static readonly Guid FWPM_CONDITION_FLAGS =
        new("632ce23b-5167-435c-86d7-e903684aa80c");
    internal static readonly Guid FWPM_CONDITION_IP_REMOTE_ADDRESS =
        new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    internal static readonly Guid FWPM_CONDITION_IP_PROTOCOL =
        new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    // The SDK defines ICMP_TYPE as an alias of IP_LOCAL_PORT (and ICMP_CODE as
    // an alias of IP_REMOTE_PORT) - the ICMP layers reuse the port fields.
    internal static readonly Guid FWPM_CONDITION_ICMP_TYPE =
        new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    internal static readonly Guid FWPM_CONDITION_IP_REMOTE_PORT =
        new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    internal static readonly Guid FWPM_CONDITION_IP_LOCAL_PORT =
        new("0c1ba1af-5765-453f-af22-a8f791ac775b");

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
