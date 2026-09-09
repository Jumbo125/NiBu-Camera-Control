Patch-Inhalt
============

Geänderte Dateien:
- Program.cs
- BridgeWorkerAdapter.cs

Was der Patch macht
-------------------
1) Debug-CLI direkt im Worker eingebaut.
   Unterstützte Befehle:
   - capture-default
   - capture-user
   - --debug-capture-default
   - --debug-capture-user

2) Debug-CLI verwendet denselben Capture-Pfad wie der Worker:
   Program -> CameraHost/BridgeWorkerAdapter -> CaptureToFileAsync(...)

3) Zusätzliche Logs in BridgeWorkerAdapter:
   - ApplySettings Fehler werden geloggt
   - ResetAfterShoot Restore-Fehler werden geloggt

4) Offensichtlichen Duplicate-Run in Program.cs bereinigt:
   Es gab zwei Application.Run(...) Aufrufe hintereinander.

Beispiele
---------
Standard/aktuelle Kameraeinstellungen:
Photobox.CameraBridge.exe capture-default --file default.jpg --log

User-Settings ohne Reset:
Photobox.CameraBridge.exe capture-user --file user_noreset.jpg --iso 100 --shutter 1/60 --wb Auto --reset-after=false --log

User-Settings mit Reset:
Photobox.CameraBridge.exe capture-user --file user_reset.jpg --iso 100 --shutter 1/60 --wb Auto --reset-after=true --log

Optionen
--------
--file <name>
--path <voller oder relativer pfad>
--overwrite
--camera <id>
--select <id>
--serial <serial>
--iso <wert>
--shutter <wert>
--wb <wert>
--aperture <wert>
--exposure <wert>
--reset-after=true|false
--skip-refresh
--log oder --log=<datei>

Wichtig
-------
Diesen Debug-Modus nicht parallel zu einer bereits laufenden Worker-Instanz verwenden,
wenn dieselbe Kamera gleichzeitig offen wäre. Für den Test am besten die normale Worker-Instanz beenden
und dann den Debug-Befehl einmalig ausführen.
