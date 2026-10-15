namespace Keepmark.Core.Data;

/// <summary>Uses the explicitly packaged SQLCipher native, never a plaintext fallback.</summary>
public static class SqlCipherRuntime
{
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlcipher());
        SQLitePCL.raw.FreezeProvider();
        return true;
    });

    public static void Initialize() => _ = Initialized.Value;
}
