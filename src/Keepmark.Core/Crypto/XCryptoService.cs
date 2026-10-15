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
    /// <summary>Independent of the 50 MiB backup-file cap: bounds expanded UTF-8 data.</summary>
    public const int MaxPlaintextBytes = 256 * 1024 * 1024;
    public const int MaxEnvelopeBytes = 50 * 1024 * 1024;

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

        int plainBytes = StrictUtf8.GetByteCount(plain);
        if (plainBytes > MaxPlaintextBytes) throw new PayloadTooLargeException();
        _log.Debug("crypto", $"Encrypting {plainBytes}B plaintext");
        byte[] key = Convert.FromBase64String(derivedKeyB64);
        byte[] nonce = RandomNumberGenerator.GetBytes(XbsConstants.GcmNonceLength);
        byte[] compressed = LzUtf8.Compress(StrictUtf8.GetBytes(plain));

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(forEncryption: true, new AeadParameters(new KeyParameter(key), 128, nonce));
        // Write ciphertext directly after the nonce, avoiding a second ciphertext buffer.
        int envelopeLength = nonce.Length + cipher.GetOutputSize(compressed.Length);
        if (envelopeLength > MaxEnvelopeBytes) throw new PayloadTooLargeException();
        byte[] combined = new byte[envelopeLength];
        nonce.CopyTo(combined, 0);
        int len = cipher.ProcessBytes(compressed, 0, compressed.Length, combined, nonce.Length);
        cipher.DoFinal(combined, nonce.Length + len);
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
            if (blob.Length > ((MaxEnvelopeBytes + 2L) / 3) * 4) throw new PayloadTooLargeException();
            if (!System.Buffers.Text.Base64.IsValid(blob, out int envelopeBytes))
                throw new FormatException("Invalid base64 envelope.");
            if (envelopeBytes > MaxEnvelopeBytes) throw new PayloadTooLargeException();
            byte[] combined = Convert.FromBase64String(blob);
            if (combined.Length < XbsConstants.GcmNonceLength + 16 + 1)
            {
                throw new InvalidCredentialsException(_str.Get(Str.ErrInvalidCredentials));
            }

            byte[] nonce = combined[..XbsConstants.GcmNonceLength];
            int ctLength = combined.Length - nonce.Length;

            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(forEncryption: false, new AeadParameters(new KeyParameter(key), 128, nonce));
            byte[] outBuf = new byte[cipher.GetOutputSize(ctLength)];
            int len = cipher.ProcessBytes(combined, nonce.Length, ctLength, outBuf, 0);
            cipher.DoFinal(outBuf, len);

            byte[] decompressed = LzUtf8.Decompress(outBuf);
            return StrictUtf8.GetString(decompressed);
        }
        catch (PayloadTooLargeException)
        {
            throw;
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

/// <summary>Authenticated payload exceeds the mobile memory budget.</summary>
public sealed class PayloadTooLargeException() : XbsException(Loc.Get(Str.ErrPayloadTooLarge))
{
}
