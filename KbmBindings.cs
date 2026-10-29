// =====================================================================================
//  Keyboard + mouse: key names, the [Keyboard] / [Mouse] ini sections, and turning the
//  pressed keys into actions.
//
//  This is completely separate from the controller code in Proxy.cs: it has its own
//  settings, its own macros and its own state. Nothing here reads or changes any
//  controller setting, preset or macro, and the controller code never reads these.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlayStationPDTEinputFix
{
    internal static partial class KeyboardMouse
    {
        // -----------------------------------------------------------------------------
        // Inputs. 0..255 are Windows virtual-key codes, mouse inputs are above.
        // -----------------------------------------------------------------------------
        private const int IN_MOUSE_LEFT = 0x100;
        private const int IN_MOUSE_RIGHT = 0x101;
        private const int IN_MOUSE_MIDDLE = 0x102;
        private const int IN_MOUSE_4 = 0x103;
        private const int IN_MOUSE_5 = 0x104;
        private const int IN_WHEEL_UP = 0x105;
        private const int IN_WHEEL_DOWN = 0x106;
        private const int IN_COUNT = 0x107;

        private static bool IsMouseInput(int i) => i >= IN_MOUSE_LEFT;

        private static readonly Dictionary<string, int> _inputNames = BuildInputNames();

        private static Dictionary<string, int> BuildInputNames()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
            for (char c = '0'; c <= '9'; c++) d[c.ToString()] = c;
            for (int f = 1; f <= 24; f++) d["F" + f] = 0x70 + f - 1;
            for (int n = 0; n <= 9; n++) d["NUMPAD" + n] = 0x60 + n;
            d["NUMPAD_MULTIPLY"] = 0x6A; d["NUMPAD_ADD"] = 0x6B; d["NUMPAD_SUBTRACT"] = 0x6D;
            d["NUMPAD_DECIMAL"] = 0x6E; d["NUMPAD_DIVIDE"] = 0x6F;
            d["SPACE"] = 0x20; d["TAB"] = 0x09; d["ENTER"] = 0x0D; d["ESC"] = 0x1B; d["BACKSPACE"] = 0x08;
            d["CAPSLOCK"] = 0x14; d["LSHIFT"] = 0xA0; d["RSHIFT"] = 0xA1; d["LCTRL"] = 0xA2; d["RCTRL"] = 0xA3;
            d["LALT"] = 0xA4; d["RALT"] = 0xA5;
            d["UP"] = 0x26; d["DOWN"] = 0x28; d["LEFT"] = 0x25; d["RIGHT"] = 0x27;
            d["INSERT"] = 0x2D; d["DELETE"] = 0x2E; d["HOME"] = 0x24; d["END"] = 0x23; d["PAGEUP"] = 0x21; d["PAGEDOWN"] = 0x22;
            d["MINUS"] = 0xBD; d["EQUALS"] = 0xBB; d["LBRACKET"] = 0xDB; d["RBRACKET"] = 0xDD; d["BACKSLASH"] = 0xDC;
            d["SEMICOLON"] = 0xBA; d["APOSTROPHE"] = 0xDE; d["GRAVE"] = 0xC0; d["COMMA"] = 0xBC; d["PERIOD"] = 0xBE; d["SLASH"] = 0xBF;
            d["NUMLOCK"] = 0x90; d["SCROLLLOCK"] = 0x91; d["PAUSE"] = 0x13;
            d["MOUSE_LEFT"] = IN_MOUSE_LEFT; d["MOUSE_RIGHT"] = IN_MOUSE_RIGHT; d["MOUSE_MIDDLE"] = IN_MOUSE_MIDDLE;
            d["MOUSE_4"] = IN_MOUSE_4; d["MOUSE_5"] = IN_MOUSE_5;
            d["MOUSE_WHEEL_UP"] = IN_WHEEL_UP; d["MOUSE_WHEEL_DOWN"] = IN_WHEEL_DOWN;
            return d;
        }

        // -----------------------------------------------------------------------------
        // Actions
        // -----------------------------------------------------------------------------
        private enum Act
        {
            MoveForward, MoveBackward, MoveLeft, MoveRight, Walk, Dash, Jump,
            SwitchLeftWeapon, SwitchRightWeapon, SwitchItem, SwitchMagic,
            LightAttack, HeavyAttack, Block, Parry,
            Interact, UseItem, TwoHand, Menu, Gestures,
            LockOn, NextTarget, PreviousTarget,
            DashSprintOnly, Kick, LeapAttack,
            MenuLeft, MenuRight,
            Count
        }

        private static readonly string[] _actNames =
        {
            "MOVE_FORWARD", "MOVE_BACKWARD", "MOVE_LEFT", "MOVE_RIGHT", "WALK", "DASH_ROLL_BACKSTEP", "JUMP",
            "SWITCH_LEFT_WEAPON", "SWITCH_RIGHT_WEAPON", "SWITCH_ITEM", "SWITCH_MAGIC",
            "LIGHT_ATTACK", "HEAVY_ATTACK", "BLOCK", "PARRY",
            "INTERACT", "USE_ITEM", "TWO_HAND", "MENU", "GESTURES",
            "LOCK_ON", "NEXT_TARGET", "PREVIOUS_TARGET",
            "DASH_SPRINT_ONLY", "KICK", "LEAP_ATTACK",
            "MENU_LEFT", "MENU_RIGHT"
        };

        // Actions that may only use ONE key (no combination).
        private static bool SingleKeyOnly(Act a)
        {
            switch (a)
            {
                case Act.SwitchLeftWeapon: case Act.SwitchRightWeapon: case Act.SwitchItem: case Act.SwitchMagic:
                case Act.LightAttack: case Act.HeavyAttack: case Act.Block: case Act.Parry:
                case Act.Kick: case Act.LeapAttack:
                    return false;
                default:
                    return true;
            }
        }

        // Actions still used inside menus: the A / B / X / Y / START / BACK equivalents.
        private static bool MenuAction(Act a) =>
            a == Act.Dash || a == Act.Interact || a == Act.UseItem || a == Act.TwoHand || a == Act.Menu || a == Act.Gestures;

        private struct Binding
        {
            public Act Action;
            public int A, B;            // B = -1 for a single key
        }

        private static readonly List<Binding> _bindings = new List<Binding>();

        // [Mouse] settings
        private static float _sensX = 1.0f, _sensY = 1.0f, _bowSensX = 1.0f, _bowSensY = 1.0f;
        private static bool _invertX, _invertY;

        // -----------------------------------------------------------------------------
        // The default [Keyboard] / [Mouse] text. Appended to DDTMControllerMappings.ini
        // when the file has no [Keyboard] section yet (new file or an older file).
        // -----------------------------------------------------------------------------
        private const string DEFAULT_SECTION =
@"

# ===========================================================================
#  5. KEYBOARD AND MOUSE
# ===========================================================================
# Keyboard and mouse always work, together with the controller.
# This section is completely separate from the controller sections above:
# presets, macros and controller settings never change it, and it never
# changes them.
#
# The game's own keyboard and mouse settings are ignored, only these are used.
# The camera is moved with the mouse only, never with keys.
#
# Format:  ACTION = KEY
#     or:  ACTION = KEY + KEY        (a combination of two, where allowed)
# NONE leaves an action without a key. You can write the same action on two
# lines to give it two different keys.
# When a combination is held (for example LSHIFT + MOUSE_LEFT), only the
# combination's action happens, not the action of MOUSE_LEFT alone.
#
# Keys you can use after the '=':
#   Letters      A to Z
#   Numbers      0 to 9 (the row above the letters)
#   F keys       F1 to F24
#   Numpad       NUMPAD0 to NUMPAD9, NUMPAD_ADD, NUMPAD_SUBTRACT,
#                NUMPAD_MULTIPLY, NUMPAD_DIVIDE, NUMPAD_DECIMAL
#   Modifiers    LSHIFT, RSHIFT, LCTRL, RCTRL, LALT, RALT
#   Others       SPACE, TAB, ENTER, ESC, BACKSPACE, CAPSLOCK,
#                UP, DOWN, LEFT, RIGHT, INSERT, DELETE, HOME, END,
#                PAGEUP, PAGEDOWN, MINUS, EQUALS, LBRACKET, RBRACKET,
#                BACKSLASH, SEMICOLON, APOSTROPHE, GRAVE, COMMA, PERIOD,
#                SLASH, NUMLOCK, SCROLLLOCK, PAUSE
#   Mouse        MOUSE_LEFT, MOUSE_RIGHT, MOUSE_MIDDLE, MOUSE_4, MOUSE_5,
#                MOUSE_WHEEL_UP, MOUSE_WHEEL_DOWN
#
# In menus: only INTERACT, DASH_ROLL_BACKSTEP, USE_ITEM, TWO_HAND, MENU,
# GESTURES, MENU_LEFT and MENU_RIGHT work, and the mouse works as in the
# normal game. These keys always work in menus, whatever is set here:
#   ENTER = confirm, BACKSPACE = back, ARROW KEYS = move through the items,
#   ESC = close the menu (like START).
# With DSfix: if you turn the HUD off in the game options, also disable
# HUD mod in DSfix, or mouse clicks land in the wrong place.
# With a bow or crossbow two-handed, and while aiming: MOUSE_LEFT shoots and
# MOUSE_RIGHT changes the arrow type, whatever is written here. While aiming,
# the mouse wheel zooms.
[Keyboard]

# One key only:
MOVE_FORWARD = W
MOVE_BACKWARD = S
MOVE_LEFT = A
MOVE_RIGHT = D
# Hold together with a movement key to walk.
WALK = LALT
# Roll, backstep, hold to sprint. It never jumps.
DASH_ROLL_BACKSTEP = SPACE
# Jumps while moving.
JUMP = F
# Press once to sprint while moving. It stops when you stop moving, attack
# or open a menu. It never rolls or jumps.
DASH_SPRINT_ONLY = NONE

# One key or a combination of two:
SWITCH_LEFT_WEAPON = 1
SWITCH_RIGHT_WEAPON = 2
SWITCH_ITEM = 3
SWITCH_MAGIC = 4
LIGHT_ATTACK = MOUSE_LEFT
HEAVY_ATTACK = MOUSE_RIGHT
BLOCK = LCTRL
PARRY = Q
# Instant kick and instant jumping heavy attack (no flick needed),
# for example KICK = X  or  KICK = LALT + MOUSE_LEFT
KICK = NONE
LEAP_ATTACK = NONE

# One key only:
INTERACT = E
USE_ITEM = R
TWO_HAND = T
MENU = TAB
GESTURES = G
LOCK_ON = MOUSE_MIDDLE
NEXT_TARGET = MOUSE_WHEEL_DOWN
PREVIOUS_TARGET = MOUSE_WHEEL_UP

# Only in the quick menu and the menus opened from it (inventory,
# equipment, status...): change the tab, like LB / RB on the controller.
# There these keys do nothing else; in the game they keep their normal job.
MENU_LEFT = A
MENU_RIGHT = D

[Mouse]
# 1.0 = normal. 2.0 = twice as fast. 0.5 = half speed.
Sensitivity_X = 1.0
Sensitivity_Y = 1.0
# Used while aiming with a bow or crossbow.
Bow_Sensitivity_X = 0.5
Bow_Sensitivity_Y = 0.5
# TRUE turns the direction around.
Invert_X = FALSE
Invert_Y = FALSE
";

        private static readonly (Act, string)[] _defaults =
        {
            (Act.MoveForward, "W"), (Act.MoveBackward, "S"), (Act.MoveLeft, "A"), (Act.MoveRight, "D"),
            (Act.Walk, "LALT"), (Act.Dash, "SPACE"), (Act.Jump, "F"),
            (Act.SwitchLeftWeapon, "1"), (Act.SwitchRightWeapon, "2"), (Act.SwitchItem, "3"), (Act.SwitchMagic, "4"),
            (Act.LightAttack, "MOUSE_LEFT"), (Act.HeavyAttack, "MOUSE_RIGHT"), (Act.Block, "LCTRL"), (Act.Parry, "Q"),
            (Act.Interact, "E"), (Act.UseItem, "R"), (Act.TwoHand, "T"), (Act.Menu, "TAB"), (Act.Gestures, "G"),
            (Act.LockOn, "MOUSE_MIDDLE"), (Act.NextTarget, "MOUSE_WHEEL_DOWN"), (Act.PreviousTarget, "MOUSE_WHEEL_UP"),
            (Act.DashSprintOnly, "LSHIFT"),
            (Act.MenuLeft, "Q"), (Act.MenuRight, "E"),
        };

        private static void LoadKeyboardConfig()
        {
            const string iniPath = "DDTMControllerMappings.ini";
            _bindings.Clear();
            string[] lines = Array.Empty<string>();
            try
            {
                if (File.Exists(iniPath)) lines = File.ReadAllLines(iniPath);
                bool hasSection = false;
                foreach (string l in lines)
                    if (l.Trim().Equals("[Keyboard]", StringComparison.OrdinalIgnoreCase)) { hasSection = true; break; }

                if (!hasSection)
                {
                    File.AppendAllText(iniPath, DEFAULT_SECTION.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                    Log("ini: no [Keyboard] section found, the default keyboard and mouse section was added");
                    lines = File.ReadAllLines(iniPath);
                }
            }
            catch (Exception e) { Log("ini: could not read or write the file (" + e.Message + "), using defaults"); }

            bool inKeyboard = false, inMouse = false, sawKeyboard = false, debugLog = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.Replace(" ", "").StartsWith("DebugLog=", StringComparison.OrdinalIgnoreCase))
                    debugLog = ParseOn(line.Substring(line.IndexOf('=') + 1).Trim());
                if (line.StartsWith("["))
                {
                    inKeyboard = line.Equals("[Keyboard]", StringComparison.OrdinalIgnoreCase);
                    inMouse = line.Equals("[Mouse]", StringComparison.OrdinalIgnoreCase);
                    sawKeyboard |= inKeyboard;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToUpperInvariant();
                string val = line.Substring(eq + 1).Trim();

                if (inKeyboard)
                {
                    int a = Array.IndexOf(_actNames, key);
                    if (a < 0) { Log($"ini: unknown keyboard action '{key}' ignored"); continue; }
                    AddBinding((Act)a, val);
                }
                else if (inMouse)
                {
                    switch (key)
                    {
                        case "SENSITIVITY_X": ParseFloat(val, ref _sensX); break;
                        case "SENSITIVITY_Y": ParseFloat(val, ref _sensY); break;
                        case "BOW_SENSITIVITY_X": ParseFloat(val, ref _bowSensX); break;
                        case "BOW_SENSITIVITY_Y": ParseFloat(val, ref _bowSensY); break;
                        case "INVERT_X": _invertX = ParseOn(val); break;
                        case "INVERT_Y": _invertY = ParseOn(val); break;
                    }
                }
            }

            if (!sawKeyboard)
                foreach (var (act, keys) in _defaults) AddBinding(act, keys);

            SetLogEnabled(debugLog);

            Log($"ini: {_bindings.Count} keyboard/mouse bindings, sensitivity {_sensX}/{_sensY}, bow {_bowSensX}/{_bowSensY}, invert X={_invertX} Y={_invertY}");
        }

        private static void ParseFloat(string v, ref float target)
        {
            if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f > 0 && f < 100) target = f;
        }

        private static bool ParseOn(string v)
        {
            v = v.ToUpperInvariant();
            return v == "TRUE" || v == "1" || v == "YES" || v == "ON";
        }

        private static void AddBinding(Act act, string value)
        {
            if (value.Length == 0 || value.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return;
            string[] parts = value.Split('+');
            int a = -1, b = -1;
            if (!_inputNames.TryGetValue(parts[0].Trim(), out a)) { Log($"ini: unknown key '{parts[0].Trim()}' for {_actNames[(int)act]}"); return; }
            if (parts.Length > 1)
            {
                if (SingleKeyOnly(act))
                    Log($"ini: {_actNames[(int)act]} can only have one key, '{value}' uses only the first one");
                else if (!_inputNames.TryGetValue(parts[1].Trim(), out b)) { Log($"ini: unknown key '{parts[1].Trim()}' for {_actNames[(int)act]}"); return; }
                if (parts.Length > 2) Log($"ini: {_actNames[(int)act]} = '{value}': at most two keys, the rest is ignored");
            }
            _bindings.Add(new Binding { Action = act, A = a, B = b });
        }

        // -----------------------------------------------------------------------------
        // Which actions are held this poll.
        //   down[]       inputs held this poll
        //   menu         only the A/B/X/Y/START/BACK actions, keyboard keys only
        //   forcedMouse  bow two-handed or aiming: MOUSE_LEFT / MOUSE_RIGHT are taken
        //   aiming       the mouse wheel zooms instead
        // A held combination of two hides the single-key actions of its own keys.
        // -----------------------------------------------------------------------------
        private static readonly bool[] _consumed = new bool[IN_COUNT];

        private static bool BindingAllowed(in Binding bd, bool menu, bool forcedMouse, bool aiming)
        {
            if (bd.Action == Act.MenuLeft || bd.Action == Act.MenuRight) return false;   // handled first, see EvaluateActions
            if (menu && (!MenuAction(bd.Action) || IsMouseInput(bd.A) || (bd.B >= 0 && IsMouseInput(bd.B)))) return false;
            if (forcedMouse && (UsesInput(bd, IN_MOUSE_LEFT) || UsesInput(bd, IN_MOUSE_RIGHT))) return false;
            if (aiming && (UsesInput(bd, IN_WHEEL_UP) || UsesInput(bd, IN_WHEEL_DOWN))) return false;
            return true;
        }

        private static bool UsesInput(in Binding bd, int i) => bd.A == i || bd.B == i;

        // menuTabs: the quick menu (or a menu opened from it) is open. Then MENU_LEFT / MENU_RIGHT work, and
        // their keys are taken by them, so no other action on the same key happens there.
        private static void EvaluateActions(bool[] down, bool[] act, bool menu, bool forcedMouse, bool aiming, bool menuTabs)
        {
            Array.Clear(act);
            Array.Clear(_consumed);

            if (menuTabs)
                foreach (var bd in _bindings)
                {
                    if (bd.Action != Act.MenuLeft && bd.Action != Act.MenuRight) continue;
                    _consumed[bd.A] = true;
                    if (down[bd.A]) act[(int)bd.Action] = true;
                }

            foreach (var bd in _bindings)
            {
                if (bd.B < 0 || !BindingAllowed(bd, menu, forcedMouse, aiming)) continue;
                if (_consumed[bd.A] && menuTabs || _consumed[bd.B] && menuTabs) continue;
                if (down[bd.A] && down[bd.B])
                {
                    act[(int)bd.Action] = true;
                    _consumed[bd.A] = true;
                    _consumed[bd.B] = true;
                }
            }
            foreach (var bd in _bindings)
            {
                if (bd.B >= 0 || !BindingAllowed(bd, menu, forcedMouse, aiming)) continue;
                if (down[bd.A] && !_consumed[bd.A]) act[(int)bd.Action] = true;
            }
        }
    }
}
