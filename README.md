# DDTM Controller Fix for Dark Souls: Prepare to Die Edition

Play **Dark Souls: Prepare to Die Edition** with almost any controller — DualSense, DualShock 4, Switch Pro, Xbox or a generic pad — plus a full keyboard & mouse mode, without DS4Windows, DualSenseX or Steam Input.

DDTM Controller Fix is a small proxy DLL that sits in the game's input path. It reads your controller directly through **SDL2** and hands the game a clean Xbox (XInput) signal, so the game behaves exactly as if an Xbox pad were plugged in.

---

## Compatibility at a glance

| | |
|---|---|
| **Game** | Dark Souls: Prepare to Die Edition (PTDE), **Steam version**. *Not* for Dark Souls Remastered. |
| **OS** | Windows 10 / 11. No .NET install needed (the DLL is compiled natively). |
| **DSfix** | ✅ Compatible with DSfix 2.4 and recommended. Loaded through DSfix's `dinput8dllWrapper` line. [DSfix 3.0](https://github.com/DDTM-bit/DSfix-3.0---Dark-Souls-PTDE) adds extra features (see below). |
| **Without DSfix** | ✅ Works — rename the DLL to `dinput8.dll`. |
| **Controllers** | ✅ DualSense, DualShock 4, Xbox One / Series / 360, Switch Pro, most generic pads (wired & Bluetooth). |
| **DualShock 3** | ⚠️ Needs [DsHidMini](https://github.com/nefarius/DsHidMini) installed first. |
| **Keyboard & mouse** | ✅ Works together with the controller, on the current Steam `DARKSOULS.exe`. On any other exe it switches itself off safely and the controller keeps working. |
| **Button-icon texture mods** (PS / Xbox prompts) | ✅ Compatible — they only change pictures. |
| **Other input mods** (DSMfix, Dark Souls Mouse Fix, Dark Souls Input Customizer) | ❌ Don't combine. They do the same job and need the same `dinput8` slot. Use one or the other. |
| **Controller tools** (DS4Windows, DualSenseX, Steam Input, x360ce) | ❌ Turn them off for this game, or you get double inputs. |
| **Saves** | Never changed. The mod doesn't modify your save file, stats or items. |
| **Linux / Steam Deck** | Not tested. |

Only **one controller** (the first one connected) is used, which is all the game supports anyway.

---

## Features

* **Native controller support** — DualSense, DualShock 4, Switch Pro, Xbox and generic pads read directly through SDL2. PlayStation touchpad and Share/Create buttons can be mapped too.
* **No background software** — no DS4Windows, DualSenseX or Steam Input needed.
* **Rumble on every controller**, including DualSense and DualShock 4, with adjustable strength per motor.
* **Proper deadzones** — circular stick deadzones, per-stick sensitivity, trigger deadzones and activation thresholds.
* **Full button remapping** — any game button to any physical button, multiple buttons per action, and the **Japanese PS3 layout** (confirm on Circle) in two lines.
* **Presets** for modern-Souls controls, chosen with one line:
  * `DS3_JUMP` — jump on L3, the roll button can no longer jump by accident.
  * `ELDEN_RING` — jump on Cross, kick / leaping attack on Cross + R1 / R2, auto-run on L3.
  * `MORTAL_SHELL` — a second sprint button on Cross, auto-run on L3.
  * `RUN_AND_ATTACK` — vanilla movement plus auto-run and reliable kicks / leaping attacks.
* **Macros** you can put on any button or combo (up to 3 buttons): `KICK`, `LEAP_ATTACK`, `AUTO_RUN`, `DASH_SPRINT_ONLY`, `ROLL_ONLY`, `JUMP_ONLY`. Timed in milliseconds, so they behave the same at 30 and 60 FPS.
* **Keyboard & mouse mode**, always on alongside the controller:
  * 1:1 raw mouse camera, applied inside the game's own camera update — no stick emulation, no acceleration, same feel at any FPS.
  * Separate bow / crossbow aiming sensitivity, mouse-wheel zoom while aiming.
  * Every action rebindable, two-key combos, dedicated jump / sprint / roll / kick keys.
  * Menu cursor that appears when you use the mouse and hides when you pick up the controller.
  * A single ALT press no longer freezes the game (ALT+TAB and ALT+F4 still work).
* **Safe by design** — every game address the keyboard & mouse code uses is checked byte-for-byte at startup. If anything doesn't match, that feature stays off and nothing in the game is patched.

---

## Installation

The game's install folder is usually
`...\Steam\steamapps\common\Dark Souls Prepare to Die Edition\DATA` — the folder that contains `DARKSOULS.exe`. All files go **there**.

### With DSfix (recommended)

1. Copy `DDTMControllerFix.dll` and `SDL2.dll` into the `DATA` folder.
2. Open `DSfix.ini` and find the `dinput8dllWrapper` line. Change it to:
   ```ini
   dinput8dllWrapper DDTMControllerFix.dll
   ```
3. Save and start the game.

### Without DSfix

1. Copy `DDTMControllerFix.dll` and `SDL2.dll` into the `DATA` folder.
2. Rename `DDTMControllerFix.dll` to `dinput8.dll`.
3. Start the game.

### Before you play

* **Steam Input:** in Steam, right-click Dark Souls: Prepare to Die Edition → *Properties* → *Controller* → **Disable Steam Input**.
* **Close** DS4Windows, DualSenseX or any other controller-mapping tool.
* **Remove** any x360ce files (`xinput1_3.dll`, `x360ce.ini`) from the `DATA` folder.
* Leave the game's own controller options at their defaults.

---

## Configuration

On first launch the mod creates **`DDTMControllerMappings.ini`** in the `DATA` folder. Every option is explained inside the file.

* **Quickest start:** set `Preset =` to one of the presets above. Done.
* **Reset everything:** delete the file; a fresh one is written on the next launch.
* **Rule:** `#` only works at the start of a line. Never put a note after a setting on the same line.

| Section | What it controls |
|---|---|
| `Preset` | One-line control schemes (`NONE`, `DS3_JUMP`, `ELDEN_RING`, `MORTAL_SHELL`, `RUN_AND_ATTACK`). |
| `[Bindings]` | Button mapping, Japanese layout, macros and macro behaviour (`SuppressMacroButtons`, `AutoRun_CancelOnStop`, `MUTE_JUMP_FROM_B`). |
| `[Settings]` | Stick deadzones & sensitivity, trigger deadzones & thresholds, rumble strength. |
| `[Advanced]` | Engine timings (you shouldn't need these) and `DebugLog`. |
| `[Keyboard]` | Every keyboard / mouse-button action, two-key combos. |
| `[Mouse]` | Camera & bow sensitivity, axis inversion. |

### Keyboard & mouse notes

* The game's own keyboard settings are ignored; only the `[Keyboard]` section is used.
* In menus, ENTER = confirm, BACKSPACE = back, arrow keys = move, ESC = close — always, whatever you bind.
* **With DSfix:** if you turn the HUD off in the game options, also turn off the HUD mod in `DSfix.ini`, or mouse clicks land in the wrong place.

### Extra features with DSfix 3.0

[DSfix 3.0](https://github.com/DDTM-bit/DSfix-3.0---Dark-Souls-PTDE) (my fork of Durante's DSfix) works together with this mod and adds:

* **Button prompts that follow your controls** — your own keys from `[Keyboard]` while you play with keyboard & mouse, and the real button you mapped in `[Bindings]` while you use the controller. Set `DDTMControllerMappings 1` in `DSfix.ini`.
* **True-colour menu cursor** drawn inside the game picture, so it looks the same on every PC and shows up in screenshots and recordings.
* **Mouse-wheel scrolling** in item lists (inventory, Bottomless Box, equipment, shops, upgrades).

Everything else in this mod works the same with the original DSfix 2.4 or without DSfix.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Double inputs, menus skipping | Steam Input or DS4Windows / DualSenseX is still running. See *Before you play*. |
| Controller not detected | Check that `SDL2.dll` is next to the DLL. DualShock 3 needs DsHidMini. |
| Character drifts on its own | Raise `LS_DeadzoneMin` in `[Settings]`. |
| Diagonals won't run | Lower `LS_DeadzoneMax`. |
| Accidental heavy attacks | Raise `RT_Deadzone`. |
| Keyboard & mouse does nothing | Your `DARKSOULS.exe` isn't the current Steam one, so the feature switched itself off. The controller still works. |
| Anything else | Set `DebugLog = TRUE`, reproduce the problem, and attach `DDTMControllerFix_log.txt` to your bug report. |

---

## Uninstall

Delete `DDTMControllerFix.dll` (or `dinput8.dll`), `SDL2.dll` and `DDTMControllerMappings.ini`. If you use DSfix, set `dinput8dllWrapper none` again.

---

## Building from source

Requirements: .NET 9 SDK on Windows.

```
dotnet publish DDTMControllerFix.csproj -c Release -r win-x86
```

The project is compiled with Native AOT into a 32-bit native DLL that exports `DirectInput8Create`. `SDL2.dll` (win-x86) comes from the `ppy.SDL2-CS` NuGet package and must be shipped next to it. The cursor picture is `resources/cursor.png`, embedded at build time.

---

## Credits & third-party licenses

* **[SDL2](https://www.libsdl.org)** — Sam Lantinga and the SDL contributors (zlib)
* **[SDL2-CS](https://github.com/flibitijibibo/SDL2-CS)** — Ethan Lee, via the [ppy.SDL2-CS](https://github.com/ppy/SDL2-CS) fork (zlib)
* **[MinHook.NET](https://github.com/CCob/MinHook.NET)** — CCob (BSD 3-Clause), a C# port of **[MinHook](https://github.com/TsudaKageyu/minhook)** by Tsuda Kageyu (BSD 2-Clause)
* **[SharpDisasm](https://github.com/spazzarama/SharpDisasm)** — Justin Stenning, with portions from **[udis86](https://github.com/vmt/udis86)** by Vivek Thampi (BSD 2-Clause), included in MinHook.NET
* **[.NET runtime](https://github.com/dotnet/runtime)** — .NET Foundation and contributors (MIT), compiled into the DLL by Native AOT
* **[DSfix](https://github.com/PeterTh/dsfix)** — Peter "Durante" Thoman, the fix this mod plugs into (GPLv3; DSfix is not included in this mod)
* **[DsHidMini](https://github.com/nefarius/DsHidMini)** — Nefarius, for DualShock 3 users

The full license texts are in `THIRD_PARTY_LICENSES.txt`, included in every release.

### AI disclosure

The code of this mod and the cursor picture (`resources/cursor.png`) were generated with AI tools, then directed, tested and tuned in the game by DDTM.

Dark Souls is a trademark of FromSoftware / Bandai Namco. This project is not affiliated with or endorsed by them.

## License

Copyright (C) 2026 DDTM

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the [LICENSE](LICENSE) file for details.

DDTM Controller Fix is also shipped inside [DSfix 3.0](https://github.com/DDTM-bit/DSfix-3.0---Dark-Souls-PTDE), which uses the same license.
