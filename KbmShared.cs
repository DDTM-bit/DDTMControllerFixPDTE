// =====================================================================================
//  Keyboard + mouse: the block shared with DSfix (its "DDTMControllerMappings" option).
//
//  DSfix shows the keyboard / mouse keys in the game's button prompts while the player
//  uses keyboard and mouse. For that it needs three things only this DLL knows: which
//  device was used last, what the game shows (game / menu / quick menu) and the key of
//  each prompt. They are written here once per poll (a few numbers, no allocation) and
//  DSfix reads them through the export DDTM_GetSharedState. Nothing in this DLL reads
//  them back, so without DSfix (or with its option off) nothing changes.
//
//  The key icons are cells of DSfix's KeyboardMouse.png, a 16 x 16 grid; a cell's index
//  is row * 16 + column, in the order of _atlasRows below (DSfix uses the same order).
//
//  Version 2 adds the controller mapping of the face buttons, LB / RB and LT / RT
//  ([Bindings] of DDTMControllerMappings.ini), so DSfix can show the icon of the real
//  button that triggers each game button.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace PlayStationPDTEinputFix
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DdtmSharedState
    {
        public uint Magic;           // 0x4D544444 ("DDTM")
        public int Version;          // 2 (1: no Pad[])
        public int Ready;            // 1 once Cell[] is filled (the .ini is read at the first poll)
        public int KbmActive;        // 1 = keyboard / mouse used last, 0 = controller
        public int Phase;            // 0 = game (hint messages included), 1 = menu, 2 = quick menu or a menu opened from it
        public uint MenuMask;        // the game's open-menu mask (only for DSfix's log)
        public fixed int Cell[16];   // KeyboardMouse.png cell of each prompt (_promptActs order), -1 = no single key
        // Controller: for each game button A B X Y LB RB LT RT (in this order), the real button that triggers it,
        // as 0 SOUTH (A / CROSS), 1 EAST (B / CIRCLE), 2 WEST (X / SQUARE), 3 NORTH (Y / TRIANGLE), 4 LB, 5 RB,
        // 6 LT, 7 RT; -1 = none or another button (DSfix keeps the game's icon)
        public fixed int Pad[8];
    }

    public static unsafe class DdtmExports
    {
        /// <summary>For DSfix: the address of the shared block (never changes, never freed).</summary>
        [UnmanagedCallersOnly(EntryPoint = "DDTM_GetSharedState", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static IntPtr DDTM_GetSharedState() => (IntPtr)KeyboardMouse.Shared;
    }

    internal static unsafe partial class KeyboardMouse
    {
        internal static readonly DdtmSharedState* Shared = CreateShared();

        private static DdtmSharedState* CreateShared()
        {
            var s = (DdtmSharedState*)NativeMemory.AllocZeroed((nuint)sizeof(DdtmSharedState));
            s->Magic = 0x4D544444;
            s->Version = 2;
            for (int i = 0; i < 16; i++) s->Cell[i] = -1;
            for (int i = 0; i < 8; i++) s->Pad[i] = -1;
            return s;
        }

        // The prompts, in the order DSfix reads them (Y X B A VIEW RS START, quick menu LB RB, then RB RT LB LT)
        private static readonly Act[] _promptActs =
        {
            Act.TwoHand, Act.UseItem, Act.Dash, Act.Interact, Act.Gestures, Act.LockOn, Act.Menu, Act.MenuLeft, Act.MenuRight,
            Act.LightAttack, Act.HeavyAttack, Act.Block, Act.Parry
        };

        // KeyboardMouse.png, row by row (16 cells per row; rows 10+ hold DSfix's own icons: arrows, mouse moving)
        private static readonly string[][] _atlasRows =
        {
            new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P" },
            new[] { "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z" },
            new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" },
            new[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" },
            new[] { "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24" },
            new[] { "NUMPAD0", "NUMPAD1", "NUMPAD2", "NUMPAD3", "NUMPAD4", "NUMPAD5", "NUMPAD6", "NUMPAD7", "NUMPAD8", "NUMPAD9",
                    "NUMPAD_ADD", "NUMPAD_SUBTRACT", "NUMPAD_MULTIPLY", "NUMPAD_DIVIDE", "NUMPAD_DECIMAL" },
            new[] { "LSHIFT", "RSHIFT", "LCTRL", "RCTRL", "LALT", "RALT", "SPACE", "TAB", "ENTER", "ESC", "BACKSPACE", "CAPSLOCK" },
            new[] { "UP", "DOWN", "LEFT", "RIGHT", "INSERT", "DELETE", "HOME", "END", "PAGEUP", "PAGEDOWN" },
            new[] { "MINUS", "EQUALS", "LBRACKET", "RBRACKET", "BACKSLASH", "SEMICOLON", "APOSTROPHE", "GRAVE", "COMMA", "PERIOD",
                    "SLASH", "NUMLOCK", "SCROLLLOCK", "PAUSE" },
            new[] { "MOUSE_LEFT", "MOUSE_RIGHT", "MOUSE_MIDDLE", "MOUSE_4", "MOUSE_5", "MOUSE_WHEEL_UP", "MOUSE_WHEEL_DOWN" },
        };

        private static bool _iconInit, _iconKbm;
        private static int _sharedPhase;
        private static long _phaseDropAt;
        // The game clears a menu's flag one frame before it stops drawing the menu, so on closing, the icons of
        // that last frame would already be those of what comes next. Going "down" (quick menu -> other window,
        // menu -> game) is therefore shown to DSfix 200 ms late; going "up" (opening a menu) at once.
        private const long PHASE_DROP_DELAY_MS = 200;

        // Called once, right after the [Keyboard] section was read (first poll)
        private static void FillSharedCells()
        {
            var cellOfInput = new Dictionary<int, int>();
            for (int r = 0; r < _atlasRows.Length; r++)
                for (int c = 0; c < _atlasRows[r].Length; c++)
                    if (_inputNames.TryGetValue(_atlasRows[r][c], out int input)) cellOfInput[input] = r * 16 + c;

            DdtmSharedState* s = Shared;
            var sb = new System.Text.StringBuilder("prompt keys for DSfix:");
            for (int p = 0; p < _promptActs.Length; p++)
            {
                // the first single key of the action; an action with only combinations (KEY + KEY) has no icon,
                // so the game's controller icon stays
                int cell = -1;
                foreach (var bd in _bindings)
                    if (bd.Action == _promptActs[p] && bd.B < 0 && cellOfInput.TryGetValue(bd.A, out int c)) { cell = c; break; }
                s->Cell[p] = cell;
                sb.Append(' ').Append(_actNames[(int)_promptActs[p]]).Append('=').Append(cell);
            }
            ReadPadMapping(s, sb);
            Thread.MemoryBarrier();
            s->Ready = 1;
            Log(sb.ToString());
        }

        // The controller's [Bindings] lines for A B X Y LB RB LT RT, read with the same button names the controller
        // code accepts (Proxy.cs: ParsePhysicalButton / ParsePhysicalAxis). The first line of each is used.
        private static readonly string[] _padGameButtons = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT" };

        private static int PadPosition(string name) => name.ToUpperInvariant() switch
        {
            "A" or "CROSS" or "SOUTH" => 0,
            "B" or "CIRCLE" or "EAST" => 1,
            "X" or "SQUARE" or "WEST" => 2,
            "Y" or "TRIANGLE" or "NORTH" => 3,
            "LB" or "L1" or "L" or "LEFTBUMPER" => 4,
            "RB" or "R1" or "R" or "RIGHTBUMPER" => 5,
            _ => -1
        };

        private static int TriggerPosition(string name) => name.ToUpperInvariant() switch
        {
            "LT" or "L2" or "ZL" or "LEFTTRIGGER" => 6,
            "RT" or "R2" or "ZR" or "RIGHTTRIGGER" => 7,
            _ => -1
        };

        private static void ReadPadMapping(DdtmSharedState* s, System.Text.StringBuilder sb)
        {
            try
            {
                if (!System.IO.File.Exists("DDTMControllerMappings.ini")) return;
                bool inBindings = false;
                foreach (string raw in System.IO.File.ReadAllLines("DDTMControllerMappings.ini"))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("[")) { inBindings = line.Equals("[Bindings]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!inBindings) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToUpperInvariant();
                    if (key == "LEFTSHOULDER") key = "LB";
                    else if (key == "RIGHTSHOULDER") key = "RB";
                    int slot = Array.IndexOf(_padGameButtons, key);
                    if (slot < 0 || s->Pad[slot] >= 0) continue;
                    string val = line.Substring(eq + 1).Trim();
                    s->Pad[slot] = slot >= 6 ? TriggerPosition(val) : PadPosition(val);
                }
            }
            catch (Exception e) { Log("controller mapping for DSfix: " + e.Message); }
            sb.Append(" | controller:");
            for (int i = 0; i < 8; i++) sb.Append(' ').Append(_padGameButtons[i]).Append('=').Append(s->Pad[i]);
        }

        // Once per poll, after the cursor was updated: same "last device" rule as the cursor.
        private static void UpdateShared(bool hasController, bool controllerUsed, bool kbmUsed, bool menu, bool menuTabs)
        {
            if (!_iconInit) { _iconInit = true; _iconKbm = !hasController; }
            if (controllerUsed) _iconKbm = false;
            else if (kbmUsed) _iconKbm = true;

            int want = menuTabs ? 2 : (menu ? 1 : 0);
            if (want >= _sharedPhase) { _sharedPhase = want; _phaseDropAt = 0; }
            else if (_phaseDropAt == 0) _phaseDropAt = _now + PHASE_DROP_DELAY_MS;
            else if (_now >= _phaseDropAt) { _sharedPhase = want; _phaseDropAt = 0; }

            DdtmSharedState* s = Shared;
            s->KbmActive = _iconKbm ? 1 : 0;
            s->Phase = _sharedPhase;
            s->MenuMask = _menuMask;
        }
    }
}
