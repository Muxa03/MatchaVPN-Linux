using System.Runtime.InteropServices;

namespace MatchaLab.App.Services;

internal static class NativeTunnel
{
    [DllImport("tunnel.dll", EntryPoint = "WireGuardTunnelService",
               CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool WireGuardTunnelService(
        [MarshalAs(UnmanagedType.LPWStr)] string confContent,
        [MarshalAs(UnmanagedType.LPWStr)] string tunnelName);
}
