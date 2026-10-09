using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Keepmark.Core.Errors;
using Keepmark.Core.I18n;
using Keepmark.Core.Logging;

namespace Keepmark.Core.Crypto;

public interface ICryptoService
{
    /// <summary>
    /// Derives the sync key. Mirrors getPasswordHash(password, syncId):
    /// PBKDF2-SHA256, 250k iterations, salt = sync ID. Returns base64.
    /// Legacy syncs without a version are rejected (see remarks).
    /// </summary>
    /// <remarks>
    /// Syncs created before versioning (no sync version) are not supported:
    /// open them once in the original client, which upgrades them in place.
    /// </remarks>
    string DeriveKey(string password, string syncId, string? syncVersion);

    string Encrypt(string plain, string derivedKeyB64);
    string Decrypt(string blob, string derivedKeyB64);
}

/// <summary>
/// AES-GCM envelope compatible with the original JS client.
/// NOTE: the JS client uses a 16-byte IV; .NET's AesGcm only accepts 12-byte
/// nonces, so GCM goes through BouncyCastle (standard GCM, GHASH path for
/// non-12-byte IVs — identical output to WebCrypto).
/// </summary>
public sealed class XCryptoService(IXbsLocalizer str, IXbsLogger log) : ICryptoService
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string DeriveKey(string password, string syncId, string? syncVersion)
    {
        if (string.IsNullOrEmpty(syncVersion))
        {
            throw new UnsupportedSyncVersionException(
                Loc.Get(Str.ErrLegacyReconnect));
        }

        _log.Debug("crypto", "Deriving key (PBKDF2-SHA256, 250k)");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            StrictUtf8.GetBytes(syncId),
            XbsConstants.KeyDerivationIterations,
            HashAlgorithmName.SHA256,
            outputLength: 32);
        return Convert.ToBase64String(key);
    }

    public string Encrypt(string plain, string derivedKeyB64)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        _log.Debug("crypto", $"Encrypting {StrictUtf8.GetByteCount(plain)}B plaintext");
        byte[] key = Convert.FromBase64String(derivedKeyB64);
        byte[] nonce = RandomNumberGenerator.GetBytes(XbsConstants.GcmNonceLength);
        byte[] compressed = LzUtf8.Compress(StrictUtf8.GetBytes(plain));

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(forEncryption: true, new AeadParameters(new KeyParameter(key), 128, nonce));
        byte[] outBuf = new byte[cipher.GetOutputSize(compressed.Length)];
        int len = cipher.ProcessBytes(compressed, 0, compressed.Length, outBuf, 0);
        cipher.DoFinal(outBuf, len);

        byte[] combined = new byte[nonce.Length + outBuf.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(outBuf, 0, combined, nonce.Length, outBuf.Length);
        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string blob, string derivedKeyB64)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return string.Empty;
        }

        try
        {
            byte[] key = Convert.FromBase64String(derivedKeyB64);
            byte[] combined = Convert.FromBase64String(blob);
            if (combined.Length < XbsConstants.GcmNonceLength + 16 + 1)
            {
                throw new InvalidCredentialsException(_str.Get(Str.ErrInvalidCredentials));
            }

            byte[] nonce = combined[..XbsConstants.GcmNonceLength];
            byte[] ct = combined[XbsConstants.GcmNonceLength..];

            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(forEncryption: false, new AeadParameters(new KeyParameter(key), 128, nonce));
            byte[] outBuf = new byte[cipher.GetOutputSize(ct.Length)];
            int len = cipher.ProcessBytes(ct, 0, ct.Length, outBuf, 0);
            cipher.DoFinal(outBuf, len);

            byte[] decompressed = LzUtf8.Decompress(outBuf);
            return StrictUtf8.GetString(decompressed);
        }
        catch (InvalidCredentialsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("crypto", $"Decryption failed: {ex.GetType().Name}");
            throw new InvalidCredentialsException(_str.Get(Str.ErrInvalidCredentials), ex);
        }
    }

    private readonly IXbsLocalizer _str = str;
    private readonly IXbsLogger _log = log;
}
