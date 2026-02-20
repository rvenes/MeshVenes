using System;
using System.Threading;

namespace MeshVenes.Protocol;

public static class PacketIdGenerator
{
    private static int _next = unchecked((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x7FFFFFFF));

    public static uint Next()
    {
        int v = Interlocked.Increment(ref _next);

        if (v <= 0)
        {
            Interlocked.Exchange(ref _next, 1);
            v = Interlocked.Increment(ref _next);
        }

        return unchecked((uint)v);
    }
}
