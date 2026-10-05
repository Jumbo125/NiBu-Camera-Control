Pico MicroPython Setup

Dateien auf den Raspberry Pi Pico kopieren:
1. main.py
2. lib/vl53l1x.py  (externer VL53L1X-MicroPython-Treiber)

main.py startet auf MicroPython automatisch beim Boot.

Wiring steht in WIRING.txt.

Hinweis:
Der VL53L1X-Treiber ist nicht Bestandteil dieses ZIPs.
Verwende z.B. den Pico-Treiber aus:
drakxtwo/vl53l1x_pico -> vl53l1x.py

Danach liegen auf dem Pico:
/
  main.py
  lib/
    vl53l1x.py

USB-Protokoll:
READY
PRESENCE
COIN:<cent>
