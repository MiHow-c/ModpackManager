using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ModpackManager.Core.Remote;

/// <summary>The host's self-signed TLS certificate. Clients pin its SHA-256 fingerprint from the invite code.</summary>
public static class HostCertificate
{
    public static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        if (!File.Exists(pfxPath))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=ModpackManager host", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));

            Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            // the file holds the private key: owner-only from the moment it exists
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(pfxPath, options);
            file.Write(cert.Export(X509ContentType.Pkcs12));
        }
        // Windows TLS (SChannel) cannot use in-memory keys: there the key sits in the user's key store while the
        // certificate is alive and is deleted when it is disposed. Elsewhere it never leaves memory.
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null, flags);
    }

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
}
