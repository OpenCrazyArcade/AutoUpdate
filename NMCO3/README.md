# NMCO3 - nmco3.dll

Our replacement for Nexon's messenger library `nmco3.dll`, loaded by `CA.exe`. Native AOT, x86, 30 cdecl exports
(`Exports.cs`). It does no networking of its own: every call that matters is passed to the launcher (AutoUpdate)
over the raw named pipe `OpenCrazyArcade_NMCO`.

The pipe protocol (one JSON line out, one JSON line back) is defined once, in `../Ipc/NmcoProtocol.cs`, and both
this project and the launcher (`AutoUpdate/Nmco/NmcoPipeRouter.cs`) use those types. Every request carries `"v"`, the
protocol version, so a dll and a launcher built at different times can tell what the other speaks.

## Build

    dotnet publish NMCO3/NMCO3.csproj -c Release -p:Platform=x86

Output: `NMCO3/bin/x86/Release/net10.0/win-x86/publish/nmco3.dll`.

Native AOT links with the MSVC tools. If the build stops with `'vswhere.exe' is not recognized`, put the Visual Studio
Installer folder on `PATH` first:

    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;" + $env:PATH
