using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// Reads live network state straight from Windows:
///  - Active TCP connections with owning process IDs (IP Helper API).
///  - Cumulative bytes sent/received per adapter (managed APIs) so the UI can
///    derive a real-time throughput graph from successive deltas.
///
/// Everything here is read-only observation of the local machine. No data
/// leaves the device.
/// </summary>
public sealed class NetworkMonitor
{
    // ---- IP Helper API ------------------------------------------------------
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;   // low 16 bits, network byte order
        public uint remoteAddr;
        public uint remotePort;  // low 16 bits, network byte order
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    /// <summary>Enumerates current TCP connections (IPv4 + IPv6) with owning PIDs.</summary>
    public List<ConnectionInfo> GetTcpConnections()
    {
        var result = new List<ConnectionInfo>();
        // Each read is isolated: if one address family or table fails (e.g. an
        // IPv6 quirk on some adapters), the others still populate. Previously a
        // single failing read threw and left the whole connection list empty.
        try { ReadTcpTable(AF_INET, result); } catch { }
        try { ReadTcpTable(AF_INET6, result); } catch { }
        try { ReadUdpTable(AF_INET, result); } catch { }
        try { ReadUdpTable(AF_INET6, result); } catch { }
        return result;
    }

    // ---- UDP listeners ------------------------------------------------------
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        public uint owningPid;
    }

    private void ReadUdpTable(int family, List<ConnectionInfo> output)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, true, family, UDP_TABLE_OWNER_PID, 0);

        IntPtr table = IntPtr.Zero;
        try
        {
            uint err = ERROR_INSUFFICIENT_BUFFER;
            for (int attempt = 0; attempt < 6 && err == ERROR_INSUFFICIENT_BUFFER; attempt++)
            {
                if (size <= 0) return;
                if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
                table = Marshal.AllocHGlobal(size);
                err = GetExtendedUdpTable(table, ref size, true, family, UDP_TABLE_OWNER_PID, 0);
            }
            if (err != 0) return;

            int numEntries = Marshal.ReadInt32(table);
            IntPtr rowPtr = IntPtr.Add(table, 4);

            if (family == AF_INET)
            {
                int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                    output.Add(new ConnectionInfo
                    {
                        ProcessId = (int)row.owningPid,
                        Protocol = "UDP",
                        LocalAddress = new IPAddress(row.localAddr).ToString(),
                        LocalPort = NetworkToHostPort(row.localPort),
                        RemoteAddress = "",
                        RemotePort = 0,
                        State = "Listen"
                    });
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                    output.Add(new ConnectionInfo
                    {
                        ProcessId = (int)row.owningPid,
                        Protocol = "UDP",
                        LocalAddress = new IPAddress(row.localAddr).ToString(),
                        LocalPort = NetworkToHostPort(row.localPort),
                        RemoteAddress = "",
                        RemotePort = 0,
                        State = "Listen"
                    });
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private void ReadTcpTable(int family, List<ConnectionInfo> output)
    {
        int size = 0;
        // First call sizes the buffer (returns ERROR_INSUFFICIENT_BUFFER).
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr table = IntPtr.Zero;
        try
        {
            // Retry: on a busy machine the table can grow between the sizing call
            // and the data read, returning ERROR_INSUFFICIENT_BUFFER. Re-size and
            // retry so the list never comes back empty under load.
            uint err = ERROR_INSUFFICIENT_BUFFER;
            for (int attempt = 0; attempt < 6 && err == ERROR_INSUFFICIENT_BUFFER; attempt++)
            {
                if (size <= 0) return;
                if (table != IntPtr.Zero) Marshal.FreeHGlobal(table);
                table = Marshal.AllocHGlobal(size);
                err = GetExtendedTcpTable(table, ref size, true, family, TCP_TABLE_OWNER_PID_ALL, 0);
            }
            if (err != 0) return;

            int numEntries = Marshal.ReadInt32(table);
            IntPtr rowPtr = IntPtr.Add(table, 4);

            if (family == AF_INET)
            {
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    output.Add(new ConnectionInfo
                    {
                        ProcessId = (int)row.owningPid,
                        Protocol = "TCP",
                        LocalAddress = new IPAddress(row.localAddr).ToString(),
                        LocalPort = NetworkToHostPort(row.localPort),
                        RemoteAddress = new IPAddress(row.remoteAddr).ToString(),
                        RemotePort = NetworkToHostPort(row.remotePort),
                        State = DescribeState(row.state)
                    });
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    output.Add(new ConnectionInfo
                    {
                        ProcessId = (int)row.owningPid,
                        Protocol = "TCP",
                        LocalAddress = new IPAddress(row.localAddr).ToString(),
                        LocalPort = NetworkToHostPort(row.localPort),
                        RemoteAddress = new IPAddress(row.remoteAddr).ToString(),
                        RemotePort = NetworkToHostPort(row.remotePort),
                        State = DescribeState(row.state)
                    });
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static int NetworkToHostPort(uint port)
    {
        // Port sits in the low 16 bits in network byte order.
        return (int)(IPAddress.NetworkToHostOrder((short)(port & 0xFFFF)) & 0xFFFF);
    }

    private static string DescribeState(uint state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SYN-Sent",
        4 => "SYN-Received",
        5 => "Established",
        6 => "FIN-Wait-1",
        7 => "FIN-Wait-2",
        8 => "Close-Wait",
        9 => "Closing",
        10 => "Last-ACK",
        11 => "Time-Wait",
        12 => "Delete-TCB",
        _ => "Unknown"
    };

    // ---- Bandwidth ----------------------------------------------------------

    /// <summary>
    /// Returns cumulative bytes (received, sent) summed across all operational,
    /// non-loopback interfaces. The UI subtracts successive samples to compute
    /// instantaneous throughput.
    /// </summary>
    private bool _loggedAdapterScan;

    /// <summary>
    /// Sum of bytes in/out across active (Up, non-loopback) adapters. Each adapter is
    /// read inside its own try/catch: some virtual/tunnel drivers (WinTUN-based VPNs,
    /// userspace tunnels, etc.) throw on GetIPStatistics, and a single failure must NOT
    /// abort the whole sum - otherwise the baseline never establishes and the throughput
    /// meter silently reads zero even while a real NIC is moving data. On the first call
    /// we write a one-time per-adapter scan to the diagnostics log so the active path can
    /// be confirmed from an exported bundle.
    /// </summary>
    public (long received, long sent) GetCumulativeBytes()
    {
        long rx = 0, tx = 0;
        bool log = !_loggedAdapterScan;
        System.Text.StringBuilder? sb = log ? new System.Text.StringBuilder() : null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
                sb?.Append("  + ").Append(nic.Name).Append(" [").Append(nic.NetworkInterfaceType)
                  .Append("] rx=").Append(stats.BytesReceived).Append(" tx=").Append(stats.BytesSent).Append('\n');
            }
            catch (Exception ex)
            {
                // A flaky virtual/tunnel adapter must not zero the whole meter.
                sb?.Append("  ! ").Append(nic.Name).Append(" [").Append(nic.NetworkInterfaceType)
                  .Append("] stats unavailable: ").Append(ex.Message).Append('\n');
            }
        }

        if (log)
        {
            _loggedAdapterScan = true;
            try { DiagnosticLog.Log("Throughput adapter scan (Up, non-loopback):\n" + sb); } catch { }
        }
        return (rx, tx);
    }
}
