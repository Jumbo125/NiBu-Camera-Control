# Photobox.CameraBridge — Projektübersicht

> Diese Datei ist für zukünftige Debug-Sessions gedacht, damit das Projekt nicht
> jedes Mal neu analysiert werden muss. Bei größeren Architekturänderungen bitte
> aktualisieren.

## Was ist das

.NET Framework 4.8 / WinForms Worker-Prozess (`x86`, weil das Canon EDSDK x86 ist),
Assembly-Name **`CameraWorker.exe`** (siehe `Photobox.CameraBridge.csproj`,
`<AssemblyName>CameraWorker</AssemblyName>` — der Projekt-/Ordnername stimmt NICHT
mit dem Exe-Namen überein, wichtig fürs Suchen von Log-/Prozessnamen).

Der Worker kapselt die Kamera-SDKs (Canon EDSDK, Nikon/CameraControl.Devices,
PortableDeviceLib/WIA) und stellt sie über eine Named-Pipe-IPC einem anderen
Prozess (dem eigentlichen Photobox-Host, net8, außerhalb dieses Repo-Teils)
zur Verfügung. Zusätzlich gibt es eine Tray-App/optionale WinForms-UI zum
manuellen Debuggen.

## Solution-Layout (`Worker/`)

- `Photobox.CameraBridge/` — dieses Projekt (Worker-Exe, Program.cs = Einstiegspunkt)
- `CameraControl.Devices/` — digiCamControl-Fork, Kamera-SDK-Abstraktion (Nikon etc.)
- `Canon.Eos.Framework/` — Canon EDSDK Wrapper
- `PortableDeviceLib/` — WIA/PTP Fallback
- `Photobox.Bridge.Shared/` — netstandard2.0, DTOs + Pipe-Protokoll (`BridgeProtocol.cs`, `Dtos.cs`, `Commands.cs`)
- `Photobox.Bridge.WorkerIpc/` — Named-Pipe-Server (`BridgeIpcServer.cs`) + `IBridgeWorker` Interface

**Wichtig:** `Photobox.CameraBridge/bin/` und `*/obj/` sind nur Build-Output —
niemals dort nach Quellcode/Verhalten suchen, das kann veraltet sein.

## Kernklassen (`Core/`)

| Datei | Zweck |
|---|---|
| `Program.cs` | Einstiegspunkt. Parst CLI-Args, baut Logger/FileSink, startet `MtaWorker`, `CameraHost`, `UsbReconnectWatchdog`, `BridgeIpcServer`, Tray-`ApplicationContext`. |
| `StartupOptions.cs` | CLI-Flags (`--headless`, `--log[=path]`, `--fps=`, `--auto-liveview`, `--select=`, `--one-instance`, ...). |
| `MtaWorker.cs` | Dedizierter STA-Thread mit WinForms-Message-Pump, weil Canon EDSDK-Callbacks eine Message-Loop brauchen. Alle SDK-Aufrufe laufen über `InvokeAsync`. |
| `CameraHost.cs` | Zentrale Kamerasteuerung: Refresh, Select, Capture, LiveView-Koordination, Nikon-Fallback/Camera-Map-Overrides. |
| `LiveViewPump.cs` | Pollt LiveView-Frames von der Kamera, schreibt sie in den `FrameHub`, hat eigenen Watchdog (Neustart wenn >2s kein Frame). |
| `FrameHub.cs` | Hält den neuesten JPEG-Frame threadsicher, `WaitNextAsync` für Long-Poll. |
| `UsbReconnectWatchdog.cs` | Pollt alle 800ms ob Kamera weg ist, versucht alle ~2.5s Reconnect, merkt sich ob LiveView danach wiederhergestellt werden soll. |
| `RingLogger.cs` | In-Memory Ringpuffer-Logger (Default-Kapazität 5000 Zeilen im Programmstart, siehe `Program.cs:92`). Events: `LineAppended` (nur String, für UI) und `EntryAppended` (mit Exception, für File-Sink). |
| `FileLogSink.cs` | Schreibt `EntryAppended` in eine Datei, siehe Abschnitt "Log-Rotation" unten. |
| `AppSettings.cs` / `AppSettingsLoader.cs` | `appsettings.json` neben der Exe (LiveViewFps, KeepAliveSeconds, DefaultCaptureFolder, LoadWiaDevices). |
| `CameraMap.cs` / `CameraMapLoader.cs` | `camera-map.json` neben der Exe: Modell→Treiber-Overrides, Nikon-Default-Treiber. |

## UI (`UI/`)

- `MainForm.cs` — optionale WinForms-Debug-UI (Live-Log, LiveView-Preview, manuelle Steuerung).
- `HeadlessTrayContext.cs` — reine Tray-Icon-App für `--headless`-Betrieb ohne UI (Restart/Exit im Kontextmenü).
- In `Program.cs` gibt es eine dritte, "immer aktive" Tray-Variante (`WorkerAppContext`), die UI lazy nachlädt.

## IPC

- Named Pipe, Pipename Default `PhotoboxBridge.Cmd` (`Photobox.Bridge.Shared/BridgeProtocol.cs`), überschreibbar via `appsettings.json` (siehe `Program.ReadPipeNameFromAppSettingsJson`).
- Protokoll: längenpräfixiertes UTF-8-JSON, `PipeRequest{id,cmd,payload}` → `PipeResponse{id,ok,errorCode,errorMessage,payload}`.
- `IBridgeWorker` (in `Photobox.Bridge.WorkerIpc`) definiert die Kommandos (Status, Cameras, Select, Refresh, LiveView Start/Stop/Fps, Settings, Capture, Watchdog, WaitNextFrame).
- `Ipc/BridgeWorkerAdapter.cs` implementiert `IBridgeWorker` und delegiert an `CameraHost`/`UsbReconnectWatchdog`.

## Threading-Modell

- WinForms-Message-Loop läuft im Hauptthread (Application.Run).
- Alle SDK-Zugriffe laufen über `MtaWorker` auf einem eigenen STA-Thread mit eigener Message-Pump (`InvokeAsync`/`InvokeAsync<T>`), damit Canon-EDSDK-Callbacks funktionieren.
- `CameraHost` hat zwei Semaphoren: `_captureGate` (blockt gegen Capture) und `_switchGate` (serialisiert Select/Start/Stop).

## Log-Rotation (Bugfix-Historie — wichtig fürs Debugging!)

`Core/FileLogSink.cs` schreibt alle `RingLogger.EntryAppended`-Events in eine Datei
(aktiviert nur mit `--log` bzw. `--log=<pfad>` CLI-Flag, siehe `Program.cs:50-90`).
Ohne `--log` gibt es **keine** Logdatei.

**Bug #1 (unbegrenztes Wachstum) — behoben:** Ohne Größenbegrenzung wuchs die
Logdatei unbegrenzt — jede Aufnahme schreibt mehrere Zeilen, und der
`UsbReconnectWatchdog` loggt alle ~2.5s solange keine Kamera verbunden ist.
Fix: `FileLogSink.MaxSizeBytes = 5 MB`. Nach jedem Eintrag prüft
`RotateIfOversized()` `_writer.BaseStream.Length`; bei Überschreitung wird die
aktuelle Datei nach `<name>.1` verschoben (alte `.1` wird überschrieben) und
neu begonnen. Beim Start wird eine bereits übergroße Vorgänger-Datei ebenfalls
sofort rotiert. Maximale Plattengröße dadurch ~2× 5 MB, verteilt auf zwei
Dateien (aktuelle `.log` + Backup `.log.1`).

**Bug #2 (Datei wird sofort ~10 MB und ist unlesbar) — behoben, 2026-07-27:**
Ein von einem Test-PC eingesammeltes `Worker_log.txt` (10.287.876 Bytes) bestand
zu 99,96 % aus physischen NUL-Bytes (0x00) — nur die letzten ~4 KB (45 Zeilen)
waren echter, lesbarer Log-Inhalt. `fsutil sparse queryflag` bestätigte, dass
die Datei NICHT sparse ist, d.h. Windows hat diese NUL-Bytes tatsächlich auf
die Platte geschrieben (kein Loch/keine Metadaten-Anomalie).

Root Cause: In `Program.cs` wurde `FileLogSink` (öffnet die Logdatei im
`FileMode.Append`) VOR `EnsureSingleInstance()` erzeugt. Wenn `--one-instance`
aktiv ist und noch eine alte/hängende Vorgängerinstanz läuft (im vorliegenden
Log sichtbar: `"OneInstance: shutting down PID 13424..."` → IPC-Shutdown
timeout → `Kill()`), hatte diese alte Instanz zu diesem Zeitpunkt noch ihren
eigenen offenen `FileStream`-Handle auf dieselbe Logdatei — während der neue
Prozess bereits einen zweiten, unabhängigen Append-Handle auf denselben Pfad
öffnete. Zwei gleichzeitige Append-Handles auf dieselbe Datei sind unter
Windows nicht sicher: Die interne Endposition der beiden Streams kann
auseinanderlaufen, und wenn ein Handle mit einer veralteten (zu hohen)
Endposition schreibt, füllt NTFS die Lücke bis dahin mit echten Null-Bytes auf
— exakt das beobachtete Muster (großer NUL-Block, gefolgt vom tatsächlichen
Log-Text).

**Fix (bereits angewendet):**
- `Program.cs`: `FileLogSink` wird jetzt erst NACH `EnsureSingleInstance()`
  erzeugt (also erst, nachdem eine eventuell hängende Vorgängerinstanz beendet
  wurde). Der Debug-CLI-Pfad (`TryParseDebugCli`), der `EnsureSingleInstance`
  nie aufruft, öffnet den Sink weiterhin sofort — dort gibt es keine
  Mehrprozess-Konkurrenz.
- `FileLogSink.cs` `OpenWriter()`: `FileStream` wird zusätzlich mit
  `FileShare.Delete` geöffnet (statt nur `FileShare.ReadWrite`), damit ein
  anderer Prozess unsere Datei umbenennen/verschieben kann (z. B. dessen
  eigene `RotateFile()`), statt mit einer Sharing-Violation zu scheitern —
  Verteidigung in der Tiefe, falls es je wieder zu überlappenden Instanzen
  kommt.

**Praktischer Hinweis:** Falls eine alte, riesige/unlesbare Logdatei aus einer
Deployment vor diesem Fix gefunden wird: einfach löschen und mit dem
aktuellen Build neu starten.

## appsettings.json / camera-map.json

Liegen als `Config/appsettings.json` und `Config/camera-map.json` im Projekt und
werden beim Build neben die Exe kopiert (`CopyToOutputDirectory=PreserveNewest`,
siehe `.csproj`). Änderungen an den Config-Dateien im Output-Ordner sind nach
einem Rebuild weg, wenn nicht in `Config/` editiert wird.

## CLI-Flags (Auszug, siehe `StartupOptions.cs`)

`--headless` · `--log` / `--log=<pfad>` · `--fps=<n>` · `--select=<id>` /
`--camera=<id>` · `--auto-liveview` / `--no-auto-liveview` · `--no-auto-refresh` ·
`--no-auto-select` · `--tray` · `--one-instance` · intern: `--waitforpid=` /
`--delayms=` (für Self-Restart, siehe `HeadlessTrayContext.Restart()` und
`Program.ApplyRestartArgs`).

## Bekannte Stolpersteine

- `bin/` und `obj/` NICHT als Quelle der Wahrheit verwenden — nur Build-Artefakte.
- Kein Git-Repo an dieser Stelle (`Worker/` hat kein `.git`) — `git log`/`git blame`
  funktionieren hier nicht, Historie ggf. über übergeordnetes Verzeichnis prüfen.
- Ohne `--log` läuft der Worker ohne Datei-Logging (nur `RingLogger`-Ringpuffer
  im Speicher + ggf. UI).
- Exe heißt `CameraWorker.exe`, nicht `Photobox.CameraBridge.exe` — bei Prozess-
  /Log-Dateisuche danach suchen.
