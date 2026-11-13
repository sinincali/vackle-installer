# Vackle Installer

Installer and launcher for [Vackle](https://github.com/sinincali/vackle), the Flackle client mod.

Download `Vackle.exe` from the [latest release](https://github.com/sinincali/vackle-installer/releases/latest) and run it. It finds Flackle, downloads the latest signed Vackle release and sets up the Flackle shortcuts. Run it again to update, repair or uninstall.

## Building

Needs the [.NET SDK](https://dotnet.microsoft.com/download) 10.0.404 or newer. The exe targets .NET Framework 4.7.2, which ships with Windows 10 and 11.

```
dotnet build Vackle.csproj -c Release
```

## License

GPL-3.0-or-later, same as Vackle.
