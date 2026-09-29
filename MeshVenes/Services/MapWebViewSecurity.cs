using Microsoft.Web.WebView2.Core;

namespace MeshVenes.Services;

public static class MapWebViewSecurity
{
    public static void Configure(CoreWebView2 core, string host)
    {
        core.Settings.AreHostObjectsAllowed = false;
        core.NavigationStarting += (_, args) => args.Cancel = !MapSourcePolicy.IsTrustedPage(args.Uri, host);
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
    }
}
