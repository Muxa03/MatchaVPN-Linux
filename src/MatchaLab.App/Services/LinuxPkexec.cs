using System.Diagnostics;
using System.Runtime.Versioning;

namespace MatchaLab.App.Services;

[SupportedOSPlatform("linux")]
internal static class LinuxPkexec
{
    private static string? Self => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

    public static (int code, string err) Run(string helperArgs)
    {
        var self = Self;
        if (string.IsNullOrEmpty(self)) return (-1, "self path unknown");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pkexec", $"\"{self}\" {helperArgs}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return (-1, "pkexec start failed");
            var err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, err);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public static Process? Start(string helperArgs)
    {
        var self = Self;
        if (string.IsNullOrEmpty(self)) return null;
        try
        {
            return Process.Start(new ProcessStartInfo("pkexec", $"\"{self}\" {helperArgs}")
            {
                UseShellExecute = false,
            });
        }
        catch { return null; }
    }
}
