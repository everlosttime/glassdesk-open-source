# ASP.NET Core framework provenance

Nearby uses Microsoft's first-party `Microsoft.AspNetCore.App` framework reference. The self-contained win-x64 publish resolves `Microsoft.AspNetCore.App.Runtime.win-x64` version 10.0.12. No LocalSend client executable or source code is included in the product payload.

License files were copied unchanged from that runtime NuGet package:

- LICENSE.txt SHA-256: D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B
- THIRD-PARTY-NOTICES.txt SHA-256: 307D014F65D8482314F1400DDEAE7A0CBABB96C2207BCC77F6233CC10588E5D9

They ship as ASPNETCORE-LICENSE.txt and ASPNETCORE-THIRD-PARTY-NOTICES.txt. The package verifier requires these notices and the Kestrel HTTP/socket runtime when GlassDesk.Nearby.dll is present. The payload manifest records the exact shipped file sizes and SHA-256 values.

Sources: https://www.nuget.org/packages/Microsoft.AspNetCore.App.Runtime.win-x64/10.0.12 and https://github.com/dotnet/aspnetcore .
