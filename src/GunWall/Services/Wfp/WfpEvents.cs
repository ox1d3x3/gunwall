using System.Runtime.InteropServices;

namespace GunWall.Services.Wfp;

// =============================================================================
//  WfpEvents.cs
//  Interop for kernel-driven network event subscription. This is what lets
//  GunWall see EVERY filtered connection (drop or permit) the instant it
//  happens — including blocked attempts that never appear in the socket table
//  and short-lived connections that polling would miss.
//
//  We use FwpmNetEventSubscribe0 with FWPM_NET_EVENT1, the oldest and most
//  widely supported layout (Windows 8+), to keep the marshalling as simple and
//  stable as possible. The callback runs on a kernel-supplied thread, so the
//  managed handler must be fast, must not throw, and the delegate must be kept
//  alive for the lifetime of the subscription (a GCHandle handles that).
//
//  CAUTION: every struct here is layout-sensitive. A wrong offset can crash the
//  process from the callback. The layouts below follow the documented x64 ABI.
// =============================================================================

internal static class WfpEvents
{
    private const string Dll = "fwpuclnt.dll";

    // FWPM_NET_EVENT_TYPE
    internal const uint FWPM_NET_EVENT_TYPE_CLASSIFY_DROP = 3;

    // Engine option to start collecting net events.
    internal const uint FWPM_ENGINE_COLLECT_NET_EVENTS = 0;     // FWPM_ENGINE_OPTION value index
    internal const uint FWPM_NET_EVENT_KEYWORD_CLASSIFY_DROP = 0x00000002;

    // AF families inside the event header flags.
    internal const uint FWPM_NET_EVENT_FLAG_IP_PROTOCOL_SET = 0x00000001;
    internal const uint FWPM_NET_EVENT_FLAG_LOCAL_ADDR_SET = 0x00000002;
    internal const uint FWPM_NET_EVENT_FLAG_REMOTE_ADDR_SET = 0x00000004;
    internal const uint FWPM_NET_EVENT_FLAG_LOCAL_PORT_SET = 0x00000008;
    internal const uint FWPM_NET_EVENT_FLAG_REMOTE_PORT_SET = 0x00000010;
    internal const uint FWPM_NET_EVENT_FLAG_APP_ID_SET = 0x00000020;
    internal const uint FWPM_NET_EVENT_FLAG_USER_ID_SET = 0x00000040;
    internal const uint FWPM_NET_EVENT_FLAG_IP_VERSION_SET = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    // FWPM_NET_EVENT_HEADER1 (we read the subset we need; trailing fields exist
    // but we never dereference past what we declare). Layout matches the header
    // through the address/port region used by classify-drop events.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_NET_EVENT_HEADER1
    {
        public long timeStamp;          // FILETIME (8 bytes)
        public uint flags;              // which fields are set
        public uint ipVersion;          // FWP_IP_VERSION
        public byte ipProtocol;
        // 3 bytes padding to align next uint
        public byte _pad0, _pad1, _pad2;
        public uint localAddrV4;        // union: V4 addr (host of V6 if v6)
        public uint remoteAddrV4;
        // For v6 the addresses are 16-byte arrays in a union; for our v4-focused
        // first cut we read V4. v6 events are still surfaced (addr shown blank).
        public ushort localPort;
        public ushort remotePort;
        public uint scopeId;
        public FWP_BYTE_BLOB appId;     // the app path blob
        public IntPtr userId;           // SID*
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_NET_EVENT_CLASSIFY_DROP1
    {
        public ushort layerId;
        public uint filterId;
        public ushort reauthReason;
        public uint originalProfile;
        public uint currentProfile;
        public uint msFwpDirection;
        public int isLoopback;          // BOOL
    }

    // We only need the header + type + a pointer to the classify-drop payload.
    // FWPM_NET_EVENT1 { header; type; union { classifyDrop*; ... } }.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_NET_EVENT1
    {
        public FWPM_NET_EVENT_HEADER1 header;
        public uint type;               // FWPM_NET_EVENT_TYPE
        public IntPtr value;            // union pointer (classifyDrop1* when drop)
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_NET_EVENT_SUBSCRIPTION0
    {
        public IntPtr enumTemplate;     // FWPM_NET_EVENT_ENUM_TEMPLATE0* (null = all)
        public uint flags;
        public Guid sessionKey;
    }

    // The kernel calls this for each event. context is our optional state ptr.
    internal delegate void FwpmNetEventCallback1(IntPtr context, IntPtr eventPtr);

    [DllImport(Dll)]
    internal static extern uint FwpmNetEventSubscribe0(
        IntPtr engineHandle,
        ref FWPM_NET_EVENT_SUBSCRIPTION0 subscription,
        FwpmNetEventCallback1 callback,
        IntPtr context,
        out IntPtr eventsHandle);

    [DllImport(Dll)]
    internal static extern uint FwpmNetEventUnsubscribe0(
        IntPtr engineHandle,
        IntPtr eventsHandle);

    // Turn on net-event collection. Value is a FWP_VALUE0 holding a UINT32 = 1.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_VALUE0
    {
        public uint type;   // FWP_UINT32 = 3
        public ulong value;
    }

    [DllImport(Dll)]
    internal static extern uint FwpmEngineSetOption0(
        IntPtr engineHandle,
        uint option,
        ref FWP_VALUE0 newValue);
}
