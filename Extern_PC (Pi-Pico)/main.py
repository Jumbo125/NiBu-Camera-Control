# main.py
# Raspberry Pi Pico / MicroPython
#
# Aufgabe:
#   - VL53L1X über I2C lesen
#   - stabile Präsenz -> "PRESENCE" über USB-Serial senden
#   - ST-001 über UART lesen
#   - value:<cent> -> "COIN:<cent>" über USB-Serial senden
#
# WICHTIG:
#   Der Pico führt KEIN Guthaben. Jede Münze wird einzeln an den PC gemeldet.

from machine import Pin, I2C, UART
import time
import sys

try:
    import select
except ImportError:
    import uselect as select

try:
    from vl53l1x import VL53L1X
except ImportError:
    VL53L1X = None


# ---------------------------------------------------------------------------
# KONFIGURATION
# ---------------------------------------------------------------------------

# VL53L1X / I2C0
TOF_I2C_ID = 0
TOF_SDA_PIN = 0          # GP0
TOF_SCL_PIN = 1          # GP1
TOF_I2C_FREQ = 400_000

# ToF: kontinuierliches Free-Running-Messen, Median-Filter, gedrosseltes Senden
TOF_SAMPLE_MS = 50                  # Rohmessintervall (~20 Messungen/s)
TOF_MEDIAN_WINDOW = 5                # Anzahl Rohwerte für den Median-Filter
DISTANCE_SEND_INTERVAL_MS = 250      # Sendedrosselung für DISTANCE:<mm>
TOF_MIN_VALID_MM = 40
TOF_MAX_VALID_MM = 5000
TOF_RETRY_MS = 5000
TOF_ERRORS_BEFORE_REINIT = 5

# Altprotokoll PRESENCE (Fallback, standardmäßig aus): der Schwellwert lebt
# jetzt serverseitig (external_pc.distance_threshold_mm). Nur aktivieren,
# falls der PC-Server noch nicht auf DISTANCE: umgestellt ist.
ENABLE_PRESENCE_FALLBACK = False
PRESENCE_DISTANCE_MM = 3000       # Person gilt bis 3,0 m als "da"
RELEASE_DISTANCE_MM = 3400        # Hysterese: erst ab 3,4 m wieder freigeben
PRESENCE_STABLE_MS = 500          # muss 0,5 s stabil erkannt werden
RELEASE_STABLE_MS = 1000          # muss 1,0 s wieder frei sein

# ST-001 / UART1
ST_UART_ID = 1
ST_UART_BAUD = 115200
ST_UART_TX_PIN = 4       # GP4; wird nicht angeschlossen
ST_UART_RX_PIN = 5       # GP5; ST-001 TX kommt hierhin
ST_MAX_LINE_BYTES = 256

# PC-Identifikation
# Der PC-Server (Windows/Linux) kann beim Durchsuchen aller seriellen
# Schnittstellen dieses Kommando senden, um den Pico eindeutig zu erkennen.
PC_IDENTIFY_COMMAND = "ID?"
PC_IDENTITY_RESPONSE = "ID:TOF_Muenzzaehler"
PC_CMD_MAX_CHARS = 64

DEBUG = False


# ---------------------------------------------------------------------------
# AUSGABE ZUM PC ÜBER USB-SERIAL
# ---------------------------------------------------------------------------

def usb_send(message):
    # print() läuft beim Pico-MicroPython über die USB-REPL/USB-Serial.
    print(message)


def debug(message):
    if DEBUG:
        usb_send("DBG:" + str(message))


# ---------------------------------------------------------------------------
# STATUS-EVENTS (nicht an DEBUG gekoppelt)
# ---------------------------------------------------------------------------

_last_tof_status = None


def report_tof_status(ok):
    """
    Meldet Änderungen der ToF-Erreichbarkeit über USB-Serial.
    Wird unabhängig von DEBUG gesendet, nur bei tatsächlichem
    Zustandswechsel (kein Spam bei jedem Messzyklus).
    """
    global _last_tof_status
    status = "OK" if ok else "ERR"
    if status == _last_tof_status:
        return
    _last_tof_status = status
    usb_send("STATUS:TOF={}".format(status))


# ---------------------------------------------------------------------------
# PC-KOMMANDOS ÜBER USB-SERIAL (z.B. Schnittstellen-Identifikation)
# ---------------------------------------------------------------------------

def process_pc_command(poll_obj, buffer):
    """
    Liest nicht-blockierend eingehende USB-Serial-Zeichen vom PC.

    Aktuell wird nur ein Kommando unterstuetzt: PC_IDENTIFY_COMMAND ("ID?").
    Damit kann der PC-Server (Windows/Linux) beim Durchsuchen aller
    seriellen Schnittstellen erkennen, welche davon der Pico ist.
    """
    while poll_obj.poll(0):
        ch = sys.stdin.read(1)
        if not ch:
            break

        if ch in ("\n", "\r"):
            line = buffer.strip()
            buffer = ""
            if line == PC_IDENTIFY_COMMAND:
                usb_send(PC_IDENTITY_RESPONSE)
            elif line:
                debug("PC cmd ignored: {}".format(line))
        else:
            buffer += ch
            if len(buffer) > PC_CMD_MAX_CHARS:
                buffer = ""

    return buffer


# ---------------------------------------------------------------------------
# ST-001
# ---------------------------------------------------------------------------

def parse_st001_line(raw_line):
    """
    Erwartet z.B.:
        value:50,frequency:213

    Gibt den Cent-Wert als int zurück oder None.
    """
    try:
        line = raw_line.decode("ascii", "ignore").strip()
    except Exception:
        return None

    marker = "value:"
    pos = line.find(marker)
    if pos < 0:
        return None

    value_text = line[pos + len(marker):]
    if "," in value_text:
        value_text = value_text.split(",", 1)[0]

    value_text = value_text.strip()

    try:
        value = int(value_text)
    except (ValueError, TypeError):
        return None

    # Nur grobe Plausibilitätsprüfung.
    # Welche Münzwerte erlaubt sind, entscheidet später die PC-Seite.
    if value <= 0 or value > 10000:
        return None

    return value


def process_st001_uart(uart, buffer):
    """
    Liest alle aktuell verfügbaren UART-Daten.
    Jede vollständige Zeile wird unabhängig verarbeitet.
    """
    if not uart.any():
        return buffer

    chunk = uart.read()
    if not chunk:
        return buffer

    debug("ST001 raw: {}".format(chunk))

    # CR, LF und CRLF robust behandeln.
    chunk = chunk.replace(b"\r", b"\n")
    buffer += chunk

    # Schutz gegen kaputte/nie abgeschlossene UART-Zeilen.
    if len(buffer) > ST_MAX_LINE_BYTES * 4:
        debug("ST001 buffer overflow, verworfen: {}".format(buffer))
        buffer = b""

    while b"\n" in buffer:
        raw_line, buffer = buffer.split(b"\n", 1)

        if not raw_line:
            continue

        if len(raw_line) > ST_MAX_LINE_BYTES:
            debug("ST001 line too long")
            continue

        value = parse_st001_line(raw_line)
        if value is not None:
            # Ganz wichtig: NICHT aufsummieren.
            usb_send("COIN:{}".format(value))
        else:
            debug("ST001 ignored: {}".format(raw_line))

    return buffer


# ---------------------------------------------------------------------------
# VL53L1X
# ---------------------------------------------------------------------------

def create_tof(i2c):
    if VL53L1X is None:
        raise RuntimeError("vl53l1x.py fehlt auf dem Pico")

    devices = i2c.scan()
    if 0x29 not in devices:
        raise RuntimeError(
            "VL53L1X nicht gefunden; I2C scan={}".format(
                [hex(x) for x in devices]
            )
        )

    sensor = VL53L1X(i2c)

    # Ein paar Messungen nach Initialisierung verwerfen.
    for _ in range(3):
        try:
            sensor.read()
        except Exception:
            pass
        time.sleep_ms(50)

    return sensor


def valid_distance(distance_mm):
    return (
        isinstance(distance_mm, int)
        and TOF_MIN_VALID_MM <= distance_mm <= TOF_MAX_VALID_MM
    )


def median_of(values):
    """
    Median einer kleinen Liste von Ganzzahlen (Rundung nach unten bei
    gerader Anzahl). Robuster gegen einzelne Ausreißer als ein Mittelwert.
    """
    s = sorted(values)
    n = len(s)
    mid = n // 2
    if n % 2 == 1:
        return s[mid]
    return (s[mid - 1] + s[mid]) // 2


# ---------------------------------------------------------------------------
# HAUPTPROGRAMM
# ---------------------------------------------------------------------------

def main():
    i2c = I2C(
        TOF_I2C_ID,
        sda=Pin(TOF_SDA_PIN),
        scl=Pin(TOF_SCL_PIN),
        freq=TOF_I2C_FREQ,
    )

    # UART1: GP5 = RX vom ST-001.
    # GP4/TX ist initialisiert, bleibt aber unbeschaltet.
    st_uart = UART(
        ST_UART_ID,
        baudrate=ST_UART_BAUD,
        bits=8,
        parity=None,
        stop=1,
        tx=Pin(ST_UART_TX_PIN),
        rx=Pin(ST_UART_RX_PIN),
        timeout=0,
        timeout_char=0,
    )

    st_buffer = b""

    pc_poll = select.poll()
    pc_poll.register(sys.stdin, select.POLLIN)
    pc_buffer = ""

    tof = None
    tof_error_count = 0
    last_tof_init_try = time.ticks_add(time.ticks_ms(), -TOF_RETRY_MS)
    last_tof_sample = time.ticks_add(time.ticks_ms(), -TOF_SAMPLE_MS)

    # Rollierendes Fenster für den Median-Filter + Sendedrosselung
    tof_window = []
    last_distance_send = time.ticks_add(
        time.ticks_ms(), -DISTANCE_SEND_INTERVAL_MS
    )

    # Zustandsautomat für Presence-Debounce/Hysterese (nur ENABLE_PRESENCE_FALLBACK)
    presence_latched = False
    presence_since = None
    release_since = None

    usb_send("READY")

    while True:
        # ---------------------------------------------------------------
        # 1) ST-001 möglichst oft leeren, damit keine UART-Daten verloren gehen
        # ---------------------------------------------------------------
        try:
            st_buffer = process_st001_uart(st_uart, st_buffer)
        except Exception as exc:
            debug("UART error: {}".format(exc))
            st_buffer = b""

        # ---------------------------------------------------------------
        # 1b) PC-Kommandos (z.B. Schnittstellen-Identifikation) verarbeiten
        # ---------------------------------------------------------------
        try:
            pc_buffer = process_pc_command(pc_poll, pc_buffer)
        except Exception as exc:
            debug("PC cmd error: {}".format(exc))
            pc_buffer = ""

        now = time.ticks_ms()

        # ---------------------------------------------------------------
        # 2) VL53L1X bei Bedarf neu verbinden
        # ---------------------------------------------------------------
        if tof is None:
            if time.ticks_diff(now, last_tof_init_try) >= TOF_RETRY_MS:
                last_tof_init_try = now
                try:
                    tof = create_tof(i2c)
                    tof_error_count = 0
                    tof_window = []
                    presence_latched = False
                    presence_since = None
                    release_since = None
                    debug("VL53L1X ready")
                    report_tof_status(True)
                except Exception as exc:
                    tof = None
                    debug("VL53L1X init error: {}".format(exc))
                    report_tof_status(False)

        # ---------------------------------------------------------------
        # 3) ToF zyklisch messen (Free-Running, Median-Filter, gedrosseltes Senden)
        # ---------------------------------------------------------------
        elif time.ticks_diff(now, last_tof_sample) >= TOF_SAMPLE_MS:
            last_tof_sample = now

            try:
                distance_mm = tof.read()
                tof_error_count = 0

                if DEBUG:
                    debug("DIST:{}".format(distance_mm))

                is_valid = valid_distance(distance_mm)

                if is_valid:
                    tof_window.append(distance_mm)
                    if len(tof_window) > TOF_MEDIAN_WINDOW:
                        tof_window.pop(0)
                else:
                    debug("DIST invalid: {}".format(distance_mm))

                if tof_window and (
                    time.ticks_diff(now, last_distance_send)
                    >= DISTANCE_SEND_INTERVAL_MS
                ):
                    last_distance_send = now
                    usb_send("DISTANCE:{}".format(median_of(tof_window)))

                if ENABLE_PRESENCE_FALLBACK:
                    if not presence_latched:
                        # Noch nicht ausgelöst:
                        # Abstand muss für PRESENCE_STABLE_MS <= Schwellwert bleiben.
                        if is_valid and distance_mm <= PRESENCE_DISTANCE_MM:
                            if presence_since is None:
                                presence_since = now
                            elif (
                                time.ticks_diff(now, presence_since)
                                >= PRESENCE_STABLE_MS
                            ):
                                usb_send("PRESENCE")
                                presence_latched = True
                                presence_since = None
                                release_since = None
                        else:
                            presence_since = None

                    else:
                        # Bereits ausgelöst:
                        # Erst wieder "scharf", wenn die Person stabil weit genug weg ist.
                        far_enough = (
                            (not is_valid)
                            or distance_mm >= RELEASE_DISTANCE_MM
                        )

                        if far_enough:
                            if release_since is None:
                                release_since = now
                            elif (
                                time.ticks_diff(now, release_since)
                                >= RELEASE_STABLE_MS
                            ):
                                presence_latched = False
                                release_since = None
                                presence_since = None
                                debug("Presence re-armed")
                        else:
                            release_since = None

            except Exception as exc:
                tof_error_count += 1
                debug("VL53L1X read error: {}".format(exc))

                if tof_error_count >= TOF_ERRORS_BEFORE_REINIT:
                    tof = None
                    tof_error_count = 0
                    tof_window = []
                    presence_latched = False
                    presence_since = None
                    release_since = None
                    report_tof_status(False)

        # Kurze Pause: UART bleibt trotzdem sehr reaktionsschnell.
        time.sleep_ms(5)


main()
