using System.Buffers.Binary;

namespace ModpackManager.Core.Sources;

public static class Murmur2
{
    /// <summary>CurseForge fingerprint: MurmurHash2 with seed 1 over the file with bytes 9, 10, 13 and 32 removed (as in packwiz).</summary>
    public static uint CurseForgeFingerprint(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var length = 0;
        foreach (var b in bytes)
            if (b is not (9 or 10 or 13 or 32))
                bytes[length++] = b;
        return Hash(bytes.AsSpan(0, length), 1);
    }

    /// <summary>Austin Appleby's 32-bit MurmurHash2.</summary>
    public static uint Hash(ReadOnlySpan<byte> data, uint seed)
    {
        const uint m = 0x5bd1e995;
        var h = seed ^ (uint)data.Length;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            k *= m;
            k ^= k >> 24;
            k *= m;
            h *= m;
            h ^= k;
        }
        switch (data.Length - i)
        {
            case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h ^= data[i]; h *= m; break;
        }
        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return h;
    }
}
