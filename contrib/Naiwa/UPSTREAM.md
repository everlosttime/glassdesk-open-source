# NaiwaDesktopPet

This companion contains C# source from LIN4RUN's [NaiwaDesktopPet v2.5.1](https://github.com/LIN4RUN/naiwa-desktop-pet/tree/a0f044560feb1cd9fd8707ee7816a06b726fb0d1). Its [MIT license](./LICENSE) accompanies the source and compiled companion.

GlassDesk builds the companion as a .NET Framework 4.8 WinForms executable. The managed session owns the window and tray lifecycle and uses a separate settings directory. Rendering, animation, audio, and pet interactions use the upstream implementation. File drops use the Windows Recycle Bin through GlassDesk's recycle service.

Artwork, animation packs, audio, manuals, and binaries are separate from this source. The companion reads the original user-imported resources from the [official v2.5.1 portable archive](https://github.com/LIN4RUN/naiwa-desktop-pet/releases/tag/v2.5.1). The upstream README calls for confirming the rights to any third-party artwork before commercial use.

To run the companion's self-test, pass `--self-test <outputDirectory> <assetDirectory>` to the built executable.
