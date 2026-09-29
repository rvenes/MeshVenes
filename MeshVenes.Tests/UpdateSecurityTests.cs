using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public sealed class UpdateSecurityTests
{
    private const string Url = "https://venes.org/meshvenes/MeshVenes-1.5.1-win-x64.zip";
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void OfficialPackageIsAccepted() => UpdateSourcePolicy.ValidatePackage("1.5.1", Url, Hash, 100);

    [Theory]
    [InlineData("http://venes.org/meshvenes/MeshVenes-1.5.1-win-x64.zip")]
    [InlineData("https://evil.example/MeshVenes-1.5.1-win-x64.zip")]
    [InlineData("https://venes.org.evil.example/meshvenes/MeshVenes-1.5.1-win-x64.zip")]
    [InlineData("https://venes.org/meshvenes/MeshVenes-1.5.1-win-x64.zip?redirect=1")]
    [InlineData("https://venes.org/meshvenes/MeshVenes-1.5.2-win-x64.zip")]
    [InlineData("file:///C:/update.zip")]
    [InlineData("http://localhost:8000/update.zip")]
    public void RejectsUnexpectedDownloadUrls(string url) =>
        Assert.Throws<InvalidDataException>(() => UpdateSourcePolicy.ValidatePackage("1.5.1", url, Hash, 100));

    [Theory]
    [InlineData("../1.5.1")]
    [InlineData("1.5.1\n")]
    [InlineData("1.5.1-beta")]
    [InlineData("1.5.1%PATH%")]
    [InlineData("01.5.1")]
    public void RejectsUnsafeVersion(string version) =>
        Assert.Throws<InvalidDataException>(() => UpdateSourcePolicy.ValidatePackage(version, Url, Hash, 100));

    [Theory]
    [InlineData("http://localhost:8080/version.json", true)]
    [InlineData("http://127.0.0.1:8080/version.json", true)]
    [InlineData("http://[::1]:8080/version.json", true)]
    [InlineData("http://127.0.0.1.evil.example/version.json", false)]
    [InlineData("http://192.168.1.2/version.json", false)]
    [InlineData("file:///tmp/version.json", false)]
    public void TestFeedIsRestrictedToLoopback(string url, bool allowed) =>
        Assert.Equal(allowed, UpdateSourcePolicy.IsLoopbackHttp(url));

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("NUL.txt")]
    [InlineData("folder./file.txt")]
    [InlineData("folder /file.txt")]
    [InlineData("payload.msix")]
    [InlineData("MESHVENES.EXE")]
    public void RejectsUnsafeArchiveBeforeWriting(string name)
    {
        using var temp = new TemporaryDirectory();
        var zip = MakeZip(temp.Path, name);
        var output = System.IO.Path.Combine(temp.Path, "extracted");
        Assert.Throws<InvalidDataException>(() => UpdatePackageIntegrity.ExtractVerifiedArchive(zip, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void RequiresPriAndRejectsSymlinks()
    {
        using var temp = new TemporaryDirectory();
        var zip = MakeZip(temp.Path);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
            archive.GetEntry("MeshVenes.pri")!.Delete();
        Assert.Throws<InvalidDataException>(() => UpdatePackageIntegrity.ExtractVerifiedArchive(zip, System.IO.Path.Combine(temp.Path, "missing")));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("MeshVenes.pri").Open())) writer.Write("resources");
            archive.CreateEntry("link").ExternalAttributes = unchecked((int)0xA1FF0000);
        }
        Assert.Throws<InvalidDataException>(() => UpdatePackageIntegrity.ExtractVerifiedArchive(zip, System.IO.Path.Combine(temp.Path, "link")));
    }

    [Fact]
    public void ExtractsValidPackage()
    {
        using var temp = new TemporaryDirectory();
        var zip = MakeZip(temp.Path, "Assets/Map/map.html");
        var output = System.IO.Path.Combine(temp.Path, "extracted");
        UpdatePackageIntegrity.ExtractVerifiedArchive(zip, output);
        Assert.Equal("test", File.ReadAllText(System.IO.Path.Combine(output, "Assets/Map/map.html")));
    }

    [Theory]
    [InlineData("https://mapassets.local/Map/map.html", true)]
    [InlineData("https://mapassets.local/Map/map.html#view", true)]
    [InlineData("http://mapassets.local/Map/map.html", false)]
    [InlineData("https://mapassets.local.evil.example/Map/map.html", false)]
    [InlineData("https://mapassets.local:8000/Map/map.html", false)]
    [InlineData("https://mapassets.local/other.html", false)]
    [InlineData("https://user@mapassets.local/Map/map.html", false)]
    [InlineData("https://mapassets.local/Map/map.html?remote=1", false)]
    public void MapBridgeOnlyAcceptsExpectedPage(string source, bool allowed) =>
        Assert.Equal(allowed, MapSourcePolicy.IsTrustedPage(source, "mapassets.local"));

    [Fact]
    public void ApplyScriptPreservesLiteralPathsAndBacksUpOriginalFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "staging")).FullName;
        var install = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "install %PATH% & [x] ' æ")).FullName;
        File.WriteAllText(System.IO.Path.Combine(source, "app.txt"), "new");
        File.WriteAllText(System.IO.Path.Combine(install, "app.txt"), "old");
        File.WriteAllText(System.IO.Path.Combine(install, "user.txt"), "preserve");
        var script = UpdateApplyScript.Build(source, install, "unused.exe", int.MaxValue);
        // Do not launch a GUI during the test; all actual file operations run unchanged.
        script = script.Replace("Start-Process -FilePath $executable -WorkingDirectory $destination", "exit 0");
        Assert.True(RunPowerShell(temp.Path, script) == 0, File.ReadAllText(System.IO.Path.Combine(temp.Path, "apply-result.txt")));
        Assert.Equal("new", File.ReadAllText(System.IO.Path.Combine(install, "app.txt")));
        Assert.Equal("old", File.ReadAllText(System.IO.Path.Combine(temp.Path, "backup", "app.txt")));
        Assert.Equal("preserve", File.ReadAllText(System.IO.Path.Combine(install, "user.txt")));
    }

    [Fact]
    public void ApplyScriptRollsBackAfterMidCopyFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "staging")).FullName;
        var install = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "install")).FullName;
        File.WriteAllText(System.IO.Path.Combine(source, "a.txt"), "new");
        File.WriteAllText(System.IO.Path.Combine(install, "a.txt"), "old");
        var script = UpdateApplyScript.Build(source, install, "unused.exe", int.MaxValue);
        script = script.Replace("[IO.File]::Copy($file.FullName, $target, $true)", "[IO.File]::Copy($file.FullName, $target, $true); throw 'Simulated disk failure'");
        Assert.Equal(1, RunPowerShell(temp.Path, script));
        Assert.Equal("old", File.ReadAllText(System.IO.Path.Combine(install, "a.txt")));
        Assert.Contains("original files restored", File.ReadAllText(System.IO.Path.Combine(temp.Path, "apply-result.txt")));
    }

    private static int RunPowerShell(string directory, string script)
    {
        var file = System.IO.Path.Combine(directory, "apply.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(true));
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(30000), "Updater test timed out.");
        return process.ExitCode;
    }

    [Fact]
    public void UpdaterRefusesToModifyAnInstallationStillInUse()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "staging")).FullName;
        var install = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "install")).FullName;
        File.WriteAllText(System.IO.Path.Combine(source, "app.txt"), "new");
        File.WriteAllText(System.IO.Path.Combine(install, "app.txt"), "old");
        using var held = new FileStream(System.IO.Path.Combine(install, ".meshvenes-use.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal(1, RunPowerShell(temp.Path, UpdateApplyScript.Build(source, install, "unused.exe", int.MaxValue)));
        Assert.Equal("old", File.ReadAllText(System.IO.Path.Combine(install, "app.txt")));
    }

    [Fact]
    public void LockedTargetLeavesAllOriginalFilesUntouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "staging")).FullName;
        var install = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "install")).FullName;
        File.WriteAllText(System.IO.Path.Combine(source, "a.txt"), "new-a");
        File.WriteAllText(System.IO.Path.Combine(source, "b.txt"), "new-b");
        File.WriteAllText(System.IO.Path.Combine(install, "a.txt"), "old-a");
        File.WriteAllText(System.IO.Path.Combine(install, "b.txt"), "old-b");
        using (var held = new FileStream(System.IO.Path.Combine(install, "b.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(1, RunPowerShell(temp.Path, UpdateApplyScript.Build(source, install, "unused.exe", int.MaxValue)));
        Assert.Equal("old-a", File.ReadAllText(System.IO.Path.Combine(install, "a.txt")));
        Assert.Equal("old-b", File.ReadAllText(System.IO.Path.Combine(install, "b.txt")));
    }

    [Fact]
    public void InterruptedUpdateCanBeRecoveredButTamperedBackupIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var source = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "staging")).FullName;
        var install = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "install")).FullName;
        foreach (var name in new[] { "MeshVenes.exe", "MeshVenes.pri" })
        {
            File.WriteAllText(System.IO.Path.Combine(source, name), "new");
            File.WriteAllText(System.IO.Path.Combine(install, name), "old");
        }
        var script = UpdateApplyScript.Build(source, install, "unused.exe", int.MaxValue)
            .Replace("[IO.File]::Copy($file.FullName, $target, $true)", "[IO.File]::Copy($file.FullName, $target, $true); exit 17");
        Assert.Equal(17, RunPowerShell(temp.Path, script));
        var repair = System.IO.Path.Combine(AppContext.BaseDirectory, "Repair-Update.ps1");
        var command = $"& '{repair}' -UpdateDirectory '{temp.Path}' -InstallDirectory '{install}'; if ($?) {{ exit 0 }} else {{ exit 1 }}";
        var savedExe = System.IO.Path.Combine(temp.Path, "backup", "MeshVenes.exe");
        File.WriteAllText(savedExe, "tampered");
        Assert.Equal(1, RunPowerShell(temp.Path, command));
        Assert.Equal("new", File.ReadAllText(System.IO.Path.Combine(install, "MeshVenes.exe")));
        File.WriteAllText(savedExe, "old");
        Assert.Equal(0, RunPowerShell(temp.Path, command));
        Assert.Equal("old", File.ReadAllText(System.IO.Path.Combine(install, "MeshVenes.exe")));
        Assert.Equal("old", File.ReadAllText(System.IO.Path.Combine(install, "MeshVenes.pri")));
        Assert.True(File.Exists(savedExe));
    }

    private static string MakeZip(string directory, string? extra = null)
    {
        var path = System.IO.Path.Combine(directory, "release.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in new[] { "MeshVenes.exe", "MeshVenes.pri", extra }.OfType<string>())
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("test");
        }
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meshvenes-security-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
