# Fotobox Qt WebEngine Host (Linux)

This project is the Linux/Qt WebEngine counterpart of the Windows WebView2 host.

## What it does

- loads a startup target from `init.json`
- starts maximized by default
- can start in kiosk mode (frameless fullscreen)
- normal minimize goes to the task bar / window manager
- `restore()` leaves kiosk mode and returns to a maximized normal window
- optional title and icon override from `init.json`
- JavaScript bridge is injected automatically as `window.hostApp`
- optional DevTools window with `F12` or `Ctrl+Shift+I`

## Build on Debian

Debian ships `qt6-base-dev`, `qt6-webchannel-dev`, and `qt6-webengine-dev` packages for Qt 6 development, including WebEngine and WebChannel. Qt WebEngine provides the development files for building applications that embed web content. If you build with the system Qt packages, the resulting binary will use those installed runtime libraries on the same machine.

```bash
sudo apt update
sudo apt install -y build-essential cmake qt6-base-dev qt6-webchannel-dev qt6-webengine-dev
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j"$(nproc)"
./build/fotobox-qtwebengine-host
```

## Build inside WSL / Debian on Windows

WSL lets you run Linux tools directly on Windows, and WSL 2 supports Linux GUI applications through WSLg. Linux GUI apps are only supported with WSL 2, not WSL 1. So yes, you can compile this from your Debian console on Windows and also run the GUI app there if your distro is using WSL 2 with GUI support enabled.

```bash
wsl --status
```

If your Debian instance is WSL 2 and GUI support is active, the Qt window should open directly on your Windows desktop.

## Config file

The app looks for `init.json` next to the executable. Paths like `localIndexPath` and `icon` are resolved relative to that file.

Example:

```json
{
  "url": "",
  "defaultUrl": "http://127.0.0.1",
  "defaultPort": 8080,
  "localIndexPath": "wwwroot/index.html",
  "kiosk": false,
  "title": "My Photo Booth",
  "icon": "assets/app.png",
  "allowDevTools": false
}
```

## URL resolution order

1. `url` as full URL (`http://`, `https://`, `file://`)
2. `url` as local file path relative to `init.json`
3. `url` as host/IP without scheme, automatically prefixed with `http://`
4. `localIndexPath` relative to `init.json`
5. fallback to `defaultUrl` + `defaultPort`

## JavaScript API

The host injects this object automatically:

```js
window.hostApp.minimize();
window.hostApp.maximize();
window.hostApp.restore();
window.hostApp.setKiosk(true);
window.hostApp.setKiosk(false);
window.hostApp.close();
window.hostApp.exit();
```

`maximize()` enters kiosk mode to match your last Windows version. `restore()` leaves kiosk mode and returns to a maximized normal window.

## Notes

Qt WebEngine is based on Chromium, but it is not the Google Chrome browser and does not include Chrome services or add-ons. Qt WebChannel lets a C++ `QObject` talk directly to HTML/JavaScript clients by exposing slots and methods through `qwebchannel.js`. For clients inside Qt WebEngine, `qwebchannel.js` can be loaded from `qrc:///qtwebchannel/qwebchannel.js`, and the host installs the web channel transport on the page.

If you ever deploy this app outside the build machine with bundled Qt libraries instead of using the distro packages, remember that Qt WebEngine uses a separate Qt WebEngine Process that must be shipped as part of the application bundle.
