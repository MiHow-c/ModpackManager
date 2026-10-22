using System.Buffers.Binary;
using ModpackManager.Core.Sources;
using ModpackManager.Tests.Fixtures;

namespace ModpackManager.Tests;

public class Murmur2Tests
{
    /// <summary>SMHasher's KeysetVerification: published value for MurmurHash2 (x86, 32-bit) is 0x27864C1E.</summary>
    [Fact]
    public void Matches_the_SMHasher_verification_value()
    {
        var key = new byte[256];
        var hashes = new byte[4 * 256];
        for (var i = 0; i < 256; i++)
        {
            key[i] = (byte)i;
            BinaryPrimitives.WriteUInt32LittleEndian(hashes.AsSpan(i * 4), Murmur2.Hash(key.AsSpan(0, i), (uint)(256 - i)));
        }
        Assert.Equal(0x27864C1Eu, Murmur2.Hash(hashes, 0));
    }

    [Fact]
    public void CurseForge_fingerprint_ignores_whitespace()
    {
        var dir = TestPacks.NewDir("murmur");
        var a = TestPacks.Write(Path.Combine(dir, "a.txt"), "ab cd\r\n\tef");
        var b = TestPacks.Write(Path.Combine(dir, "b.txt"), "abcdef");
        Assert.Equal(Murmur2.CurseForgeFingerprint(b), Murmur2.CurseForgeFingerprint(a));
        Assert.Equal(Murmur2.Hash("abcdef"u8, 1), Murmur2.CurseForgeFingerprint(b));
    }
}
