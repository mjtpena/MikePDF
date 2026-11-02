using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace S8.Fixtures;

/// <summary>Test PKI built in memory with CertificateRequest. Nothing here is ever imported into a Windows certificate store.</summary>
public sealed class TestPki
{
    public required X509Certificate2 Root { get; init; }       // public part only (what verifiers trust)
    public required X509Certificate2 Leaf { get; init; }       // with private key
    public required string Algorithm { get; init; }
    public byte[] RootCer => Root.Export(X509ContentType.Cert);
}

public static class CertFactory
{
    public static TestPki Rsa3072(string tag = "RSA-3072", DateTimeOffset? leafNotBefore = null, DateTimeOffset? leafNotAfter = null)
    {
        using var rootKey = RSA.Create(3072);
        var rootReq = new CertificateRequest($"CN=Quire S8 Test Root {tag}, O=Quire spike S8 (not for production)", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var root = MakeRoot(rootReq, rootKey);
        var leafKey = RSA.Create(3072);
        var leafReq = new CertificateRequest($"CN=Quire S8 Test Signer {tag}, O=Quire spike S8 (not for production)", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var leaf = MakeLeaf(leafReq, root, leafNotBefore, leafNotAfter, rootKey, null);
        return new TestPki { Root = X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert)), Leaf = leaf.CopyWithPrivateKey(leafKey), Algorithm = tag };
    }

    public static TestPki EcdsaP256(string tag = "ECDSA-P256")
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootReq = new CertificateRequest($"CN=Quire S8 Test Root {tag}, O=Quire spike S8 (not for production)", rootKey, HashAlgorithmName.SHA256);
        var root = MakeRoot(rootReq, rootKey);
        var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest($"CN=Quire S8 Test Signer {tag}, O=Quire spike S8 (not for production)", leafKey, HashAlgorithmName.SHA256);
        var leaf = MakeLeaf(leafReq, root, null, null, null, rootKey);
        return new TestPki { Root = X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert)), Leaf = leaf.CopyWithPrivateKey(leafKey), Algorithm = tag };
    }

    private static X509Certificate2 MakeRoot(CertificateRequest req, AsymmetricAlgorithm key)
    {
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(10));
    }

    private static X509Certificate2 MakeLeaf(CertificateRequest req, X509Certificate2 root, DateTimeOffset? nb, DateTimeOffset? na, RSA? rsaRootKey, ECDsa? ecRootKey)
    {
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        var serial = new byte[12]; RandomNumberGenerator.Fill(serial); serial[0] &= 0x7F; if (serial[0] == 0) serial[0] = 1;
        X509SignatureGenerator gen = rsaRootKey != null
            ? X509SignatureGenerator.CreateForRSA(rsaRootKey, RSASignaturePadding.Pkcs1)
            : X509SignatureGenerator.CreateForECDsa(ecRootKey!);
        return req.Create(root.SubjectName, gen, nb ?? DateTimeOffset.UtcNow.AddDays(-7), na ?? DateTimeOffset.UtcNow.AddYears(2), serial);
    }
}
