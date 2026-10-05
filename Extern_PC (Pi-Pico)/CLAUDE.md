# CLAUDE.md

## Projekt: Raspberry Pi Pico – Photobox Sensor-/Münzinterface

Dieses Verzeichnis enthält die MicroPython-Firmware für den Raspberry Pi Pico
der Photobox.

Der Pico übernimmt ausschließlich die Hardware-nahe Erfassung von:

1. Präsenz über einen VL53L1X ToF-Sensor
2. Münzereignissen über einen Sintron ST-001 Münzprüfer

Der Pico ist per USB mit dem Windows-/Linux-PC verbunden und sendet einfache
zeilenbasierte Events über USB-Serial.

---

## Architektur

```text
VL53L1X
   │ I2C
   ▼
Raspberry Pi Pico
   │
   ├── kontinuierliche Distanzmessung (Free-Running, Median-Filter)
   │      └── USB Serial: DISTANCE:<mm>  (gedrosselt, ~alle 250 ms)
   │      └── USB Serial: PRESENCE       (Altprotokoll-Fallback, optional)
   │
   ├── PC fragt Geräte-Identität ab
   │      └── USB Serial: ID? -> ID:TOF_Muenzzaehler
   │
   └── ST-001 UART
          └── Münze erkannt
                 └── USB Serial: COIN:<cent>
                              │
                              ▼
                     pi_pico_core.py
                              │
                  ┌───────────┴────────────┐
                  │                        │
          DISTANCE:<mm>                COIN:<cent>
   (Schwellwert serverseitig)              │
                  │                        │
        CameraBridge LiveView      credit_core.add_credit()
```

Wichtig:

- Der Pico führt **kein Guthaben**.
- Der Pico summiert Münzen **niemals**.
- Jede erkannte Münze wird einzeln an den PC weitergemeldet.
- Der PC/Python-Server ist alleinige Quelle der Wahrheit für Guthaben.
- Der Pico trifft **keine Presence-Entscheidung mehr selbst**: er sendet
  laufend den (median-gefilterten) Distanzwert, der Schwellwert
  (`external_pc.distance_threshold_mm`) lebt serverseitig in
  `server_config.json` (WebUI-editierbar, kein Neuflashen nötig).
- Presence/Distance, Coin und der Identify-Handshake (`ID?`) laufen über
  denselben USB-Serial-Port.

---

## Dateien

### `main.py`

Hauptprogramm auf dem Pico.

Aufgaben:

- VL53L1X über I2C lesen (Free-Running, ~alle 50 ms)
- Rohwerte per Median-Filter (letzte 5 Werte) glätten
- `DISTANCE:<mm>` gedrosselt (~alle 250 ms) über USB-Serial senden
- Presence-Debounce/-Hysterese optional als Altprotokoll-Fallback
  (`ENABLE_PRESENCE_FALLBACK`, standardmäßig aus) weiterhin unterstützen
- ST-001 über UART lesen
- `value:` aus ST-001-Zeilen extrahieren
- Events über USB-Serial ausgeben
- ToF-Erreichbarkeit bei Zustandswechsel als `STATUS:TOF=OK`/`STATUS:TOF=ERR` melden
- Identify-Handshake beantworten: `ID?` -> `ID:TOF_Muenzzaehler`

### `lib/vl53l1x.py`

Externer MicroPython-Treiber für VL53L1X.

Diese Datei muss zusätzlich zu `main.py` auf dem Pico vorhanden sein, und
zwar unter `/lib/vl53l1x.py`. MicroPython hat `/lib` standardmäßig im
`sys.path`, daher findet `from vl53l1x import VL53L1X` in `main.py` den
Treiber dort automatisch, ohne zusätzliche Pfad-Logik.

Der aktuelle `main.py` erwartet diese API:

```python
from vl53l1x import VL53L1X

sensor = VL53L1X(i2c)
distance_mm = sensor.read()
```

---

# Hardware / Wiring

## VL53L1X

```text
VL53L1X         Raspberry Pi Pico

VIN / VCC  ---> 3V3 OUT
GND        ---> GND
SDA        ---> GP0
SCL        ---> GP1
XSHUT      ---> nicht benötigt
INT        ---> nicht benötigt
```

Pico Pins:

```text
GP0  = physischer Pin 1
GP1  = physischer Pin 2
3V3  = physischer Pin 36
GND  = z.B. physischer Pin 38
```

I2C:

```python
I2C(
    0,
    sda=Pin(0),
    scl=Pin(1),
    freq=400_000,
)
```

---

## ST-001 Münzprüfer

```text
ST-001          Raspberry Pi Pico

TX         ---> GP5 / UART1 RX
GND        ---> GND
```

Pico Pins:

```text
GP4 = physischer Pin 6
GP5 = physischer Pin 7
GND = z.B. physischer Pin 8
```

UART:

```python
UART(
    1,
    baudrate=115200,
    tx=Pin(4),
    rx=Pin(5),
)
```

GP4/TX wird aktuell nicht angeschlossen.

Der Pico liest ausschließlich:

```text
ST-001 TX -> Pico GP5 RX
```

---

# Elektrische Sicherheit

Pico GPIOs sind 3,3-V-Logik.

Es dürfen keine höheren Spannungen direkt auf GP0/GP1/GP5 gelangen.

Insbesondere beim ST-001 muss sichergestellt sein, dass dessen TX-Signal
3,3-V-kompatibel ist.

Falls der TX-Pegel höher ist:

- Pegelwandler verwenden
- oder geeigneten Spannungsteiler verwenden

GND von ST-001 und Pico muss gemeinsam verbunden sein.

---

# USB-Serial-Protokoll

Das Protokoll ist absichtlich sehr einfach und zeilenbasiert.

Jedes Event endet mit `\n`.

## Startup

Beim Start:

```text
READY
```

## Distance (bevorzugtes ToF-Protokoll)

```text
DISTANCE:<mm>
```

Beispiel:

```text
DISTANCE:734
```

- Ganzzahl in Millimetern.
- Wert ist der Median der letzten `TOF_MEDIAN_WINDOW` (5) Rohmessungen
  (robuster gegen einzelne Ausreißer/Reflexionen als ein Mittelwert).
- Gesendet gedrosselt alle `DISTANCE_SEND_INTERVAL_MS` (~250 ms), nicht
  bei jeder Einzelmessung (Rohmessung läuft alle `TOF_SAMPLE_MS` = 50 ms
  im Hintergrund, Free-Running, ohne Kommando vom PC).
- Die Firmware entscheidet **nicht mehr selbst** über eine
  Presence-Schwelle. Das übernimmt `pi_pico_core.py` serverseitig anhand
  von `external_pc.distance_threshold_mm` (WebUI-editierbar, kein
  Neuflashen nötig bei Schwellenänderung).

## Presence (Altprotokoll, Fallback)

```text
PRESENCE
```

Wird nur gesendet, wenn `ENABLE_PRESENCE_FALLBACK = True` in `main.py`
gesetzt ist (Standard: `False`, da `DISTANCE:` das bevorzugte Protokoll
ist). Dient ausschließlich der Abwärtskompatibilität, falls die
PC-Seite noch nicht auf `DISTANCE:` umgestellt ist — kein Zwang zur
sofortigen Migration.

Falls aktiv gilt weiterhin:

- Es darf nicht permanent `PRESENCE` gesendet werden.
- Pro Annäherung genau ein Event.
- Erst nachdem die Person wieder ausreichend weit entfernt ist, wird der
  Presence-Trigger erneut scharf.

## Coin

Beispiele:

```text
COIN:10
COIN:20
COIN:50
COIN:100
COIN:200
```

## Status (ToF-Erreichbarkeit)

```text
STATUS:TOF=OK
STATUS:TOF=ERR
```

Wird gesendet, sobald sich die Erreichbarkeit des VL53L1X ändert
(I2C-Scan findet die Adresse `0x29` bzw. Initialisierung erfolgreich vs.
Sensor nicht gefunden / wiederholte Lesefehler führen zu Reinit).

Wichtig:

- Anders als `DBG:...` ist `STATUS:TOF=...` **nicht** an `DEBUG`
  gekoppelt und wird auch im Produktivbetrieb (`DEBUG = False`) gesendet.
- Es wird nur bei tatsächlichem Zustandswechsel gesendet, nicht bei jedem
  Messzyklus (kein Spam alle 50 ms).
- Der PC (`pi_pico_core.py`) kann diesen Status nutzen, um in der
  Web-UI anzuzeigen, ob der ToF-Sensor gerade erreichbar ist.
- Münzzähler-Erreichbarkeit wird bewusst **nicht** über ein analoges
  `STATUS:COIN=...`-Event abgebildet: Der ST-001 wird nur passiv über
  UART-RX gelesen (kein Rückkanal/Handshake), daher gibt es keine
  echte Erreichbarkeitsprüfung wie beim I2C-Scan des ToF.

Der Zahlenwert ist der Münzwert in Cent.

Beispiel:

```text
COIN:100
```

bedeutet:

```text
1,00 EUR
```

## Identify-Handshake

Der PC-Server (Windows und Linux) muss beim Start alle verfügbaren
seriellen Schnittstellen durchsuchen können, um herauszufinden, an
welcher der Pico hängt (`identify_external_pc_core.py`,
`list_identify()`/`check_identify()`).

Empfängt der Pico über USB-Serial die Zeile:

```text
ID?
```

antwortet er mit:

```text
ID:TOF_Muenzzaehler
```

Manuell testbar über eine serielle Konsole (Thonny, PuTTY, `mpremote`
etc.): `ID?` eintippen sollte `ID:TOF_Muenzzaehler` liefern, bevor die
PC-seitige Integration getestet wird.

---

# ST-001 Eingabeformat

Erwartete ST-001-Zeile:

```text
value:100,frequency:213
```

Relevant ist ausschließlich:

```text
value:100
```

Der Pico extrahiert daraus:

```text
100
```

und sendet:

```text
COIN:100
```

`frequency:` wird derzeit ignoriert.

---

# Ganz wichtige Architekturregel

## Niemals Guthaben auf dem Pico speichern

Nicht implementieren:

```python
credit += coin_value
```

Nicht implementieren:

- Guthaben-Dateien auf dem Pico
- Flash-Speicherung des Guthabens
- Summierung mehrerer Münzen
- Guthaben-Restore nach Pico-Neustart

Richtig:

```text
Münze
  -> ST-001
  -> Pico
  -> COIN:100
  -> PC
  -> pi_pico_core.py
  -> credit_core.add_credit(100)
```

Der Pico bleibt zustandslos.

---

# ToF-Distanzmessung (Median-Filter, rollierendes Fenster)

Aktuelle Konfiguration in `main.py`:

```python
TOF_SAMPLE_MS = 50                  # Rohmessintervall (~20 Messungen/s)
TOF_MEDIAN_WINDOW = 5                # Anzahl Rohwerte für den Median-Filter
DISTANCE_SEND_INTERVAL_MS = 250      # Sendedrosselung für DISTANCE:<mm>
```

Bedeutung:

- Der VL53L1X wird kontinuierlich (Free-Running) alle `TOF_SAMPLE_MS`
  abgefragt, ohne auf ein "jetzt messen"-Kommando vom PC zu warten
  (`pi_pico_core.py` sendet nie ein Mess-Kommando, nur passiv `ID?` für
  den Identify-Handshake).
- Die letzten `TOF_MEDIAN_WINDOW` gültigen Rohwerte landen in einem
  rollierenden Fenster (Ringpuffer). Ungültige Werte
  (außerhalb `TOF_MIN_VALID_MM`/`TOF_MAX_VALID_MM`) werden verworfen und
  fließen nicht in den Puffer ein.
- Aus dem Fenster wird laufend der **Median** gebildet (robuster gegen
  einzelne Ausreißer/Reflexionen als ein gleitender Mittelwert).
- Eine `DISTANCE:<mm>`-Zeile wird nicht bei jeder Rohmessung gesendet,
  sondern gedrosselt auf `DISTANCE_SEND_INTERVAL_MS` (~250 ms) — das
  hält die Reaktionszeit niedrig, ohne bei jeder 50-ms-Rohmessung eine
  eigene Zeile zu senden.
- Serielle Bandbreite ist dabei kein Engpass: bei 115200 Baud
  (~11.500 Byte/s) ist eine `DISTANCE:1234` Zeile (~15 Byte) alle
  250 ms verschwindend klein, genug Raum bleibt für gelegentliche
  `COIN:<wert>`-Zeilen dazwischen (`pi_pico_core.py` dispatcht bereits
  zeilenweise nach Präfix).
- **Kein Firmware-seitiger Schwellenwert mehr:** Ob `DISTANCE:<mm>`
  einer "Person da" entspricht, entscheidet ausschließlich der PC-Server
  anhand von `external_pc.distance_threshold_mm` (WebUI-editierbar).

## Presence-Fallback (nur falls `ENABLE_PRESENCE_FALLBACK = True`)

Für den Fall, dass die PC-Seite noch nicht auf `DISTANCE:` umgestellt
ist, bleibt die alte Presence-Debounce/Hysterese-Logik im Code:

```python
PRESENCE_DISTANCE_MM = 3000
RELEASE_DISTANCE_MM = 3400
PRESENCE_STABLE_MS = 500
RELEASE_STABLE_MS = 1000
```

### Einschalten

Objekt innerhalb `<= 3000 mm` muss für mindestens `500 ms` stabil
erkannt werden, dann wird `PRESENCE` gesendet.

### Wieder scharf schalten

Nach einem Presence-Event wird kein weiteres Presence-Event gesendet,
solange die Person weiterhin vor der Box steht. Erst wenn die Entfernung
mindestens `>= 3400 mm` für `1000 ms` beträgt, wird der Trigger erneut
scharf. Die Differenz zwischen 3000 mm und 3400 mm ist die Hysterese,
sie verhindert Flattern am Grenzbereich.

Bei Umstellung auf `DISTANCE:` übernimmt der Median-Filter (siehe oben)
effektiv diese Debounce-Rolle, der PC-Server macht die eigentliche
Schwellwert-Entscheidung.

---

# Aufgabenverteilung Pico vs. PC

## Pico

Der Pico darf:

- VL53L1X lesen (Free-Running, kontinuierlich)
- Sensorwerte per Median-Filter glätten
- `DISTANCE:<mm>` gedrosselt senden
- Presence debounce/Hysterese nur als optionaler Altprotokoll-Fallback
  (`ENABLE_PRESENCE_FALLBACK`) durchführen
- ST-001 UART lesen
- Münzwert parsen
- `PRESENCE` senden (nur Fallback)
- `COIN:<cent>` senden
- `STATUS:TOF=OK`/`STATUS:TOF=ERR` senden
- `ID?` empfangen und mit `ID:TOF_Muenzzaehler` beantworten

Der Pico darf nicht:

- HTTP sprechen
- CameraBridge direkt ansprechen
- Guthaben verwalten
- Guthaben addieren
- Guthaben abbuchen
- `credit.json` schreiben
- den Presence-/Distanz-Schwellwert selbst festlegen (lebt in
  `server_config.json`)

## PC / Python-Server

`pi_pico_core.py`:

- öffnet USB-Serial
- hält nur einen Reader für den Pico
- reconnectet bei USB-Aussetzern
- unterscheidet `DISTANCE:`, `PRESENCE` (Fallback) und `COIN:`
- wertet `DISTANCE:<mm>` gegen `external_pc.distance_threshold_mm` aus
- Distanz unter Schwellwert / Presence -> LiveView HTTP POST
- Coin -> `credit_core.add_credit()`

`identify_external_pc_core.py`:

- durchsucht verfügbare serielle Schnittstellen (Windows/Linux)
- sendet `ID?`, erwartet `ID:TOF_Muenzzaehler` zur Identifikation

`credit_core.py`:

- Guthaben im RAM
- `threading.Lock`
- addieren / später abbuchen
- atomar `credit.json` schreiben
- Audit-Log schreiben

---

# CameraBridge

Presence soll auf PC-Seite später auslösen:

```http
POST http://127.0.0.1:8052/api/liveview/start
```

Der Pico selbst kennt diesen Endpoint nicht.

---

# Fehlerverhalten

Die Firmware soll robust sein.

## VL53L1X nicht vorhanden

Der Pico darf wegen eines fehlenden Sensors nicht dauerhaft crashen.

Vorgesehen:

- Initialisierung versuchen
- bei Fehler weiterlaufen
- nach einigen Sekunden erneut versuchen

Dadurch können weiterhin ST-001-Münzereignisse empfangen werden.

## ToF Read Error

Bei wiederholten Leseproblemen:

- Sensorinstanz verwerfen
- neu initialisieren

## UART

UART darf nicht blockierend arbeiten.

ST-001-Daten werden gepuffert, bis eine komplette Zeile vorhanden ist.

CR/LF/CRLF sollen toleriert werden.

---

# Debugging

In `main.py`:

```python
DEBUG = False
```

Für Entwicklung kann gesetzt werden:

```python
DEBUG = True
```

Dann können zusätzliche Zeilen erscheinen:

```text
DBG:...
```

Produktiv normalerweise:

```python
DEBUG = False
```

damit `pi_pico_core.py` nur die relevanten Events erhält.

Der PC-Code sollte trotzdem unbekannte Zeilen ignorieren können.

---

# Erwartetes Verhalten bei Boot

Beispiel:

```text
READY
```

PC identifiziert den Port:

```text
ID?
ID:TOF_Muenzzaehler
```

Laufender Betrieb, alle ~250 ms:

```text
DISTANCE:1830
DISTANCE:1825
DISTANCE:820
DISTANCE:734
```

1-Euro-Münze:

```text
COIN:100
```

50-Cent-Münze:

```text
COIN:50
```

(Nur falls `ENABLE_PRESENCE_FALLBACK = True`: zusätzlich einmalig
`PRESENCE`, wenn die Person stabil unter der Firmware-Schwelle bleibt,
und erneut nach ausreichendem Abstand.)

---

# Entwicklungsregeln

Bei Änderungen an dieser Firmware:

1. USB-Protokoll rückwärtskompatibel halten.
2. `PRESENCE` exakt großgeschrieben lassen (Altprotokoll-Fallback).
3. `DISTANCE:` exakt großgeschrieben lassen (bevorzugtes ToF-Protokoll).
4. `COIN:` exakt großgeschrieben lassen.
5. `ID?` / `ID:TOF_Muenzzaehler` exakt so lassen (Identify-Handshake).
6. Münzwert immer als Integer-Cent übertragen.
7. Pico niemals Guthaben summieren lassen.
8. Keine Netzwerk-/HTTP-Logik in die Pico-Firmware einbauen.
9. Kein Presence-/Distanz-Schwellwert mehr fest in der Firmware —
   Schwellwertentscheidung bleibt beim PC-Server.
10. Distanzmessung/-senden darf den Coin-Reader nicht blockieren.
11. Coin darf die ToF-Messung nicht blockieren.
12. Keine langen `sleep()`-Aufrufe verwenden.
13. Bei Sensorfehlern den Rest der Firmware weiterlaufen lassen.

---

# Zukünftige Erweiterungen

Mögliche spätere Events dürfen nach demselben Schema ergänzt werden:

```text
EVENTNAME
```

oder:

```text
EVENTNAME:value
```

Beispiele:

```text
BUTTON:START
DOOR:OPEN
TEMP:42
```

Vor Erweiterungen muss geprüft werden, ob die PC-Seite das neue Event
ignorieren oder verarbeiten soll.

Bestehende Events dürfen nicht umbenannt werden.

---

# Deployment auf Pico

Auf dem Pico müssen mindestens liegen:

```text
/
├── main.py
└── lib/
    └── vl53l1x.py
```

`main.py` wird von MicroPython beim Boot automatisch gestartet.

Für Änderungen:

1. Pico per USB verbinden
2. MicroPython-Firmware muss installiert sein
3. `main.py` kopieren
4. `lib/vl53l1x.py` kopieren (Ordnerstruktur beibehalten)
5. Pico neu starten
6. USB-Serial prüfen

Erwartete erste Zeile:

```text
READY
```

---

# Aktueller Projektstatus

Die Pico-Seite ist auf folgende Zielarchitektur ausgelegt:

```text
VL53L1X -> Pico -> DISTANCE:<mm> (Median, gedrosselt) -> PC
                 -> PRESENCE (Fallback, optional)      -> PC
ST-001  -> Pico -> COIN:x                              -> PC
PC      -> Pico -> ID?  =>  Pico -> PC: ID:TOF_Muenzzaehler
```

Die PC-Seite mit:

```text
pi_pico_core.py
identify_external_pc_core.py
credit_core.py
credit.json
CameraBridge-Trigger
```

ist bereits fertig implementiert und gehört nicht in diese
MicroPython-Firmware — dieses Repo muss nur das oben beschriebene
Protokoll liefern, damit sie funktioniert.
