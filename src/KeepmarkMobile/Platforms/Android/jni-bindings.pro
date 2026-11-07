# Java members that .NET bindings reach only by name through JNI. R8 cannot
# see those calls, so without these rules it strips or renames the members
# and the app fails at startup (v1.0.238: SecureStorage needs
# EncryptedSharedPreferences$PrefKeyEncryptionScheme.AES256_SIV).
-keep class androidx.security.crypto.** { *; }
-keep class com.google.crypto.tink.** { *; }
