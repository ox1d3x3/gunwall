using System.Runtime.InteropServices;

namespace GunWall.Services;

/// <summary>
/// Reports whether the machine's internet connection is metered.
///
/// Used to hold back the scheduled database refresh. A firewall that quietly
/// spends someone's mobile data allowance on a 4 MB vendor registry it could have
/// fetched tomorrow is doing the wrong thing, and it does it invisibly.
///
/// Only the SCHEDULE consults this. A refresh the user asked for happens
/// regardless - they can see what they pressed and are entitled to spend their
/// own data on it.
///
/// Fails OPEN, deliberately: if the cost cannot be determined the connection is
/// reported unmetered and the refresh proceeds. The alternative fails closed and
/// silently stops refreshing forever on any machine where this API misbehaves,
/// which is a worse and much harder failure to notice than one unwanted download.
/// </summary>
internal static class NetworkCost
{
    // Verified against netlistmgr.h (mingw-w64), not recalled. Trap 2.5: a
    // measured constant taken from memory has cost this project releases before.
    //   CLSID_NetworkListManager  dcb00c01-570f-4a9b-8d69-199fdba5723b
    //   IID_INetworkCostManager   dcb00008-570f-4a9b-8d69-199fdba5723b
    private static readonly Guid ClsidNetworkListManager =
        new("dcb00c01-570f-4a9b-8d69-199fdba5723b");

    // NLM_CONNECTION_COST, from the same header.
    private const uint CostUnknown        = 0x0;
    private const uint CostUnrestricted   = 0x1;
    private const uint CostFixed          = 0x2;
    private const uint CostVariable       = 0x4;
    private const uint CostOverDataLimit  = 0x10000;
    private const uint CostRoaming        = 0x40000;

    /// <summary>
    /// GetCost is the FIRST method after IUnknown in INetworkCostManagerVtbl,
    /// which is why declaring only that one is safe - the slot is correct. The
    /// two methods after it are omitted because nothing calls them; declaring
    /// them wrongly would be worse than not declaring them at all.
    /// </summary>
    [ComImport]
    [Guid("dcb00008-570f-4a9b-8d69-199fdba5723b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint pCost, IntPtr pDestIPAddr);
    }

    /// <summary>
    /// True only when the connection is known to cost money by volume. Unknown
    /// is not metered - see the class remarks on failing open.
    /// </summary>
    public static bool IsMetered()
    {
        object? com = null;
        try
        {
            Type? t = Type.GetTypeFromCLSID(ClsidNetworkListManager);
            if (t is null) return false;

            com = Activator.CreateInstance(t);
            if (com is not INetworkCostManager mgr) return false;

            mgr.GetCost(out uint cost, IntPtr.Zero);

            if (cost == CostUnknown || cost == CostUnrestricted) return false;

            // Fixed is an allowance, variable is per-byte, and either can be over
            // its limit or roaming. All of them mean "not while unattended".
            return (cost & (CostFixed | CostVariable |
                            CostOverDataLimit | CostRoaming)) != 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.LogException("NetworkCost.IsMetered", ex);
            return false;   // fail open
        }
        finally
        {
            if (com is not null && Marshal.IsComObject(com))
            {
                try { Marshal.ReleaseComObject(com); } catch { }
            }
        }
    }

    /// <summary>What to show the reader when a refresh was held back.</summary>
    public const string HeldBackMessage =
        "skipped - this connection is metered. Use Download now to fetch it anyway.";
}
