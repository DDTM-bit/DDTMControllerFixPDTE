IF YOU HAVE PROBLEMS WITH PLAYSTATION CONTROLLERS:
This works perfectly with Xbox-Xinput,DualSense,DS4,Switch Pro, all Wired and Wireless.
Now you can map buttons and other sticks and trigger options.

For Dualshock 3 you need DsHidMini, it will not work natively.
Installation Instructions
-----------------------------------
YOU NEED DSFIX Mod installed.
    - Extract DDTMControllerFix.dll and SDL2.dll into your Dark Souls game folder.
    - Open your DSfix.ini file and scroll down to the bottom and find the dinput8dllWrapper line.
    - Update it to read exactly: dinput8dllWrapper DDTMControllerFix.dll
    - Save the file.
If you want to use a keyboard/mouse or other input mods, you change that "dinput8dllWrapper " line with your dll.
Mouse Cursor: to hide the mouse on screen go to dsfix.ini and modify in disableCursor 1.
----------------------------------------------------------------------------
Important Requirements (Read Before Playing)
Because this mod reads your controller hardware natively, you must disable all other controller translation software, or the game will read double inputs and glitch out.
Close Emulators: Fully close DS4Windows, DualSenseX, or any other controller mapping software.
IF controller is not working in game, try to close and disable/enable steam input and reopen the game.
----------------------------------------------------------------------------
Button Mapping -> Multi-Binding Support.
When you launch the game for the first time, the mod will automatically generate a DDTMControllerMappings.ini file in your game folder.
Open it with a text editor and you have all the instructions there.
----------------------------------------------------------------------------
This mod is a lightweight, zero-latency proxy that injects directly into the game's input loop. By utilizing the industry-standard SDL2 library, it natively reads almost any modern controller and perfectly translates it to Xbox commands on the fly.
Key Features
True Universal Support: Natively supports PlayStation (DualSense/DS4), Nintendo Switch Pro, and standard XInput/Generic controllers.
Zero Input Lag: Bypasses standard Windows input layers for direct, hardware-level polling via SDL2.
No External Software Required: Play directly without DS4Windows, DualSenseX, or Steam Input running in the background.

Credits & Acknowledgements
I created this dll, but I used other tools that helped me enormously:
SDL2 by Sam Lantinga and the SDL community, for the underlying controller API.
ppy.SDL2-CS by the ppy/osu! team, for the modern C# bindings.
MinHook by Tsuda Kageyu, used for memory hooking and function detouring.
A shoutout to Methanhydrat (creator of DarkSoulsInputCustomizer). While this mod uses a new native VTable patch to fix the camera spin standalone, their original work paved the way for fixing PTDE's DirectInput issues.

