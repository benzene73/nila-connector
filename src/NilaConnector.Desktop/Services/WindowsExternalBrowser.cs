using System.Diagnostics;
using NilaConnector.Core.Nila;

namespace NilaConnector.Desktop.Services;

public sealed class WindowsExternalBrowser : IExternalBrowser
{
    public void Open(Uri uri)
    {
        _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }
}
