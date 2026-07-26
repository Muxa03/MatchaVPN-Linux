using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace MatchaLab.App.Services;

public static class SingleInstance
{
    public static event Action? ShowRequested;

    private const string PipeName = "MatchaLab.ShowUI";

    private static string SocketPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Path.GetTempPath();
            return Path.Combine(dir, $"matchalab-ui-{Environment.UserName}.sock");
        }
    }

    public static bool TryClaim(bool showExisting)
    {
        if (OperatingSystem.IsWindows()) return TryClaimWindows(showExisting);
        if (OperatingSystem.IsLinux()) return TryClaimLinux(showExisting);
        return true;
    }

    private static bool TryClaimLinux(bool showExisting)
    {
        var path = SocketPath;
        if (File.Exists(path))
        {
            try
            {
                using var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                c.Connect(new UnixDomainSocketEndPoint(path));
                c.SendTimeout = 1500;
                c.Send(Encoding.ASCII.GetBytes(showExisting ? "show\n" : "ping\n"));
                return false;
            }
            catch { try { File.Delete(path); } catch { } }
        }
        try
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            var t = new Thread(() => ListenLinux(listener)) { IsBackground = true, Name = "single-instance" };
            t.Start();
        }
        catch { }
        return true;
    }

    private static void ListenLinux(Socket listener)
    {
        var buf = new byte[64];
        while (true)
        {
            try
            {
                using var conn = listener.Accept();
                conn.ReceiveTimeout = 1000;
                int n;
                try { n = conn.Receive(buf); } catch { continue; }
                if (n > 0 && Encoding.ASCII.GetString(buf, 0, n).StartsWith("show"))
                    ShowRequested?.Invoke();
            }
            catch (SocketException) { Thread.Sleep(200); }
            catch { return; }
        }
    }

    private static bool TryClaimWindows(bool showExisting)
    {
        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.FirstPipeInstance);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                c.Connect(1500);
                var msg = Encoding.ASCII.GetBytes(showExisting ? "show\n" : "ping\n");
                c.Write(msg, 0, msg.Length);
                c.Flush();
            }
            catch { }
            return false;
        }
        var t = new Thread(() => ListenWindows(server)) { IsBackground = true, Name = "single-instance" };
        t.Start();
        return true;
    }

    private static void ListenWindows(NamedPipeServerStream server)
    {
        var buf = new byte[64];
        while (true)
        {
            try
            {
                server.WaitForConnection();
                var n = server.Read(buf, 0, buf.Length);
                if (n > 0 && Encoding.ASCII.GetString(buf, 0, n).StartsWith("show"))
                    ShowRequested?.Invoke();
                server.Disconnect();
            }
            catch
            {
                try { server.Dispose(); } catch { }
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                }
                catch { return; }
            }
        }
    }
}
