# NaiwaDesktopPet

This companion contains C# source from LIN4RUN's [NaiwaDesktopPet v2.5.1](https://github.com/LIN4RUN/naiwa-desktop-pet/tree/a0f044560feb1cd9fd8707ee7816a06b726fb0d1). Its [MIT license](./LICENSE) accompanies the source and compiled companion.

GlassDesk builds the companion as a .NET Framework 4.8 WinForms executable. The managed session owns the window and tray lifecycle and uses a separate settings directory. Rendering, animation, audio, and pet interactions use the upstream implementation. File drops use the Windows Recycle Bin through GlassDesk's recycle service.

GlassDesk 1.6.3 bundles the 18 animation and audio runtime resources from the [official v2.5.1 portable archive](https://github.com/LIN4RUN/naiwa-desktop-pet/releases/tag/v2.5.1) under `assets-v2.5.1/`. The source archive SHA-256, resource sizes and resource SHA-256 hashes are pinned in `assets-manifest.json` and checked during packaging. No upstream executable, batch script or manual is shipped. The MIT license and upstream artwork notice accompany the component; the upstream README calls for confirming the rights to any third-party artwork before commercial use.

The companion reads installed resources in place without importing an archive or writing into the installation directory. User settings and runtime state remain in GlassDesk's data directory. Complete resources imported by older versions remain a fallback when bundled resources are unavailable.

To run the companion's self-test, pass `--self-test <outputDirectory> <assetDirectory>` to the built executable.
