# Third-party components

- Fluent System Icons: Microsoft Corporation, MIT; pinned source commit 83b26348c811f072429427ff48362238656524fe. Windows SVG and Android VectorDrawable paths are included locally. License: licenses/Fluent-System-Icons.LICENSE (also included in Android assets). Import details: assets/README.md.

- PowerToys Power Display: Microsoft, MIT. Pinned source commit and per-file hashes: vendor/powertoys/upstream.json. License: vendor/powertoys/LICENSE.
- WmiLight 6.14.0: Martin Kuschnik, MIT. NuGet repository commit 1499672fdb2ef6ee8f83a041b05a56003df0f3ba. License: licenses/WmiLight.LICENSE.
- Microsoft.Windows.CsWin32 0.3.269 and Windows metadata packages: build-time generation of LUID and EnumDisplayDevices declarations for read-only topology diagnostics; they are not required as installed applications. Full resolved dependencies are recorded in packages.lock.json.
- Microsoft .NET 10: a self-contained publish includes Microsoft's runtime license and third-party notices. Keep those files with distributed binaries.
- MSTest 3.8.3: development/test dependency, not included in the P0 runtime package.
- Microsoft.Data.Sqlite 10.0.11 (Windows) and Microsoft.Data.Sqlite.Core 10.0.14 (Relay): Microsoft, MIT; see licenses/Microsoft.Data.Sqlite.LICENSE.
- Relay uses Microsoft.AspNetCore.Identity.EntityFrameworkCore, Microsoft.EntityFrameworkCore (including Abstractions, Relational, Sqlite and Sqlite.Core) and Microsoft.Extensions.DependencyModel 10.0.14: Microsoft, MIT; see licenses/Microsoft.EntityFrameworkCore.LICENSE, licenses/Microsoft.AspNetCore.LICENSE and licenses/Microsoft.Extensions.DependencyModel.LICENSE. Microsoft.EntityFrameworkCore.Analyzers is build-time only. Exact resolved dependencies are recorded in the Relay packages.lock.json files. The license texts are copied from the official dotnet/aspnetcore, dotnet/efcore and dotnet/runtime v10.0.14 tags.
- SQLitePCLRaw 2.1.12 (Windows) and 2.1.13 (Relay): SourceGear, LLC, Apache-2.0; license and original notices are in licenses/SQLitePCLRaw.LICENSE and licenses/SQLitePCLRaw.NOTICE. Includes bundle_e_sqlite3, core, provider_e_sqlite3 and the native lib.e_sqlite3 runtime. SQLite itself is public domain; the package distribution notices still apply.

Do not distribute a lone executable: the runtime files, WmiLight native files and license notices belong to the package.

Remote control adds System.Security.Cryptography.ProtectedData 10.0.11 (Microsoft, MIT) and the ASP.NET Core shared framework; their license terms are included with the Microsoft runtime distributions. Android uses AndroidX/Jetpack Compose, Kotlin/coroutines and OkHttp/Okio under their respective Apache-2.0 notices; exact direct versions are recorded in src/android/app/build.gradle.kts and src/android/build.gradle.kts. Android debug APK is a development build, not a signed store release.

UI pairing uses QRCoder 1.8.0 (Raffael Herrmann and Shane Krueger, MIT; licenses/QRCoder.LICENSE) for Windows QR generation, and JourneyApps ZXing Android Embedded / ZXing (Apache-2.0) for Android offline camera decoding. No QR payload is sent to an external image service.

Six-digit LAN pairing uses BouncyCastle.Cryptography 2.7.0 (.NET) and bcprov-jdk18on 1.86 (Java), Legion of the Bouncy Castle Inc., MIT-style license; see licenses/BouncyCastle.LICENSE. The application-specific SRP transcript and encrypted bootstrap protocol are not claimed to be externally audited.
