# BLE Serial Terminal for Windows

A Windows desktop BLE terminal for discovering devices, connecting to BLE UART-style services, sending and receiving text or hexadecimal data, managing macros and profiles, logging sessions, and working with supported STM32WB OTA firmware-update flows. It uses the built-in Windows BLE APIs, so no serial COM-port setup is required. Requires Windows 10 (version 2004 / build 19041) or Windows 11 and a compatible Bluetooth adapter.

I built this because the BLE tools I tried—including nRF Connect and ST's BLE tooling—were useful references, but I wanted a single desktop app with the combination of options and straightforward workflow I personally needed. Those apps inspired the direction and feature ideas; **this project was written independently, and no code was copied from them or from other applications**. Product names are mentioned only to identify tools that influenced my requirements; this project is not affiliated with or endorsed by them.

## Get the .exe

The exe is a single self-contained file (about 70 MB): **no .NET install is needed on the PC that runs it.**

**A. On a Windows PC (about 2 minutes)**
1. Install the .NET SDK once (needed only to build):  `winget install Microsoft.DotNet.SDK.8`
2. Double-click `build.bat`
3. Your exe is `dist\BleTerminal.exe`.

**B. Without installing anything (GitHub Actions)**
1. Create a new GitHub repo and push this folder to the `main` branch.
2. Open the repo's **Actions** tab -> *build-exe* -> when it finishes, download the **BleTerminal-win-x64** artifact.

Windows SmartScreen may warn about an unsigned exe: *More info -> Run anyway*.

## Using it
1. **Scan**, pick your device (tick/untick *Hide unnamed devices*), leave Profile on **Auto-detect**, **Connect**.
2. Type in the box at the bottom, **Enter** sends (Up/Down = history). Colours: white = RX, green = TX, blue = info, red = errors.
3. **Macros**: left-click sends, right-click edits (label, text or hex, newline on/off). An empty macro opens the editor.

| Feature | Notes |
|---|---|
| Profiles | Nordic UART, HM-10/CC254x (FFE0), Microchip RN4870/71, Telit TIO (untested), or **Custom UUIDs** (16/32/128-bit) |
| Auto-detect | tries the known profiles; if none match, the log lists every service/characteristic the device exposes so you can fill in Custom |
| View | Text or Hex for RX (hex mode = one line per notification) |
| Newlines | RX: Auto/CR/LF/CRLF — TX: None/CR/LF/CRLF. Hex input and hex macros are sent raw (no newline) |
| Write type | Auto (with response if supported) / With response / Without response; writes are split to the negotiated MTU |
| Extras | timestamps, local echo, auto-scroll, auto-reconnect, log-to-file with **your own folder**, save terminal, Ctrl+wheel font size, RX/TX byte counters |

Settings and macros persist in `%AppData%\BleTerminal\settings.json`.

## Notes
- Devices that need bonding (encrypted characteristics): pair them once in *Windows Settings -> Bluetooth & devices*.
- A BLE peripheral usually accepts only one central: disconnect your phone first, or Windows reports "unreachable".
- Core logic (hex/newline/UUID parsing, settings, logging) has tests: `dotnet run --project tests/CoreTests`.


## What's new
- **Connect works with a write-only service**: Notify is optional (transmit-only), and Notify/Write may come from different services.
- **Write type "Auto" now sends a write *command*** (without response) whenever the characteristic supports it, like nRF Connect's "Write command".
  To send a text such as `OTA` exactly, set **TX newline = None** (or use a macro with "Append TX newline" unticked).
- **Cancel** button while connecting (the Connect button turns into Cancel).
- **Dark mode** checkbox in the toolbar (remembered).
- **Firmware update (ST STM32WB OTA)**: when the connected device exposes ST's OTA characteristics (FE22/FE23/FE24) a *Firmware update* panel
  appears: pick a `.bin`, flash address (default `0x08007000`), Start. Flow: indications on FE23 -> `02 00 70 00` to FE22 -> raw data to FE24
  -> `07 00 70 00` to FE22 -> wait for the confirmation indication -> device reboots.
  Note: ST's loader writes without erasing; the running app must erase the target region before it reboots into OTA mode.

## v1.1 - everything in this release
**Fixes**
- OTA: finishing the upload is success even if the loader reboots before its confirmation arrives; the dropped link is recognised as the reboot (no "Connection lost", no reconnect).
- Forced write mode is checked against the characteristic (falls back with a message); Auto-detect skips a recognised-but-unusable service.
- Local echo shows only after the write succeeded; the text decoder and RX queue are reset on every (re)connect; log files never share a name.
- After a reconnect the log states what was restored (profile, notifications, write channel).

**Terminal and scanning**
- **View: Text / Hex / Both** - "Both" prints a hex dump (16 bytes per row with an ASCII column) per notification.
- **Clear...** menu: clear display / reset counters and rates / clear session (also starts fresh log files).
- Device list is sorted by signal (stable: it only re-sorts on a 6 dB change), shows signal bars + dBm, keeps your selected device across re-scans, and explains empty results ("scanning", "no devices found", "N unnamed devices hidden").
- Connection panel: coloured state dot, window title with the device, and the active profile / service / notify / write characteristic and write mode.

**Diagnostics**
- Live RX/TX bytes, packet/write counts, throughput (and peak), current write payload size (MTU - 3) and the largest RX packet - handy for spotting fragmentation.
- **Raw byte log**: one line per RX notification / TX write with timestamp, direction, length and hex (`..._raw.log`, same folder as the text log; takes effect immediately when ticked while connected).

**Sending and configuration**
- **Repeat input every N ms, count M** (0 = until stopped), with Start/Stop. Repeats whatever is in the input box (text or hex per the box to its left); stops on send failure or disconnect. Separate from macros. There is no scripting language.
- **Saved UUID profiles**: with "Custom UUIDs" selected, *Save as profile...* stores the three UUIDs under a name; they then appear in the Profile list (a *Delete* button appears when one is selected).
- **Config... > Export / Import**: macros, saved profiles and all settings as one JSON file. Import keeps this PC's log folder and OTA file path and restarts the app to apply.


## Author

Created and maintained by **Dineshkumar G.** I am a passionate developer who enjoys building practical software to solve everyday problems and improve usability.

GitHub: https://github.com/DineshKumargethub

## Support the project

If BLE Serial Terminal is useful to you, you can optionally support its continued development. Donations are voluntary and are not required to use the software. Thank you for trying it!

[Buy me a coffee](https://buymeacoffee.com/dineshkumarg)

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
