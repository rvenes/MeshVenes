using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace MeshVenes.Services;

public enum UpdateStatus
{
    NotSupported,
    UpToDate,
    UpdateAvailable,
    GitHubReleaseInfo,
    Failed
}

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string VersionText { get; init; }
    public required string ZipUrl { get; init; }
    public required string Sha256 { get; init; }
    public long SizeBytes { get; init; }
    public string? Notes { get; init; }
    public string? ReleaseUrl { get; init; }
}

public sealed class UpdateCheckResult
{
    public required UpdateStatus Status { get; init; }
    public UpdateInfo? Update { get; init; }
    public string? Message { get; init; }
    public string? ReleaseUrl { get; init; }
}

/// <summary>
/// Self-update against the static feed on venes.org: version.json manifest plus
/// a full zip of the publish folder. Falls back to the GitHub releases API for
/// display-only information when the manifest is unreachable.
/// </summary>
public static class UpdateService
{
    public const string ManifestUrl = "https://venes.org/meshvenes/version.json";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/rvenes/MeshVenes/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly object Gate = new();
    private static Task<UpdateCheckResult>? _inFlightCheck;

    public static string CurrentVersionText { get; } = ResolveVersion();

    public static UpdateCheckResult? LastResult { get; private set; }

    /// <summary>
    /// Self-update requires an unpackaged install in a writable folder; MSIX and
    /// read-only locations degrade to showing a download link only.
    /// </summary>
    public static bool CanSelfUpdate()
    {
        if (Packaging.IsPackaged())
            return false;

        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, ".mv-writetest");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            _inFlightCheck ??= RunCheckAsync(ct);
            return _inFlightCheck;
        }
    }

    private static async Task<UpdateCheckResult> RunCheckAsync(CancellationToken ct)
    {
        try
        {
            var result = await CheckManifestAsync(ct).ConfigureAwait(false)
                         ?? await CheckGitHubAsync(ct).ConfigureAwait(false);
            LastResult = result;
            return result;
        }
        finally
        {
            lock (Gate)
                _inFlightCheck = null;
        }
    }

    private static async Task<UpdateCheckResult?> CheckManifestAsync(CancellationToken ct)
    {
        try
        {
            // Cache-buster: static hosts and proxies may cache version.json hard.
            var url = $"{GetManifestUrl()}?t={DateTime.UtcNow.Ticks}";
            using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var manifest = await JsonSerializer.DeserializeAsync<ManifestDto>(stream, cancellationToken: ct).ConfigureAwait(false);
            if (manifest is null ||
                string.IsNullOrWhiteSpace(manifest.Version) ||
                string.IsNullOrWhiteSpace(manifest.Url) ||
                string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                return null;
            }

            if (!TryParseVersion(manifest.Version, out var remote) ||
                !TryParseVersion(CurrentVersionText, out var current))
            {
                return null;
            }

            if (remote <= current)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.UpToDate,
                    Message = $"Update status: you are up to date ({CurrentVersionText})."
                };
            }

            var versionText = SanitizeDisplayVersion(manifest.Version);
            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpdateAvailable,
                Update = new UpdateInfo
                {
                    Version = remote,
                    VersionText = versionText,
                    ZipUrl = manifest.Url,
                    Sha256 = manifest.Sha256,
                    SizeBytes = manifest.SizeBytes,
                    Notes = manifest.Notes,
                    ReleaseUrl = manifest.ReleaseUrl
                },
                Message = $"Update status: new version available ({versionText}).",
                ReleaseUrl = manifest.ReleaseUrl
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<UpdateCheckResult> CheckGitHubAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var latest = await JsonSerializer.DeserializeAsync<LatestReleaseDto>(stream, cancellationToken: ct).ConfigureAwait(false);
            if (latest is null || string.IsNullOrWhiteSpace(latest.TagName))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.Failed,
                    Message = "Update status: unable to read latest release information."
                };
            }

            if (!TryParseVersion(CurrentVersionText, out var current) || !TryParseVersion(latest.TagName, out var remote))
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.GitHubReleaseInfo,
                    Message = $"Update status: latest on GitHub is {latest.TagName}.",
                    ReleaseUrl = latest.HtmlUrl
                };
            }

            if (remote > current)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.GitHubReleaseInfo,
                    Message = $"Update status: new version available ({latest.TagName}).",
                    ReleaseUrl = latest.HtmlUrl
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                Message = $"Update status: you are up to date ({CurrentVersionText})."
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Failed,
                Message = $"Update status: check failed ({ex.Message})."
            };
        }
    }

    /// <summary>
    /// Downloads and verifies the update zip, extracts it to a staging folder,
    /// and launches the updater script. The script waits for this process to
    /// exit, then replaces the install folder and restarts the app — so the
    /// caller must tell the user a restart is required and offer to do it.
    /// </summary>
    public static async Task DownloadAndStageAsync(UpdateInfo update, IProgress<double>? downloadProgress = null, CancellationToken ct = default)
    {
        if (update is null)
            throw new ArgumentNullException(nameof(update));
        if (!CanSelfUpdate())
            throw new InvalidOperationException("Self-update is not supported for this installation.");

        var updatesDir = Path.Combine(AppDataPaths.BasePath, "Updates");
        CleanDirectory(updatesDir);
        Directory.CreateDirectory(updatesDir);

        var zipPath = Path.Combine(updatesDir, $"MeshVenes-{update.VersionText}-win-x64.zip");
        await DownloadWithProgressAsync(update.ZipUrl, zipPath, update.SizeBytes, downloadProgress, ct).ConfigureAwait(false);

        var actualHash = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
        if (!string.Equals(actualHash, update.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidOperationException("Downloaded update failed the integrity check (sha256 mismatch).");
        }

        var stagingDir = Path.Combine(updatesDir, $"staging-{update.VersionText}");
        ZipFile.ExtractToDirectory(zipPath, stagingDir);
        if (!File.Exists(Path.Combine(stagingDir, "MeshVenes.exe")))
            throw new InvalidOperationException("Update package does not contain MeshVenes.exe.");

        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exePath = Path.Combine(installDir, "MeshVenes.exe");
        var scriptPath = Path.Combine(updatesDir, "apply-update.cmd");
        File.WriteAllText(scriptPath, BuildUpdaterScript(stagingDir, installDir, exePath, Environment.ProcessId), Encoding.ASCII);

        var startInfo = new ProcessStartInfo
        {
            FileName = scriptPath,
            WorkingDirectory = updatesDir,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Could not start the updater script.");
    }

    /// <summary>
    /// Closes the app so the pending updater script can apply the staged
    /// update and restart. Window.Close() runs normal shutdown handlers;
    /// Application.Exit() alone does not reliably end the process.
    /// </summary>
    public static void RestartToApplyUpdate()
    {
        var window = MeshVenes.App.MainWindowInstance;
        if (window is not null)
        {
            window.Close();
            return;
        }

        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private static string BuildUpdaterScript(string stagingDir, string installDir, string exePath, int pid)
    {
        // Waits for the app to exit, copies the new files over the install folder
        // (never deleting anything), and restarts the app. Sleep uses the ping
        // trick because "timeout /t" fails without an interactive console.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"SRC={stagingDir}\"");
        sb.AppendLine($"set \"DST={installDir}\"");
        sb.AppendLine($"set \"EXE={exePath}\"");
        sb.AppendLine($"set \"APPPID={pid}\"");
        sb.AppendLine(":waitloop");
        sb.AppendLine("tasklist /FI \"PID eq %APPPID%\" | findstr /C:\" %APPPID% \" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine("robocopy \"%SRC%\" \"%DST%\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP");
        sb.AppendLine("if errorlevel 8 goto failed");
        sb.AppendLine("rmdir /s /q \"%SRC%\"");
        sb.AppendLine("start \"\" \"%EXE%\"");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":failed");
        sb.AppendLine("start \"\" \"%EXE%\"");
        sb.AppendLine("exit /b 1");
        return sb.ToString();
    }

    private static async Task DownloadWithProgressAsync(string url, string destinationPath, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        var partialPath = destinationPath + ".partial";

        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(partialPath))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                readTotal += read;
                if (totalBytes > 0)
                    progress?.Report(Math.Min(1d, (double)readTotal / totalBytes));
            }
        }

        File.Move(partialPath, destinationPath, overwrite: true);
        progress?.Report(1d);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void CleanDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Leftovers from a previous run that are still locked are harmless.
        }
    }

    private static string GetManifestUrl()
    {
        // Debug/testing override so the feed can be pointed at a local server.
        var overrideUrl = SettingsStore.GetString("UpdateManifestUrlOverride")?.Trim();
        return string.IsNullOrWhiteSpace(overrideUrl) ? ManifestUrl : overrideUrl;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MeshVenes/1.0 (+https://github.com/rvenes/MeshVenes)");
        return client;
    }

    public static bool TryParseVersion(string? text, out Version version)
        => UpdateVersionUtil.TryParseVersion(text, out version);

    public static string SanitizeDisplayVersion(string versionText)
        => UpdateVersionUtil.SanitizeDisplayVersion(versionText);

    private static string ResolveVersion()
    {
        // Prefer MSIX package version when available, otherwise fall back to assembly version.
        try
        {
            var v = Package.Current.Id.Version;
            if (v.Major != 0 || v.Minor != 0 || v.Build != 0 || v.Revision != 0)
                return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            // Unpackaged contexts may not support Package.Current.
        }

        try
        {
            var asm = typeof(UpdateService).GetTypeInfo().Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return SanitizeDisplayVersion(info);

            return asm.GetName().Version?.ToString() ?? "—";
        }
        catch
        {
            return "—";
        }
    }

    private sealed class ManifestDto
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("releaseUrl")]
        public string? ReleaseUrl { get; set; }
    }

    private sealed class LatestReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
