using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GunWall.Services;

/// <summary>
/// Closes an active TCP connection by setting its state to DELETE_TCB via the
/// IP Helper <c>SetTcpEntry</c> API, which tears the connection down (RST).
/// IPv4 TCP only (the OS API's limitation). Requires administrator rights,
/// which GunWall has. Best-effort: returns false instead of throwing.
/// </summary>
public static class ConnectionService
{
    [DllImport("iphlpapi.dll")]
    private static extern int SetTcpEntry(ref MIB_TCPROW row);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    private const uint MIB_TCP_STATE_DELETE_TCB = 12;

    public static bool CloseTcpConnection(string localAddr, int localPort, string remoteAddr, int remotePort)
    {
        try
        {
            if (!TryIpv4(localAddr, out uint la) || !TryIpv4(remoteAddr, out uint ra))
                return false;

            var row = new MIB_TCPROW
            {
                dwState = MIB_TCP_STATE_DELETE_TCB,
                dwLocalAddr = la,
                dwLocalPort = HostToTcpRowPort(localPort),
                dwRemoteAddr = ra,
                dwRemotePort = HostToTcpRowPort(remotePort)
            };
            return SetTcpEntry(ref row) == 0; // NO_ERROR
        }
        catch
        {
            return false;
        }
    }

    // The IP, as a network-byte-order DWORD (same layout the TCP table uses).
    private static bool TryIpv4(string addr, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(addr, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        value = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        return true;
    }

    // MIB_TCPROW stores the port in network byte order in the low 16 bits.
    private static uint HostToTcpRowPort(int port)
        => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
}
