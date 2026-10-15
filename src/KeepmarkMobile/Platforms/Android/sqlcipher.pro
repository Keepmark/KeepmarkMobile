# JNI_OnLoad resolves these classes/members by name; .NET calls the C API,
# so managed/Java reachability cannot discover the native registration roots.
-keep class net.zetetic.database.** { *; }
-keep interface androidx.sqlite.db.SupportSQLiteDatabase { *; }
-keep interface androidx.sqlite.SQLiteDriver { *; }
