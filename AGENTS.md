# 枕星图吧AI助手 — contributor notes

The live Windows client is `TubaWinUi3.WinUI3/TubaWinUi3.csproj`, using WinUI 3 and .NET 10. Edit its source, not obsolete root-level application files. `TubaWinUI3.BackEnd` is the supporting native helper. `TubaWinUi3.Tests` contains unit tests; `TubaWinUi3.XamlHostRunner` supplies isolated native UI cases. The Compatible project is a separate legacy implementation.

Build the main project on Windows with the .NET 10 SDK and `-p:ExcludeToolsFromPublish=true` for a source-only build. Restore dependencies normally. See `docs/build.md` for the app-private Node/dsh runtime and packaging requirements. Third-party vendor executable tools are downloaded separately and are not source assets.

Preserve GPL-3.0 and upstream attribution. Bundled fonts, browser libraries and other third-party resources retain their original licenses. Do not add credentials, signing keys, user conversations, production databases, operator scripts, native test profiles, build outputs or vendor installers to commits.

Tests that use WebView must isolate application data and answer synthetic requests locally. Functional assertions and a native process's exit result are separate outcomes. Do not perform real account registration, real model calls, uninstall vendor programs or change global input-method/security settings as an implicit test.
