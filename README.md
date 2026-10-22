# kbdhaptic

Touch-keyboard haptic feedback and gamepad File Explorer controls for Windows 11
handheld PCs (built and tested on the MSI Claw 8 AI+).

- **Haptic typing:** a short controller vibration on every on-screen-keyboard key
  tap. Nothing fires outside the keyboard (desktop, games, scrolling stay silent).
- **Gamepad in Explorer:** while a File Explorer window is active, the gamepad drives
  it (each action with the same vibration): **A** open, **Y** up a level,
  **B** back, **X** context menu.
- Runs headless in the tray, no admin required, ~0% idle CPU.

## How it works

A low-level keyboard hook sees the on-screen keyboard's injected key events; while the
touch keyboard is visible (`IFrameworkInputPane`), each key tap fires an XInput
vibration pulse (motor stopped by a timer). The Explorer mappings poll XInput only
while an Explorer window is foreground. Area/visibility based, not per-key.

Tested on an Intel Arc / Lunar Lake handheld; needs an XInput controller (slot 0) and
.NET Framework 4.x (present on Windows by default).

## Install

Download the `release` folder (or the release archive) and run **Install.cmd**. It
removes any previous version, installs to `%LOCALAPPDATA%\kbdhaptic`, registers a
logon autostart task (no admin), and starts it. **Uninstall.cmd** removes everything.
Your `kbdhaptic.ini` settings are kept across updates.

## Settings

Double-click the tray icon:

- **Вибрация** — Малый мотор (R, high-freq, sharp click), Большой мотор (L, low-freq),
  Длина импульса (1–40 ms); a Test button and a test input field.
- **Проводник** — toggle "Кнопки геймпада в проводнике".

All settings live in `kbdhaptic.ini` next to the exe and apply live.

## Build

Run **build.cmd** (uses the in-box .NET Framework C# compiler, no Visual Studio
needed). Output goes to `release/kbdhaptic.exe`.
