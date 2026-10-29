# DDTM Controller Fix for Dark Souls: Prepare to Die Edition

A lightweight, zero-latency proxy DLL that injects directly into the game's input loop. By utilizing the industry-standard SDL2 library, it natively reads almost any modern controller and perfectly translates it to Xbox commands on the fly.

---

## Features
* **Keyboard & Mouse advanced support** if you turn the HUD off in the game options, also disable HUD mod in DSfix.ini, or mouse clicks land in the wrong place.
* **True Universal Support:** Works out of the box with Xbox (XInput), PlayStation (DualSense, DS4), Nintendo Switch Pro, and generic controllers (Wired & Wireless).
* **Zero Input Lag:** Bypasses standard Windows input layers for direct, hardware-level polling via SDL2.
* **No External Software Needed:** Play directly without running DS4Windows, DualSenseX, or Steam Input in the background.
* **Custom Button Mapping:** Supports multi-binding and advanced trigger/stick options via an auto-generated configuration file.

---

## Requirements

* **DSFix Mod** is recommended to be installed.
* This also works without DSFix, you just rename the dll to "dinput8.dll"
* **DualShock 3 Users:** Requires DsHidMini to function (not natively supported).

---

## Installation

1. **Extract Files:** Place `DDTMControllerFix.dll` and `SDL2.dll` into your Dark Souls root game directory.
2. **Configure DSFix:**
   * Open `DSfix.ini` in a text editor.
   * Scroll down to the `dinput8dllWrapper` entry.
   * Change it to:
     ```ini
     dinput8dllWrapper DDTMControllerFix.dll
     ```
   * Save and close `DSfix.ini`.

* **Keyboard & Mouse:** Play with keyboard + mouse, together with the controller at any time. Smooth 1:1 mouse camera, custom cursor, and key bindings (combos of two keys) in the `[Keyboard]` / `[Mouse]` sections of `DDTMControllerMappings.ini`. The game's own keyboard settings are not used.
---

## Important Requirements (Read Before Playing)

Because this mod reads controller hardware natively, active controller translation software will cause **double inputs and glitches**.

**Close External Software:** Fully exit **DS4Windows**, **DualSenseX**, **Disable Steam Input** or any other controller mapping tool before launching the game.
---

## Button Mapping & Configuration

When you launch the game for the first time, the mod automatically generates a configuration file in your game folder:

```text
DDTMControllerMappings.ini
