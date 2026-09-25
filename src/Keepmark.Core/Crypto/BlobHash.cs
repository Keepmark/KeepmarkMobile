using System.Security.Cryptography;
using System.Text;

namespace Keepmark.Core.Crypto;

/// <summary>SHA-256 (lowercase hex) over the ciphertext blob. Same definition
/// as the server's content hash, used to recognize our own already-applied
/// push after an ambiguous timeout.</summary>
public static class BlobHash
{
    public static string Of(string blob)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blob))).ToLowerInvariant();
}
