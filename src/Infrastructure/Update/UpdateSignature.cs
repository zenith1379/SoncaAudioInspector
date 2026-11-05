using System.IO;
using System.Security.Cryptography;

namespace SoncaAudioInspector.Update;

public static class UpdateSignature
{
    public const string PublicKey = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6l4Lpnt5FMqu0tgRpLk/h5LVfayh
PF8pwCn57h1AjOmRvYny2/YFu7AN9nTKdb7vh7J4o4DdMzmf1AZqSNx76w==
-----END PUBLIC KEY-----
""";
    private static ECDsa Key(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException("Chỉ chấp nhận ECDSA P-256.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }
    public static string Sign(byte[] bytes, string privateKey)
    {
        using var key = Key(privateKey);
        return Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
    public static void Verify(byte[] bytes, string signature, string publicKey = PublicKey)
    {
        using var key = Key(publicKey);
        byte[] sig;
        try { sig = Convert.FromBase64String(signature); }
        catch (FormatException ex) { throw new InvalidDataException("Chữ ký không phải base64.", ex); }
        if (sig.Length != 64 || !key.VerifyData(bytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Chữ ký bản cập nhật không hợp lệ.");
    }
    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static bool Matches(string path, UpdateFile file) => File.Exists(path) && new FileInfo(path).Length == file.Size && Hash(path) == file.Sha256;
}
