using System;
using System.Diagnostics;
using System.IO;

namespace MeshtasticWin.Services;

public static class AppDataPaths
{
    public static string BasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshtasticWin");

    public static string LogsPath => Path.Combine(BasePath, "Logs");

    public static string TraceroutePath => Path.Combine(LogsPath, "traceroute");

    public static string GpsPath => Path.Combine(LogsPath, "gps");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(TraceroutePath);
        Directory.CreateDirectory(GpsPath);
        Debug.WriteLine($"MeshtasticWin BasePath: {BasePath}");
    }
}
