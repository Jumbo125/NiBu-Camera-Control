# Fotobox WebView2 Host

Windows WebView2 host application for loading a local HTML page or a web URL with kiosk-like controls.

Included features:
- init.json based configuration
- hardcoded fallback title in Program.cs
- compiled fallback icon via Assets/app.ico
- optional title/icon override from init.json
- minimize to tray
- kiosk mode on startup or via JavaScript
- maximize enters kiosk mode
- normal close behavior via window X or JavaScript
- JavaScript bridge for minimize, maximize, restore, setKiosk, exit

## Build

```powershell
dotnet restore
dotnet build -c Release
```

## Publish

```powershell
./publish-win-x64.ps1
```

or

```bat
publish-win-x64.bat
```

## Config

See `init.json` and `wwwroot/index.html` for full German/English documentation and JavaScript examples.
