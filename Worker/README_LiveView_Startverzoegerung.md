# Live-View Startverzögerung – Analyse und Fehlersuche

## Ausgangssituation

Die Photobox-Software besteht aus mehreren Komponenten:

```text
Kamera
  ↓
Camera Worker (C# / digiCamControl-basierte SDK)
  ↓
API Server (C#)
  ↓
WebUI
```

Der **Camera Worker besitzt bzw. steuert die Kamera** und stellt permanent Live-View-Frames zur Verfügung, solange Live-View aktiv ist.

Der **API Server erzeugt den Live-View nicht selbst**, sondern streamt lediglich die vom Worker bereitgestellten Frames an die WebUI.

Die **WebUI stellt diesen Stream dar**.

Der eigentliche Capture-Vorgang verwendet bewusst **NICHT** das aktuelle Live-View-Bild, sondern löst die Kamera normal aus. Dadurch wird ein qualitativ hochwertiges Foto mit den eingestellten Kameraeinstellungen aufgenommen, z. B.:

```text
Belichtungszeit: 1/125 s
```

Bei einem Capture-Vorgang werden drei Fotos aufgenommen und anschließend zu einem fertigen Bild zusammengesetzt.

---

# Beobachtetes Verhalten

## Fall 1 – Live-View läuft bereits

Wenn der Live-View bereits aktiv ist:

```text
Capture starten
→ Live-View erscheint praktisch sofort
→ Capture-Sequenz läuft schnell
```

Das Verhalten ist zufriedenstellend.

---

## Fall 2 – Live-View wurde wegen Inaktivität beendet

Der Live-View wird aktuell nach längerer Inaktivität bewusst beendet, beispielsweise nach ca. 30–60 Minuten.

Beim nächsten Capture:

```text
WebUI
  ↓
API: "Live-View starten"
  ↓
Worker erhält Kommando
  ↓
Worker startet Live-View über Kamera-SDK
  ↓
erste Frames werden geliefert
  ↓
API streamt diese
  ↓
WebUI zeigt Live-View
```

Bis der Live-View sichtbar ist, vergehen ungefähr:

```text
2–3 Sekunden
```

---

# Vergleich mit dslrBooth

Mit derselben grundsätzlichen Anwendungssituation scheint dslrBooth den Live-View deutlich schneller starten zu können.

Beobachtung:

```text
Live-View vorher nicht sichtbar
→ Capture/Session wird gestartet
→ Live-View innerhalb ungefähr einer Sekunde sichtbar
```

Das bedeutet:

**Die Kamera selbst benötigt vermutlich nicht zwingend 2–3 Sekunden, um einen ersten Live-View-Frame zu liefern.**

Es gibt daher wahrscheinlich Optimierungspotenzial im Worker oder in der Art, wie die verwendete SDK angesprochen wird.

---

# Wahrscheinlichste Ursache

Der wichtigste Verdacht liegt derzeit beim:

```text
Camera Worker
```

und insbesondere beim Ablauf zwischen:

```text
StartLiveView()
```

und:

```text
erster tatsächlich verfügbarer Live-View-Frame
```

Der API Server und die WebUI sind vermutlich nicht die Hauptursache.

Wenn der Live-View bereits läuft, funktioniert die komplette Kette nämlich schnell.

---

# Der Monolith alleine erklärt die Verzögerung wahrscheinlich nicht

dslrBooth ist vermutlich stärker monolithisch aufgebaut:

```text
SDK
↓
Camera Controller
↓
Live-View
↓
UI
```

Die eigene Software besitzt dagegen zusätzliche Übergänge:

```text
SDK
↓
Worker
↓
API
↓
Browser
```

Diese zusätzlichen Übergänge können eine gewisse Verzögerung verursachen.

Sie sollten normalerweise aber **keine zusätzlichen 2–3 Sekunden** verursachen.

Typischerweise wären hier eher Millisekunden bis wenige hundert Millisekunden zu erwarten.

Deshalb sollte zuerst überprüft werden, ob der Worker bereits beim Starten des Live-Views viel Zeit verliert.

---

# Wichtigster Test

Der Live-View-Start muss im Worker exakt vermessen werden.

Nicht nur normale Log-Zeitstempel verwenden, sondern möglichst:

```csharp
System.Diagnostics.Stopwatch
```

oder hochauflösende Zeitmessung.

Folgende Ereignisse sollten geloggt werden:

```text
T0  API fordert Live-View an
T1  Worker erhält Kommando
T2  Worker beginnt StartLiveView
T3  SDK-StartLiveView-Aufruf beginnt
T4  SDK-StartLiveView-Aufruf beendet
T5  erster Frame/Event von der Kamera/SDK
T6  Frame vollständig verfügbar
T7  Frame als LatestFrame gespeichert
T8  API kann Frame abrufen
T9  WebUI erhält ersten Frame
```

Beispiel:

```text
[LV] +0 ms       StartLiveView command received
[LV] +3 ms       entering StartLiveView()
[LV] +8 ms       SDK LiveView start called
[LV] +22 ms      SDK LiveView start returned
[LV] +1845 ms    first SDK frame callback
[LV] +1861 ms    frame decoded
[LV] +1864 ms    LatestFrame assigned
[LV] +1905 ms    API delivered first frame
```

Dann wäre eindeutig:

```text
~1,8 Sekunden entstehen zwischen SDK-Start und erstem Kamera-Frame.
```

In diesem Fall sind API und WebUI praktisch unschuldig.

---

# Alternativer Fehlerfall

Falls das Log dagegen so aussieht:

```text
[LV] +0 ms       command received
[LV] +10 ms      SDK LiveView start
[LV] +350 ms     first SDK frame
[LV] +2300 ms    LatestFrame assigned
```

liegt das Problem eindeutig im Worker.

Dann sollte nach folgenden Ursachen gesucht werden:

- blockierende Locks
- `Thread.Sleep()`
- Polling-Schleifen
- unnötige Retry-Zyklen
- Task/Thread-Synchronisation
- Bitmap-Konvertierung
- JPEG-Encoding
- MemoryStream-Verarbeitung
- Warteschlangen
- unnötiges Kopieren großer Byte-Arrays
- UI-/API-Abhängigkeiten
- Garbage Collection
- wiederholte Initialisierung von Kameraobjekten
- erneutes Einlesen zahlreicher Kamera-Properties
- erneutes Erstellen von Worker-/Stream-Threads

---

# Besonders nach Polling suchen

Ein sehr wichtiger Kandidat sind Konstruktionen wie:

```csharp
while (!LiveViewReady)
{
    Thread.Sleep(500);
}
```

Bei:

```text
500 ms Polling
```

kann alleine die ungünstige Synchronisation bereits deutlich wahrnehmbar werden.

Noch problematischer wäre:

```csharp
Thread.Sleep(1000);
```

oder:

```text
Retry
→ 1 Sekunde warten
→ erneut prüfen
```

Wenn zwei solcher Wartezyklen auftreten, entstehen sehr schnell die beobachteten:

```text
2–3 Sekunden
```

---

# Event statt Polling bevorzugen

Wenn die verwendete SDK einen Frame-Callback/Event liefert, sollte möglichst darauf reagiert werden.

Beispielprinzip:

```text
StartLiveView()
↓
SDK meldet ersten Frame
↓
Frame sofort speichern
↓
API kann ihn ausliefern
```

Nicht:

```text
StartLiveView()
↓
500 ms warten
↓
prüfen
↓
500 ms warten
↓
prüfen
```

---

# Kamera-Session möglichst offen halten

Ein weiterer wichtiger Punkt:

Live-View deaktivieren bedeutet nicht unbedingt, dass die komplette Kamera-Session geschlossen werden muss.

Ideal wäre:

```text
Kamera verbunden
SDK Session offen
CameraDevice vorhanden
LiveView OFF
```

und später:

```text
LiveView ON
```

Nicht:

```text
LiveView OFF
↓
CameraDevice teilweise abbauen
↓
Session/Properties neu initialisieren
↓
LiveView ON
```

Es sollte daher überprüft werden, was `StopLiveView()` aktuell tatsächlich macht.

---

# StopLiveView untersuchen

Besonders wichtig ist die Frage:

**Was wird beim Abschalten des Live-Views im Worker alles zerstört?**

Möglicherweise wird aktuell mehr beendet als nötig.

Zu prüfen:

```text
Bleibt die SDK Session offen?
Bleibt CameraDevice bestehen?
Bleibt die Kamera als aktive Kamera ausgewählt?
Bleiben Events/Callbacks registriert?
Bleibt der LiveView-Workerthread bestehen?
Bleiben Buffer bestehen?
```

Optimal wäre:

```text
StopLiveView()
→ nur Kamera-LiveView stoppen
→ Frames nicht mehr anfordern
→ Session ansonsten erhalten
```

Beim erneuten Start:

```text
StartLiveView()
→ Kamera LiveView ON
→ erster Frame
```

---

# Erster Frame sollte sofort veröffentlicht werden

Der Worker sollte beim Start nicht darauf warten, dass mehrere Frames vorliegen.

Nicht:

```text
Frame 1 ignorieren
Frame 2 ignorieren
Frame 3 prüfen
Frame 4 veröffentlichen
```

sondern möglichst:

```text
erster gültiger Frame
→ LatestFrame
→ sofort verfügbar
```

Falls der erste Frame manchmal ungültig ist, sollte dies gezielt geprüft werden.

---

# Frame-Verarbeitung prüfen

Beim ersten Frame können unnötige Verarbeitungsschritte Zeit kosten.

Beispielsweise:

```text
SDK Frame
↓
Bitmap erzeugen
↓
Bitmap kopieren
↓
Resize
↓
JPEG Encoding
↓
MemoryStream
↓
byte[]
↓
weiterer Buffer
↓
API
```

Für Live-View sollte möglichst wenig verarbeitet werden.

Optimal:

```text
SDK Frame
↓
notwendige minimale Konvertierung
↓
LatestFrame
```

Die Capture-Fotos sind davon unabhängig und benötigen natürlich volle Qualität.

---

# LatestFrame-Prinzip

Der API Server sollte nicht darauf warten müssen, dass der Worker für jeden Client einen neuen Frame erzeugt.

Empfohlene Struktur:

```text
                  ┌──> API Client
SDK → Worker → LatestFrame
                  ├──> API Client
                  └──> API Client
```

Der Worker überschreibt ständig:

```text
LatestFrame
```

mit dem neuesten Bild.

Der API Server liefert einfach das aktuellste Bild aus.

Dadurch besteht keine Synchronisationsabhängigkeit zwischen:

```text
Browser
API
Kamera
```

---

# API und Worker entkoppeln

Ein Capture- oder LiveView-Command sollte möglichst unmittelbar an den Worker weitergegeben werden.

Beispiel:

```text
POST /liveview/start
↓
Command Queue
↓
Camera Worker
↓
SDK
```

Keine unnötige Abhängigkeit von:

```text
HTTP Stream gestartet?
Browser verbunden?
alter Frame gelesen?
vorheriger Request beendet?
```

---

# Live-View für Events dauerhaft aktiv lassen

Als kurzfristiger Workaround ist es sinnvoll, bei den ersten realen Events den Live-View während des aktiven Veranstaltungsbetriebs einfach laufen zu lassen.

Beispielsweise:

```text
Photobox startet
↓
Live-View starten
↓
mehrere Stunden aktiv lassen
↓
Photobox wird beendet
↓
Live-View stoppen
```

Das vermeidet den kritischen Neustart während einer Benutzerinteraktion.

Dabei beobachten:

```text
Kameratemperatur
Worker-Stabilität
RAM-Verbrauch
CPU-Verbrauch
Frame-Aussetzer
USB-Stabilität
```

Wenn Kamera und Worker über mehrere Stunden stabil bleiben, ist dies zunächst ein brauchbarer Produktions-Workaround.

---

# Langzeittest des Workers

Der Worker sollte testweise beispielsweise mehrere Stunden mit Live-View betrieben werden.

Dabei regelmäßig loggen:

```text
Uptime
RAM
Private Memory
Working Set
GC Memory
LiveView FPS
Frames empfangen
Frames verworfen
letzter Frame-Zeitpunkt
SDK Fehler
USB/Kamera Disconnects
```

Beispiel:

```text
[HEALTH]
Uptime: 04:13:22
Frames: 912345
LastFrameAge: 33 ms
RAM: 184 MB
LiveViewFPS: 24.8
CameraConnected: true
LiveViewActive: true
```

Besonders wichtig:

Der RAM-Verbrauch sollte nicht permanent steigen.

---

# Möglicher Bitmap-/MemoryStream-Leak

Da Live-View sehr viele Bilder erzeugt, unbedingt sicherstellen:

```csharp
Bitmap
Image
MemoryStream
Stream
```

werden korrekt freigegeben.

Beispielsweise mit:

```csharp
using
```

oder:

```csharp
Dispose()
```

Ein kleiner Leak pro Frame kann nach mehreren Stunden massiv werden.

---

# Was wahrscheinlich NICHT das Hauptproblem ist

Momentan eher unwahrscheinlich:

## WebUI

Wenn Live-View bereits läuft, stellt die WebUI ihn schnell dar.

Daher scheint die Darstellung grundsätzlich performant zu sein.

## HTTP/API grundsätzlich

Auch der API-Server funktioniert schnell, sobald Frames vorhanden sind.

Deshalb dürfte die reine HTTP-/Streaming-Kommunikation nicht für 2–3 Sekunden verantwortlich sein.

## Capture-Foto

Der eigentliche Foto-Capture ist schnell und funktioniert unabhängig vom Live-View.

Der Capture verwendet bewusst das echte Kamerafoto und nicht das LiveView-JPEG.

Das ist korrekt und sollte so bleiben.

---

# Wahrscheinlichkeits-Ranking

Aktueller Verdacht:

```text
1. Worker wartet zu lange auf LiveViewReady / ersten Frame
2. SDK-Initialisierung des Live-Views
3. Polling/Thread.Sleep/Retry im Worker
4. StopLiveView baut zu viel Kamera-Zustand ab
5. unnötige Verarbeitung des ersten Frames
6. Lock/Thread-Synchronisierung
7. API-Streaming-Initialisierung
8. Browser/WebUI
```

---

# Entscheidender Vergleichstest

dslrBooth zeigt, dass dieselbe Kamera offenbar schneller in den Live-View wechseln kann.

Deshalb sollte ein Test durchgeführt werden:

```text
Kamera frisch verbunden
LiveView OFF
↓
Worker StartLiveView()
↓
Zeit bis erster SDK Frame
```

Dieser Wert ist entscheidend.

Wenn:

```text
erster SDK Frame nach 2–3 Sekunden
```

kommt, liegt das Problem wahrscheinlich im SDK-Aufruf beziehungsweise dessen Verwendung.

Wenn:

```text
erster SDK Frame nach < 1 Sekunde
```

kommt, aber die WebUI erst nach 2–3 Sekunden etwas zeigt, liegt ein klar optimierbarer Fehler in Worker/API vor.

---

# Ziel

Langfristiges Ziel:

```text
StartLiveView angefordert
↓
erster sichtbarer Frame < 1 Sekunde
```

Idealerweise:

```text
300–800 ms
```

sofern die Kamera/SDK dies zulässt.

---

# Nächster Entwicklungsschritt

Noch keine große Architekturänderung durchführen.

Zuerst ausschließlich detailliertes Timing einbauen.

Insbesondere:

```text
StartLiveView Command
SDK Call Start
SDK Call Return
First SDK Frame
Frame Conversion Finished
LatestFrame Assignment
API First Delivery
```

Erst danach optimieren.

Ohne diese Zeitpunkte würde man derzeit nur vermuten, wo die 2–3 Sekunden verloren gehen.

---

# Kurzfassung für Codex / Claude

> Analysiere den C# Camera Worker auf Verzögerungen beim Start des Live-Views.
>
> Der Live-View benötigt nach längerer Inaktivität ungefähr 2–3 Sekunden, bis er in der WebUI erscheint. Wenn Live-View bereits aktiv ist, funktioniert alles schnell.
>
> Die Architektur ist Kamera → C# Camera Worker mit digiCamControl-basierter SDK → C# API Server → WebUI.
>
> Der API Server streamt nur die Frames des Workers und steuert die Kamera nicht direkt.
>
> Bitte untersuche insbesondere `StartLiveView()` und `StopLiveView()`.
>
> Suche nach `Thread.Sleep`, Polling, Retries, Locks, synchronen Waits, Task-Wartezeiten, unnötiger Kamera-Reinitialisierung, Property-Reads, Bitmap/JPEG-Konvertierungen sowie unnötigem Erstellen/Beenden von Threads oder Buffern.
>
> Prüfe außerdem, ob `StopLiveView()` mehr Kamera-/SDK-Zustand abbaut als notwendig.
>
> Ergänze hochauflösendes Timing mit `Stopwatch` für:
>
> - StartLiveView Command received
> - SDK LiveView call start
> - SDK LiveView call returned
> - first SDK frame received
> - frame conversion finished
> - LatestFrame assigned
> - first frame available for API
>
> Die bestehende Capture-Logik darf nicht auf LiveView-Frames umgestellt werden. Die Kamera soll weiterhin normale hochauflösende Fotos aufnehmen.
>
> Ziel ist zunächst ausschließlich festzustellen, an welcher Stelle die 2–3 Sekunden Verzögerung entstehen.
