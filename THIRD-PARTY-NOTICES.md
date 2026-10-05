# Third-party components

- PowerToys Power Display: Microsoft, MIT. Pinned source commit and per-file hashes: vendor/powertoys/upstream.json. License: vendor/powertoys/LICENSE.
- WmiLight 6.14.0: Martin Kuschnik, MIT. NuGet repository commit 1499672fdb2ef6ee8f83a041b05a56003df0f3ba. License: licenses/WmiLight.LICENSE.
- Microsoft.Windows.CsWin32 0.3.269 and Windows metadata packages: build-time generation of LUID and EnumDisplayDevices declarations for read-only topology diagnostics; they are not required as installed applications. Full resolved dependencies are recorded in packages.lock.json.
- Microsoft .NET 10: a self-contained publish includes Microsoft's runtime license and third-party notices. Keep those files with distributed binaries.
- MSTest 3.8.3: development/test dependency, not included in the P0 runtime package.
- Microsoft.Data.Sqlite 10.0.11: Microsoft, MIT; see licenses/Microsoft.Data.Sqlite.LICENSE.
- SQLitePCLRaw 2.1.12: SourceGear, LLC, Apache-2.0; license and original notices are in licenses/SQLitePCLRaw.LICENSE and licenses/SQLitePCLRaw.NOTICE. Includes the native e_sqlite3 runtime. SQLite itself is public domain; the package distribution notices still apply.

Do not distribute a lone executable: the runtime files, WmiLight native files and license notices belong to the package.

Remote control adds System.Security.Cryptography.ProtectedData 10.0.11 (Microsoft, MIT) and the ASP.NET Core shared framework; their license terms are included with the Microsoft runtime distributions. Android uses AndroidX/Jetpack Compose, Kotlin/coroutines and OkHttp/Okio under their respective Apache-2.0 notices; exact direct versions are recorded in src/android/app/build.gradle.kts and src/android/build.gradle.kts. Android debug APK is a development build, not a signed store release.
