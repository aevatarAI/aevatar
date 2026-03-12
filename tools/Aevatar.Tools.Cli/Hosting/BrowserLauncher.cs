using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aevatar.Tools.Cli.Hosting;

internal static class BrowserLauncher
{
    public static void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
        }
        catch
        {
        }
    }
}
