# Photobox CameraBridge

![License: AGPL v3+](https://img.shields.io/badge/License-AGPL%20v3%2B-blue.svg)
![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6)
![.NET](https://img.shields.io/badge/.NET-net8.0%20%2F%20net48-512BD4)
![API](https://img.shields.io/badge/API-HTTP%2FJSON-2ea44f)
![LiveView](https://img.shields.io/badge/LiveView-MJPEG-orange)
![IPC](https://img.shields.io/badge/IPC-Named%20Pipe-6f42c1)
![Docs](https://img.shields.io/badge/Docs-Swagger%20%2F%20OpenAPI-85ea2d)
![Browser](https://img.shields.io/badge/Browser-WebView2-1f6feb)

[Deutsch](#deutsch) | [English](#english)

<p align="center">
  <img src="icons/launcher-ico.png" alt="Launcher icon" width="72" />
  <img src="icons/api-server-ico.png" alt="API Server icon" width="72" />
  <img src="icons/worker-ico.png" alt="Worker icon" width="72" />
</p>

---

<a id="deutsch"></a>
# Deutsch

## Überblick

Dieses Repository bündelt die zentralen Komponenten der **Photobox CameraBridge** in einem gemeinsamen Repository:

- **launcher.exe** als zentrale Windows-Oberfläche für Installation, Start, Stop, Monitoring und Wartung
- **ApiServer.exe** als HTTP-/JSON-API mit Swagger / OpenAPI und MJPEG-LiveView
- **worker.exe** als eigentliche Kameraschnittstelle
- **NiBu-Photobox-Browser** als lokaler WebView2-Host für die Photobox-Oberfläche und die direkte Browser-Steuerung aus HTML / JavaScript
- **Shared / WorkerIpc** für gemeinsame DTOs, Commands und Named-Pipe-IPC

Der **Launcher** ist die Bedien- und Installationsschicht.  
Der **API-Server** ist die HTTP-Schicht.  
Der **Worker** steuert die Kamera.  
Der **NiBu-Photobox-Browser** stellt die lokale Browser-/Kiosk-Schicht bereit.

## Support

Donate with PayPal ☕  
Wenn dir das Projekt hilft und du mir einen Kaffee ausgeben willst:

[![Donate with PayPal ☕](https://img.shields.io/badge/Donate-PayPal-00457C?logo=paypal&logoColor=white)](https://www.paypal.me/andreasrottmann92)

## Komponenten auf einen Blick

| Komponente | Aufgabe |
|---|---|
| `launcher.exe` | Installiert und verwaltet die lokale Umgebung, startet Dienste, öffnet die App, zeigt Status und Logs an |
| `ApiServer.exe` | Stellt die HTTP/JSON-API bereit, streamt MJPEG-LiveView und zeigt die API-Dokumentation via Swagger / OpenAPI |
| `worker.exe` | Kapselt die Kamerasteuerung, LiveView, Capture, Settings und Watchdog |
| `NiBu-Photobox-Browser` | Lokaler WebView2-Host für lokale oder HTTP-basierte Oberflächen, Kioskmodus und direkte Steuerung per JavaScript-Bridge |
| `Shared` / `WorkerIpc` | Definieren Commands, DTOs, Pipe-Protokoll und die IPC-Schicht |

## Screenshots

| Launcher Allgemein | Launcher Erweitert | API Server | Worker |
|---|---|---|---|
| ![Launcher1](images/launcher1.jpg) | ![Launcher2](images/launcher2.jpg) | ![API Server](images/API-Server.jpg) | ![Worker](images/Worker.jpg) |

## Architektur

```text
Launcher / UI / lokale Verwaltung
            │
            ├─ startet / überwacht lokale Dienste
            │
            ├─ Open App → Photobox-App / Weboberfläche
            │
            └─ optionaler technischer Zugriff auf Logs / Setup / Wartung

Web UI / HTML / JavaScript
        ↓ HTTP / JSON
    ApiServer.exe
        ↕ Named Pipe IPC
      worker.exe
        ↕
      Kamera

Web UI / HTML / JavaScript
        ↕ Host Bridge
NiBu-Photobox-Browser
        ↕
   WebView2 / Kiosk / lokaler Host
```

## Repository-Struktur

```text
/src
  /Photobox.Bridge.Launcher
  /Photobox.Bridge.Shared
  /Photobox.Bridge.WorkerIpc
  /Photobox.Bridge.Worker
  /Photobox.Bridge.ApiServer
/browser
/images
/icons
/docs
```

## Funktionen

### Launcher

Der Launcher ist die zentrale Windows-Oberfläche für den lokalen Betrieb und das Setup.

Typische Aufgaben:

- **Full Install** für die komplette Einrichtung mit wenigen Klicks
- Dienste **starten**, **stoppen**, **neu starten**
- **Open App** öffnet die Photobox-App / Weboberfläche
- **Open Logs** öffnet den Log-Ordner
- Status für **Caddy**, **PHP**, **Bridge API** und **Python** prüfen
- Firewall-, Task-, Watchdog- und Port-Verwaltung
- Hilfen für Kiosk-Anpassungen und manuellen Autostart

Wichtig: Der Windows-Autostart wird bewusst **nicht automatisch** gesetzt.  
Die Einrichtung erfolgt manuell über die vom Launcher erzeugte Verknüpfung.

### API Server

Der API-Server stellt die HTTP-Schnittstelle des Systems bereit.

Typische Aufgaben:

- HTTP-Requests vom Frontend oder Controller annehmen
- diese in IPC-Commands für den Worker übersetzen
- MJPEG-LiveView streamen
- Status- und Health-Endpunkte bereitstellen
- Worker-Erreichbarkeit überwachen
- Swagger UI / OpenAPI-Dokumentation unter `/docs` bereitstellen

### Worker

Der Worker ist die eigentliche Kamerabrücke.

Typische Aufgaben:

- Kameras suchen und auswählen
- LiveView starten und stoppen
- Frames für den API-Server bereitstellen
- Kamera-Settings lesen und schreiben
- Capture als Datei oder JPEG ausführen
- optionalen USB-/Reconnect-Watchdog steuern

### NiBu-Photobox-Browser

Der **NiBu-Photobox-Browser** ist ein lokaler Browser-Host auf Basis von **WebView2** für einen flüssigeren Workflow bei der Steuerung der Photobox-Oberfläche direkt aus HTML / JavaScript.

Typische Aufgaben:

- lokale HTML-Dateien oder HTTP-/HTTPS-Ziele laden
- im **Kioskmodus** starten oder zwischen normalem Fenster und Kiosk umschalten
- lokale Konfiguration über `init.json` oder direkt über Startparameter / Flags verwenden
- Fenstertitel, Icon, Startziel und Tray-Verhalten konfigurieren
- direkte Host-Steuerung aus JavaScript über `window.hostApp`

Typische JavaScript-Funktionen:

- `window.hostApp.minimize()`
- `window.hostApp.maximize()`
- `window.hostApp.restore()`
- `window.hostApp.setKiosk(true)`
- `window.hostApp.setKiosk(false)`
- `window.hostApp.exit()`
- `window.hostApp.close()`

Typische `init.json`-Optionen:

- `url`
- `defaultUrl`
- `defaultPort`
- `localIndexPath`
- `kiosk`
- `title`
- `icon`
- `minimizeToTray`
- `allowDevTools`

Wichtige Startparameter / Flags können alternativ auch direkt beim Start übergeben werden, zum Beispiel:

```powershell
$BrowserArgs = @(
  "--url=$baseUrl",
  "--port=$caddyPort",
  "--kiosk=true"
)
```

Hinweise:

- `url` kann auf eine Web-URL oder eine lokale HTML-Datei zeigen.
- Zentrale Browser-Parameter wie `url`, `port` und `kiosk` können nicht nur über `init.json`, sondern auch direkt über Startparameter / Flags gesetzt werden.
- Eine lokale `.php`-Datei wird nicht als PHP ausgeführt. Dafür ist eine Server-URL wie `http://127.0.0.1:8080/index.php` nötig.
- Das Beenden per Fenster-X oder per JavaScript ist ohne Passwort möglich.

## Gemeinsames Protokoll

Die Kommunikation zwischen API-Server und Worker läuft über **Named Pipe IPC** mit gemeinsamen Commands und DTOs.

Wichtige Command-Gruppen:

- `status.get`
- `cameras.list`
- `camera.select`
- `camera.refresh`
- `liveview.start`
- `liveview.stop`
- `liveview.fps.get`
- `liveview.fps.set`
- `settings.get`
- `settings.set`
- `capture`
- `watchdog.get`
- `watchdog.set`
- `frame.wait_next`

Wichtig:  
Auf Worker-/IPC-Ebene gibt es weiterhin nur den Capture-Command `capture`.  
Die Komfortfunktion **Capture + danach LiveView** wird auf Ebene des API-Servers umgesetzt.

## Quick Start

### Variante A – über den Launcher

1. `launcher.exe` starten
2. optional Sprache wählen
3. **Advanced** öffnen
4. zuerst **Unblock Files** ausführen
5. **Full Install** ausführen
6. zurück ins Hauptfenster
7. **Start** klicken
8. Status prüfen
9. **Open App** nutzen

### Variante B – direkt über API Server + Worker

1. `ApiServer_settings.json` neben `ApiServer.exe` ablegen
2. `Worker.ExePath` korrekt setzen
3. `Bridge.PipeName` zwischen API-Server und Worker abstimmen
4. `worker.exe` und `ApiServer.exe` starten
5. testen:
   - `GET /api/status`
   - `GET /docs`
   - `GET /live.mjpg`

### Variante C – über den NiBu-Photobox-Browser

1. Browser-Konfiguration in `init.json` prüfen oder Startparameter / Flags festlegen
2. `url` oder `localIndexPath` auf das gewünschte Startziel setzen
3. optional `kiosk`, `title`, `icon`, `minimizeToTray` und `allowDevTools` festlegen
4. **NiBu-Photobox-Browser** starten
5. Oberfläche laden und bei Bedarf aus JavaScript über `window.hostApp` steuern

## Typische Standardports

Im Launcher-Umfeld sind typischerweise folgende Ports vorgesehen:

- **Caddy:** `8050`
- **PHP:** `8051`
- **Bridge API:** `8052`
- **Python:** `8053`

Ports sollten nur geändert werden, wenn es dafür einen klaren Grund gibt.

## Repo-Inhalt und Releases

### Repository

Dieses Repository enthält bewusst **nur eigene Projektdateien**.

Externe oder fremde Materialien, gebündelte Drittanbieter-Dateien und sonstiges Fremdmaterial werden bewusst **nicht** in das Repository eingecheckt, damit kein unnötiges Fremdmaterial veröffentlicht wird.

### Releases

Die **Releases** können die vorgesehene, lauffähige Zusammenstellung der Anwendung enthalten, einschließlich der für den Betrieb benötigten Bestandteile, soweit dies im jeweiligen Release vorgesehen und lizenzrechtlich zulässig ist.

Dadurch bleibt das Repository sauber, während Releases als vollständige Pakete für Betrieb, Test oder Deployment genutzt werden können.

## Bilder und Icons im Repository

Diese README verwendet relative Pfade zu den Dateien im Repository:

- `images/launcher1.jpg`
- `images/API-Server.jpg`
- `images/Worker.jpg`
- `icons/launcher-ico.png`
- `icons/api-server-ico.png`
- `icons/worker-ico.png`

## Danksagung / Acknowledgements

Vielen Dank an folgende Projekte und Werkzeuge:

- **digiCamControl** – für Inspiration sowie als Grundlage einzelner Ideen, Daten oder angepasster Abläufe
- **Swagger / OpenAPI** – für die übersichtliche API-Dokumentation und die schnelle technische Übersicht des API-Servers

Hinweis: Einige Daten und Teile des Verhaltens wurden für dieses Projekt angepasst, abgeändert und in den eigenen Ablauf integriert.

## Lizenz

Dieses Projekt steht unter **AGPL-3.0-or-later**.

- SPDX: `AGPL-3.0-or-later`
- Copyright (c) 2026 Andreas Rottmann
- Siehe [`LICENSE`](LICENSE)

Beispielhafter SPDX-Header:

```csharp
// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
```

---

<a id="english"></a>
# English

## Overview

This repository combines the core components of **Photobox CameraBridge** in one shared repository:

- **launcher.exe** as the central Windows UI for installation, start, stop, monitoring and maintenance
- **ApiServer.exe** as the HTTP/JSON API with Swagger / OpenAPI and MJPEG LiveView
- **worker.exe** as the actual camera bridge
- **NiBu-Photobox-Browser** as a local WebView2 host for the Photobox frontend and improved browser control directly from HTML / JavaScript
- **Shared / WorkerIpc** for common DTOs, commands and Named Pipe IPC

The **launcher** is the operations and setup layer.  
The **API server** is the HTTP layer.  
The **worker** controls the camera.  
The **NiBu-Photobox-Browser** provides the local browser / kiosk layer.

## Support

Donate with PayPal ☕

[![Donate with PayPal ☕](https://img.shields.io/badge/Donate-PayPal-00457C?logo=paypal&logoColor=white)](https://www.paypal.me/andreasrottmann92)

## Components at a glance

| Component | Purpose |
|---|---|
| `launcher.exe` | Installs and manages the local environment, starts services, opens the app, shows status and logs |
| `ApiServer.exe` | Provides the HTTP/JSON API, streams MJPEG LiveView and exposes API docs through Swagger / OpenAPI |
| `worker.exe` | Handles camera control, LiveView, capture, settings and watchdog |
| `NiBu-Photobox-Browser` | Local WebView2 host for local or HTTP-based frontend targets, kiosk mode and direct JavaScript bridge control |
| `Shared` / `WorkerIpc` | Define commands, DTOs, pipe protocol and the IPC layer |

## Screenshots

| Launcher Global | Launcher Advance | API Server | Worker |
|---|---|---|---|
| ![Launcher1](images/launcher1.jpg) | ![Launcher2](images/launcher2.jpg) | ![API Server](images/API-Server.jpg) | ![Worker](images/Worker.jpg) |

## Architecture

```text
Launcher / UI / local administration
            │
            ├─ starts / monitors local services
            │
            ├─ Open App → Photobox app / web UI
            │
            └─ optional technical access to logs / setup / maintenance

Web UI / HTML / JavaScript
        ↓ HTTP / JSON
    ApiServer.exe
        ↕ Named Pipe IPC
      worker.exe
        ↕
      Camera

Web UI / HTML / JavaScript
        ↕ Host bridge
NiBu-Photobox-Browser
        ↕
   WebView2 / kiosk / local host
```

## Repository structure

```text
/src
  /Photobox.Bridge.Launcher
  /Photobox.Bridge.Shared
  /Photobox.Bridge.WorkerIpc
  /Photobox.Bridge.Worker
  /Photobox.Bridge.ApiServer
/browser
/images
/icons
/docs
```

## Features

### Launcher

The launcher is the central Windows UI for local operation and setup.

Typical tasks:

- **Full Install** for one-click setup
- **start**, **stop** and **restart** services
- **Open App** opens the Photobox app / web UI
- **Open Logs** opens the log folder
- check status for **Caddy**, **PHP**, **Bridge API** and **Python**
- firewall, task, watchdog and port management
- helpers for kiosk tweaks and manual autostart

Important: Windows autostart is intentionally **not set automatically**.  
Setup is done manually through the shortcut created by the launcher.

### API Server

The API server provides the HTTP interface of the system.

Typical tasks:

- accept HTTP requests from the frontend or controller
- translate them into IPC commands for the worker
- stream MJPEG LiveView
- provide status and health endpoints
- monitor worker reachability
- provide Swagger UI / OpenAPI documentation under `/docs`

### Worker

The worker is the actual camera bridge.

Typical tasks:

- discover and select cameras
- start and stop LiveView
- provide frames for the API server
- read and write camera settings
- execute capture as file or JPEG
- manage optional USB / reconnect watchdog behavior

### NiBu-Photobox-Browser

The **NiBu-Photobox-Browser** is a local **WebView2** host for a smoother workflow when controlling the Photobox frontend directly from HTML / JavaScript.

Typical tasks:

- load local HTML files or HTTP / HTTPS targets
- start in **kiosk mode** or switch between normal window and kiosk mode
- use local configuration through `init.json` or directly through startup parameters / flags
- configure window title, icon, startup target and tray behavior
- expose direct host control to JavaScript through `window.hostApp`

Typical JavaScript functions:

- `window.hostApp.minimize()`
- `window.hostApp.maximize()`
- `window.hostApp.restore()`
- `window.hostApp.setKiosk(true)`
- `window.hostApp.setKiosk(false)`
- `window.hostApp.exit()`
- `window.hostApp.close()`

Typical `init.json` options:

- `url`
- `defaultUrl`
- `defaultPort`
- `localIndexPath`
- `kiosk`
- `title`
- `icon`
- `minimizeToTray`
- `allowDevTools`

Important startup parameters / flags can also be passed directly when launching, for example:

```powershell
$BrowserArgs = @(
  "--url=$baseUrl",
  "--port=$caddyPort",
  "--kiosk=true"
)
```

Notes:

- `url` can point to a web URL or a local HTML file.
- Core browser parameters such as `url`, `port` and `kiosk` can be provided not only through `init.json` but also directly through startup parameters / flags.
- A local `.php` file is not executed as PHP. For PHP, use a server URL such as `http://127.0.0.1:8080/index.php`.
- Closing via window X or via JavaScript works without a password.

## Shared protocol

Communication between API server and worker uses **Named Pipe IPC** with shared commands and DTOs.

Main command groups:

- `status.get`
- `cameras.list`
- `camera.select`
- `camera.refresh`
- `liveview.start`
- `liveview.stop`
- `liveview.fps.get`
- `liveview.fps.set`
- `settings.get`
- `settings.set`
- `capture`
- `watchdog.get`
- `watchdog.set`
- `frame.wait_next`

Important:  
At worker / IPC level there is still only one capture command: `capture`.  
The convenience flow **capture + then restart LiveView** is implemented at API-server level.

## Quick start

### Option A – via launcher

1. start `launcher.exe`
2. optionally choose the language
3. open **Advanced**
4. run **Unblock Files** first
5. run **Full Install**
6. go back to the main window
7. click **Start**
8. verify the status
9. use **Open App**

### Option B – directly via API Server + Worker

1. place `ApiServer_settings.json` next to `ApiServer.exe`
2. set `Worker.ExePath` correctly
3. make sure `Bridge.PipeName` matches on both sides
4. start `worker.exe` and `ApiServer.exe`
5. test:
   - `GET /api/status`
   - `GET /docs`
   - `GET /live.mjpg`

### Option C – via NiBu-Photobox-Browser

1. create or review the browser `init.json`, or define startup parameters / flags
2. set `url` or `localIndexPath` to the desired startup target
3. optionally configure `kiosk`, `title`, `icon`, `minimizeToTray` and `allowDevTools`
4. start **NiBu-Photobox-Browser**
5. load the UI and control the host from JavaScript through `window.hostApp` if needed

## Typical default ports

Within the launcher environment, these ports are typically used:

- **Caddy:** `8050`
- **PHP:** `8051`
- **Bridge API:** `8052`
- **Python:** `8053`

Ports should only be changed when there is a clear reason.

## Repository contents and releases

### Repository

This repository intentionally contains **project-owned files only**.

External materials, bundled third-party files and other foreign material are intentionally **not committed** to the repository so that unnecessary third-party content is not published.

### Releases

**Releases** may contain the intended runnable bundle of the application, including required runtime parts where planned for the release and where licensing permits.

This keeps the repository clean while still allowing releases to provide complete packages for operation, testing or deployment.

## Images and icons in the repository

This README uses relative paths to these repository files:

- `images/launcher1.jpg`
- `images/API-Server.jpg`
- `images/Worker.jpg`
- `icons/launcher-ico.png`
- `icons/api-server-ico.png`
- `icons/worker-ico.png`

## Thanks / Acknowledgements

Special thanks to:

- **digiCamControl** – for inspiration and as a basis for selected ideas, data or adapted workflows
- **Swagger / OpenAPI** – for the clear API overview and the interactive documentation layer of the API server

Note: Some data and parts of the behavior were adapted, modified and integrated into this project’s own workflow.

## License

This project is licensed under **AGPL-3.0-or-later**.

- SPDX: `AGPL-3.0-or-later`
- Copyright (c) 2026 Andreas Rottmann
- See [`LICENSE`](LICENSE)

Example SPDX header:

```csharp
// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
```
