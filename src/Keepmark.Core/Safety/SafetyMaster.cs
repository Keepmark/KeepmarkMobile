using System.Security.Cryptography;

namespace Keepmark.Core.Safety;

// Envelope encryption for safety backups: files are encrypted ONCE under a
// random Master Key; only the Master Key is re-wrapped when the sync key
// rotates (switch / change password). Rotation is a single small atomic
// write — backup blobs are never re-encrypted.
public static class SafetyMaster
{
    public const int CurrentFormat = 1;
    public const int MaxWraps = 3;

    /// <summary>Short id of a key-encrypting key (matches without secrets).</summary>
    public static string Fingerprint(string keyB64)
    {
        var raw = Convert.FromBase64String(keyB64);
        return Convert.ToHexString(SHA256.HashData(raw))[..8].ToLowerInvariant();
    }

    /// <summary>Adds (or refreshes) a wrap, newest first, trimmed.</summary>
    public static List<SafetyMasterWrap> AddWrap(
        IEnumerable<SafetyMasterWrap> wraps, string kekId, string wrappedKey) =>
        wraps.Where(w => w.KekId != kekId)
            .Prepend(new SafetyMasterWrap(kekId, wrappedKey))
            .Take(MaxWraps)
            .ToList();

    public static SafetyMasterWrap? FindWrap(IEnumerable<SafetyMasterWrap> wraps, string kekId) =>
        wraps.FirstOrDefault(w => w.KekId == kekId);
}

/// <summary>Master Key record: the plaintext key never touches disk, only
/// wrapped copies (one per known sync key, newest first).</summary>
public sealed record SafetyMasterRecord(int Format, List<SafetyMasterWrap> Wraps);

/// <summary>Master Key wrapped under one key-encrypting (sync-derived) key.</summary>
public sealed record SafetyMasterWrap(string KekId, string WrappedKey);
