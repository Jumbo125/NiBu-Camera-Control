# NiBuLauncher (WinForms, .NET 8)

Dieses Projekt ist ein WinForms-Launcher (GUI + Tray) für die NiBu Photobooth-Scripts unter `./launcher/`.

## Was die GUI kann
- Startet standardmäßig mit Admin-Rechten (UAC), damit BAT-Scripts Output in der Logbox angezeigt werden kann.

- Buttons: Install/Uninstall/Firewall/Unblock/TaskScheduler/Start/Stop/Logs + "Alles ausführen".
- Tray-Icon (Doppelklick öffnet GUI).
- Statusanzeige: Caddy, PHP, Bridge API, Python (Process + Healthcheck).
- Konsolenfenster (Logbox) zeigt StdOut/StdErr der Scripts.

## Ports / Healthchecks
- Caddy/PHP-Port aus `launcher/caddy_php_port.json`.
- Bridge-Port aus `booth/tools/camerabridge/APIServer/ApiServer_settings.json` (Key `Bridge.Port` oder `Port`).
- Python-Port aus `booth/tools/python_portable/server_config.json` (best-effort Key-Suche).

## Build
Voraussetzung: .NET SDK 8.x, Visual Studio 2022 oder `dotnet` CLI.

```powershell
cd .\src\NiBuLauncher

dotnet build -c Release
```

## Portable EXE (Single-File)
```powershell
cd .\src\NiBuLauncher

# x64
dotnet publish -c Release -r win-x64 --self-contained true ^
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

# x86
dotnet publish -c Release -r win-x86 --self-contained true ^
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

> Hinweis zur Windows-7-Anforderung: Microsoft listet Windows 7 **nicht** als unterstütztes OS für .NET 8.
> Wenn Win7 wirklich zwingend ist, braucht es einen **separaten Build** (z.B. .NET 6 oder .NET Framework 4.8).

## Icon
Das Icon liegt in `src/NiBuLauncher/Assets/nibu.ico` und wird via `<ApplicationIcon>` in die EXE eingebettet.
GUI + Tray verwenden das Icon aus der EXE (`Icon.ExtractAssociatedIcon`).
