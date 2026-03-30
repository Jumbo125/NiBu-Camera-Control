Photobox CameraBridge – ApiServer (net8) + CameraWorker (net48)
=====================================================
Stand: 2026-01-04

Ziel
----
Die alte CameraBridge (net48) hatte einen HttpListener-Webserver und führte zu URLACL/CORS/Bindings-Problemen.
Der neue Aufbau trennt die Verantwortung:

- CameraWorker.exe (net48): Steuert die Kamera über das SDK (LiveView, Capture, Settings, Select, Refresh).
  Kein öffentlicher HTTP-Server mehr. Kommunikation nur über Named Pipes (lokal auf dem PC).
- ApiServer.exe (net8, Kestrel): Stellt die LAN/Tablet-HTTP-API bereit (REST + MJPEG), macht CORS + Auth (API-Key),
  und ruft intern den Worker per Named Pipes auf.
- Tablet/Web: Spricht ausschließlich mit ApiServer.exe.

Architektur
-----------
Tablet/Web  →  ApiServer.exe (net8, Kestrel)
                  ↓ NamedPipe (lokal, IPC)
              CameraWorker.exe (net48)

Wichtig: Named Pipes sind lokal (Maschine). Der ApiServer ist der einzige LAN-Endpoint.

Komponenten
-----------
1) CameraWorker.exe (net48)
   - Kamera-SDK Zugriff
   - Device-Enumeration / Refresh
   - LiveView-Pump (FPS, Latest JPEG im RAM)
   - Capture (File/JPEG)
   - Optional: Watchdog (Reconnect/Health)

2) ApiServer.exe (net8)
   - Minimal API / Kestrel HTTP
   - LAN Binding (z.B. http://0.0.0.0:5000)
   - CORS (für Browser/WebView)
   - Auth via API-Key (Header)
   - MJPEG Stream (/live.mjpg)
   - IPC Calls zum Worker

Startreihenfolge
----------------
Empfohlen:
1) CameraWorker.exe starten
2) ApiServer.exe starten
3) Tablet/Web ruft ApiServer im LAN auf

Der ApiServer verbindet sich zur Laufzeit über Named Pipe. Wenn der Worker nicht läuft,
liefert /api/status weiterhin einen Status, aber Control-Calls schlagen fehl.

Konfiguration
-------------
Die Konfiguration liegt als appsettings.json neben den jeweiligen EXEs (im selben Ordner).

ApiServer.exe – appsettings.json
--------------------------------
Beispiel:

{
  "http": {
    "bind": "0.0.0.0",
    "port": 5000,
    "mjpegPath": "/live.mjpg"
  },
  "auth": {
    "key": "MEIN_SUPER_KEY_123"
  },
  "cors": {
    "enabled": true,
    "allowOrigins": [ "*" ]
  },
  "ipc": {
    "pipeName": "PhotoboxBridge.Cmd"
  }
}

Erläuterung:
- http.bind:    127.0.0.1 (nur lokal) oder 0.0.0.0 (LAN)
- http.port:    Port für HTTP API (z.B. 5000)
- http.mjpegPath: Pfad für MJPEG Stream (Standard: /live.mjpg)
- auth.key:     API-Key (wenn leer/fehlend => Auth AUS, alle Endpunkte ohne Key)
- cors.allowOrigins: Browser/Tablet Zugriff (für Embedded WebView oft "*")
- ipc.pipeName: Named Pipe Name (muss zum Worker passen)

CameraWorker.exe – appsettings.json (optional)
----------------------------------------------
Der Worker muss KEINEN API-Key kennen (Empfehlung: Auth nur im ApiServer).
Im Worker stehen typischerweise Kamera-/Capture-Settings, z.B.:

{
  "captures": {
    "folder": "C:\\Photobox\\captures"
  },
  "watchdog": {
    "enabled": true
  },
  "ipc": {
    "pipeName": "PhotoboxBridge.Cmd"
  }
}

API-Key / Authentifizierung
---------------------------
Der ApiServer schützt alle Control-Endpunkte per API-Key, wenn auth.key gesetzt ist.

Header-Optionen:
1) X-Api-Key: <KEY>
2) Authorization: Bearer <KEY>

Wenn auth.key leer oder nicht gesetzt ist:
- Auth ist deaktiviert
- Alle Endpunkte funktionieren ohne Key

Public vs. Protected Endpunkte
------------------------------
Public (ohne Key, immer erreichbar):
- GET /
- GET /api/status
- GET /live.mjpg   (oder der konfigurierte mjpegPath)

Protected (Key erforderlich, wenn AuthKey gesetzt):
- GET  /api/cameras
- POST /api/select?serial=<SERIAL>
- POST /api/select?id=<ID>
- POST /api/refresh[?timeoutMs=4000]
- POST /api/liveview/start
- POST /api/liveview/stop
- GET  /api/liveview/fps
- POST /api/liveview/fps?fps=<INT>   (oder JSON body: {"fps":15})
- GET  /api/settings
- POST /api/settings                (JSON body mit Settings)
- POST /api/capture                 (JSON body)
- GET  /api/watchdog                (Key erforderlich!)
- POST /api/watchdog?enabled=0|1    (Key erforderlich!)

HTTP API – Details
------------------
Base URLs:
- Lokal:  http://127.0.0.1:<port>
- LAN:    http://<PC-IP>:<port>

GET /
-----
Response: "OK. MJPEG: /live.mjpg" (oder konfigurierter Pfad)

GET /api/status  (public)
-------------------------
Returns StatusDto JSON:
{
  "httpRunning": true,
  "liveViewRunning": true,
  "selected": "…",
  "manufacturer": "…",
  "model": "…",
  "serial": "…",
  "httpRunningSinceUtc": "…",
  "httpUptimeSeconds": 1234,
  "streamRunning": true,
  "streamClients": 1,
  "streamSendingFrames": true,
  "framesActive": true,
  "framesTotal": 123,
  "frameAgeMs": 50,
  "lastFrameUtc": "…",
  "source": { "serial": "…", "id": "…" },
  "watchdogEnabled": true
}

GET /live.mjpg  (public)
------------------------
MJPEG stream (multipart/x-mixed-replace). Für Browser/Tablet geeignet.
Hinweis: LiveView muss im Worker laufen und Frames liefern.

GET /api/cameras  (protected)
-----------------------------
Liste verfügbarer Kameras (ID, Serial, DisplayName/Model).

POST /api/select (protected)
----------------------------
Select camera:
- /api/select?serial=<SERIAL>  (hat Priorität)
- /api/select?id=<ID>

Response:
- 200 "ok"
- 404 "not found"

POST /api/refresh (protected)
-----------------------------
Re-enumerate Devices (soft refresh, stoppt LiveView NICHT).
Optional: ?timeoutMs=4000

Responses:
- 200 "ok"
- 409 "busy"
- 504 "refresh_timeout"
- 500 "refresh_failed"

POST /api/liveview/start (protected)
------------------------------------
Response: 200 "ok"

POST /api/liveview/stop (protected)
-----------------------------------
Response: 200 "ok"

GET /api/liveview/fps (protected)
---------------------------------
Returns: { "fps": 20 }

POST /api/liveview/fps (protected)
----------------------------------
Query: ?fps=<INT> oder ?value=<INT>
oder JSON body: { "fps": 15 }
Response: 200 "ok"

GET /api/settings (protected)
-----------------------------
Returns JSON settings snapshot (ISO, Shutter, WhiteBalance, etc.)

POST /api/settings (protected)
------------------------------
Body JSON, Beispiel:
{ "iso":"200", "shutter":"1/125", "whiteBalance":"Auto" }
Response: 200 "ok"

POST /api/capture (protected)
-----------------------------
Body JSON Felder:
{
  "mode": "file" | "jpeg",              // default: file
  "overwrite": false,
  "fileName": "capture_20260104_132033.jpg",  // optional
  "path": "C:\\Photobox\\captures\\...",      // optional
  "applySettings": true,
  "resetAfterShoot": true,              // default true
  "iso": "…", "shutter": "…", "whiteBalance": "…"
}

Responses:
- mode="jpeg": 200 image/jpeg (binary)
- mode="file": 200 application/json { "ok": true, "file": "C:\\...\\shot.jpg" }

Error JSON mapping:
- 409 device_busy
- 404 no_camera
- 422 cannot_focus
- 504 timeout
- 500 capture_failed

GET /api/watchdog (protected)
-----------------------------
Returns JSON: { "enabled": true|false }

POST /api/watchdog?enabled=0|1 (protected)
------------------------------------------
Returns JSON: { "ok": true, "enabled": true|false }

Named Pipe / IPC (intern)
-------------------------
Pipe Name (Beispiel): PhotoboxBridge.Cmd

Transport (typisch):
- 4-Byte Length Prefix (Int32 little endian)
- UTF-8 JSON Payload

Hinweis:
Je nach eingesetzter WorkerIpc-Library ist das Payload entweder:
a) "command"-basiert (cmd + data)
b) methoden-/RPC-basiert (Interface-Aufruf, z.B. SetLiveViewFpsAsync)

Für den ApiServer zählt: Der BridgeClient kapselt die Bytes – ApiServer ruft Methoden wie
StartLiveViewAsync(), CaptureToFileAsync(), RefreshAsync(), SetLiveViewFpsAsync() auf.

Build / Publish
---------------
ApiServer (net8):
- Debug/Release: dotnet build
- Publish (Single-File, Self-contained Beispiel x64):

  dotnet publish .\src\ApiServer\ApiServer.csproj -c Release -r win-x64 ^
    /p:PublishSingleFile=true /p:SelfContained=true /p:PublishTrimmed=false

Worker (net48):
- Build über Visual Studio oder MSBuild.
- Output: CameraWorker.exe + benötigte DLLs + appsettings.json

Betrieb im LAN
--------------
- ApiServer bindet auf 0.0.0.0:<port> für LAN.
- Firewall: Port freigeben (Inbound TCP).
- Tablet ruft http://<PC-IP>:<port>/api/status und /live.mjpg auf.
- Control-Endpunkte nur mit API-Key (wenn gesetzt).

CORS Hinweise
-------------
Wenn Tablet/Web im Browser läuft:
- CORS muss im ApiServer aktiviert sein.
- Für Embedded WebView/Single-Origin kann allowOrigins gezielt gesetzt werden.
- Für schnelle Tests: allowOrigins ["*"] (nur in vertrauenswürdigem LAN).

Troubleshooting
---------------
1) /api/status OK, aber Capture/LiveView geht nicht:
   - Worker läuft?
   - Pipe Name stimmt (ApiServer ipc.pipeName == Worker ipc.pipeName)?
   - Worker Log prüfen (Select/LiveView/Capture).

2) MJPEG leer / No frames:
   - LiveView gestartet?
   - Kamera liefert LiveView im SDK?
   - FPS zu hoch? testweise auf 10 setzen.
   - Worker Log: "No LiveView frames ..." deutet auf fehlende Frames / falsches Device hin.

3) 401/403 bei Control-Endpunkten:
   - auth.key gesetzt? Dann Key mitschicken.
   - Header korrekt: X-Api-Key oder Authorization: Bearer

4) Tablet sieht /api/status, aber nicht /live.mjpg:
   - Browser/CORS/Proxy? (MJPEG braucht direkte Verbindung)
   - Firewall / Port / Routing prüfen

Beispiel-cURL
-------------
Status (ohne Key):
  curl http://127.0.0.1:5000/api/status

Capture (mit Key):
  curl -X POST http://127.0.0.1:5000/api/capture ^
    -H "X-Api-Key: MEIN_SUPER_KEY_123" ^
    -H "Content-Type: application/json" ^
    -d "{\"mode\":\"file\",\"applySettings\":true}"

Watchdog (mit Key):
  curl http://127.0.0.1:5000/api/watchdog -H "Authorization: Bearer MEIN_SUPER_KEY_123"
  curl -X POST "http://127.0.0.1:5000/api/watchdog?enabled=1" -H "X-Api-Key: MEIN_SUPER_KEY_123"

Lizenz / Hinweis
----------------
Dieses README beschreibt die Zielarchitektur und typische Konfiguration.
Projektpfade/Settings können je nach Deployment abweichen.
