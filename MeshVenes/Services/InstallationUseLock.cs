using System;
using System.IO;

namespace MeshVenes.Services;

public static class InstallationUseLock
{
    // Multiple app processes may share this handle, but an updater needs exclusivity.
    public static FileStream? Acquire(string directory)
    {
        try { return new FileStream(Path.Combine(directory, ".meshvenes-use.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite); }
        catch (UnauthorizedAccessException) { return null; } // Read-only portable install: self-update is unavailable.
    }
}
