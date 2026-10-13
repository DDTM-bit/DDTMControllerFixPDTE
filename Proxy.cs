using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MinHook;
using SDL2;
using System.Globalization;

namespace PlayStationPDTEinputFix
{
    public static class Proxy
    {
        //!!publish with developer cmd: dotnet publish DDTMControllerFix.csproj -c Release -r win-x86

        // ---------------------------------------------------------
        // XInput button masks
        // ---------------------------------------------------------
        private const ushort MASK_DPAD_UP = 0x0001;
        private const ushort MASK_DPAD_DOWN = 0x0002;
        private const ushort MASK_DPAD_LEFT = 0x0004;
        private const ushort MASK_DPAD_RIGHT = 0x0008;
        private const ushort MASK_START = 0x0010;
        private const ushort MASK_BACK = 0x0020;
        private const ushort MASK_LS = 0x0040;
        private const ushort MASK_RS = 0x0080;
        private const ushort MASK_LB = 0x0100;
        private const ushort MASK_RB = 0x0200;
        private const ushort MASK_A = 0x1000;
        private const ushort MASK_B = 0x2000;
        private const ushort MASK_X = 0x4000;
        private const ushort MASK_Y = 0x8000;

        // ---------------------------------------------------------
        // Macro timing (milliseconds, NOT poll counts), so behaviour is
        // identical at 30fps and at 60fps with DSFix.
        // ---------------------------------------------------------
        // These were tuned against the real engine and are properties of the GAME,
        // not of any particular PC: PTDE runs its game logic at a fixed 30fps, so
        // the thresholds are the same on every machine. DSFix framerate unlocking
        // can only make the engine commit to a run SOONER, and overshooting these
        // values is harmless, so they are safe to bake in.
        private const uint MACRO_RELEASE_MS = 40;        // kick/leap: drop stick + release button
        private const uint MACRO_PRESS_MS = 110;         // kick/leap: slam forward + press button
        private const uint ROLL_TAP_MS = 80;             // roll: short enough to never become a sprint
        private const uint AUTORUN_ENGAGE_MS = 120;      // stick must be live this long before auto-run grabs B
        private const uint AUTORUN_STOP_MS = 150;        // neutral stick this long ends auto-run
        private const uint STICK_SUSTAIN_MS = 250;       // tolerate this much neutral stick mid-sprint
        private const uint JUMP_GAP_MS = 60;             // jump: B released so the following tap registers
        private const uint JUMP_TAP_MS = 120;            // jump: the tap itself

        // ---------------------------------------------------------
        // Macro Tracking Variables
        // ---------------------------------------------------------
        private class ComboBinding
        {
            public string ActionName = "";
            public List<SDL.SDL_GameControllerButton> Buttons = new List<SDL.SDL_GameControllerButton>();
            public List<SDL.SDL_GameControllerAxis> Axes = new List<SDL.SDL_GameControllerAxis>();

            public bool IsPressed(IntPtr controller)
            {
                // All assigned buttons must be pressed
                foreach (var btn in Buttons)
                    if (SDL.SDL_GameControllerGetButton(controller, btn) == 0) return false;

                // All assigned triggers must be pulled past a threshold (approx 50%)
                foreach (var axis in Axes)
                    if (SDL.SDL_GameControllerGetAxis(controller, axis) < 16000) return false;

                return (Buttons.Count > 0 || Axes.Count > 0);
            }
        }

        private static List<ComboBinding> _macros = new List<ComboBinding>();

        // Shared clock for every macro, sampled once per poll.
        private static uint _nowMs = 0;

        private static bool _kickActive = false;
        private static uint _kickStartMs = 0;
        private static bool _wasKickBtnPressed = false;

        private static bool _leapActive = false;
        private static uint _leapStartMs = 0;
        private static bool _wasLeapBtnPressed = false;

        private static bool _autoRunActive = false;
        private static bool _autoRunEngaged = false;
        private static bool _autoRunHasRun = false;
        private static bool _autoRunLiveTiming = false;
        private static uint _autoRunLiveSince = 0;
        private static bool _autoRunNeutralTiming = false;
        private static uint _autoRunNeutralSince = 0;
        private static bool _wasAutoRunBtnPressed = false;

        private static uint _dashPressStartMs = 0;
        private static bool _dashEngaged = false;
        private static uint _dashNeutralSince = 0;
        private static bool _dashBleeding = false;
        private static bool _wasDashSprintPressed = false;

        private static bool _jumpActive = false;
        private static bool _jumpNeedsRamp = false;
        private static uint _jumpStartMs = 0;
        private static bool _wasJumpPressed = false;

        private static bool _rollActive = false;
        private static uint _rollStartMs = 0;
        private static bool _wasRollPressed = false;

        // Sprint arbiter / jump guard state
        private static bool _bHeldLast = false;
        private static uint _bHoldStartMs = 0;
        private static bool _guardArmed = false;
        private static bool _bHoldWasMoving = false;
        private static uint _sprintEndMs = 0;

        // XInput packet number: must only change when the pad state actually changes.
        private static uint _packetNumber = 0;
        private static XINPUT_GAMEPAD _lastGamepad;

        // ---------------------------------------------------------
        // 1. Strict Memory Layouts
        // ---------------------------------------------------------
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct XINPUT_VIBRATION
        {
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate uint XInputGetStateDelegate(uint dwUserIndex, IntPtr pState);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate uint XInputSetStateDelegate(uint dwUserIndex, IntPtr pVibration);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int EnumDevicesDelegate(IntPtr pThis, uint dwDevType, IntPtr lpCallback, IntPtr pvRef, uint dwFlags);

        private static HookEngine? _hookEngine;
        private static IntPtr _controller = IntPtr.Zero;
        private static EnumDevicesDelegate? _detourEnumDevices;
        private static EnumDevicesDelegate? _originalEnumDevices;
        private static bool _configLoaded = false;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        // ---------------------------------------------------------
        // 2. The Universal INI Configuration Engine
        // ---------------------------------------------------------
        private static Dictionary<SDL.SDL_GameControllerButton, ushort> _buttonMap = new Dictionary<SDL.SDL_GameControllerButton, ushort>();
        private static SDL.SDL_GameControllerAxis _ltAxis = SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERLEFT;
        private static SDL.SDL_GameControllerAxis _rtAxis = SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERRIGHT;

        // --- New Modifiers (Updated for Radial Math) ---
        private static short _lsDeadzoneMin = 4000, _lsDeadzoneMax = 32000;
        private static short _rsDeadzoneMin = 2000, _rsDeadzoneMax = 32000;
        private static float _lsSensX = 1.0f, _lsSensY = 1.0f, _rsSensX = 1.0f, _rsSensY = 1.0f;
        private static short _ltDeadzone = 1000, _rtDeadzone = 1000;
        private static short _ltThreshold = 30000, _rtThreshold = 30000;
        private static float _rumbleLeft = 1.0f, _rumbleRight = 1.0f;

        // --- Macro behaviour settings ---
        // How long the game needs the run button held before it commits to a run.
        // Drives both the sprint safe-release and the JUMP_ONLY ramp, because they
        // are the same engine threshold. Overridable from [Advanced] but never
        // normally touched.
        private static uint _runStateMs = 800;
        private static uint _jumpGuardMs = 500;

        private static bool _suppressMacroButtons = true;
        private static bool _autoRunCancelOnStop = true;
        private static bool _muteJumpFromB = false;
        private static string _preset = "NONE";

        private static bool ParseBool(string val)
        {
            return val == "TRUE" || val == "1" || val == "YES" || val == "ON";
        }

        private static void ParseMs(string val, ref uint target, uint max)
        {
            if (uint.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out uint ms))
                target = Math.Min(ms, max);
        }

        // Builds one macro from an action name and an input string such as
        // "L3" or "R1 + L3". Shared by the [Bindings] parser and the presets.
        private static void AddMacro(string action, string value)
        {
            if (string.IsNullOrEmpty(value) || value == "NONE") return;

            ComboBinding combo = new ComboBinding { ActionName = action };
            string[] inputs = value.Split('+');

            // Enforce maximum of 3 combinations
            for (int i = 0; i < Math.Min(inputs.Length, 3); i++)
            {
                string input = inputs[i].Trim();

                // First check if the input is a trigger (Axis)
                var axis = ParsePhysicalAxis(input);
                if (axis != SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_INVALID)
                {
                    combo.Axes.Add(axis);
                    continue;
                }

                // Otherwise, check if it's a standard button
                var btn = ParsePhysicalButton(input);
                if (btn != SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_INVALID)
                {
                    combo.Buttons.Add(btn);
                }
            }

            if (combo.Buttons.Count > 0 || combo.Axes.Count > 0) _macros.Add(combo);
        }

        // A preset replaces every macro binding, and sets the handful of behaviour
        // switches that go with it. Presets never touch the button map, so the user's
        // own A/B/X/Y mapping (including the Japanese swap) is always preserved.
        // Runs after the whole file is parsed, so a preset wins over any manual
        // MUTE_JUMP_FROM_B / SuppressMacroButtons / AutoRun_CancelOnStop line.
        private static void ApplyPreset()
        {
            if (_preset == "NONE" || string.IsNullOrEmpty(_preset)) return;

            _macros.Clear();

            switch (_preset)
            {
                // Jump on its own button, and the roll button can no longer jump.
                case "DS3_JUMP":
                    _muteJumpFromB = true;
                    _suppressMacroButtons = true;
                    AddMacro("JUMP_ONLY", "L3");
                    break;

                // Sprint moved onto CROSS, auto-run on L3, kick and leap on CROSS combos.
                case "MORTAL_SHELL":
                    _suppressMacroButtons = false;
                    _autoRunCancelOnStop = true;
                    AddMacro("AUTO_RUN", "L3");
                    AddMacro("DASH_SPRINT_ONLY", "CROSS");
                    AddMacro("KICK", "CROSS + R1");
                    AddMacro("LEAP_ATTACK", "CROSS + R2");
                    break;

                // Jump on CROSS as in Elden Ring, with kick and leap on CROSS combos.
                case "ELDEN_RING":
                    _muteJumpFromB = true;
                    _suppressMacroButtons = false;
                    _autoRunCancelOnStop = true;
                    AddMacro("JUMP_ONLY", "CROSS");
                    AddMacro("KICK", "CROSS + R1");
                    AddMacro("LEAP_ATTACK", "CROSS + R2");
                    AddMacro("AUTO_RUN", "L3");
                    break;

                // Vanilla movement, plus auto-run and instant kick/leap on CIRCLE combos.
                case "RUN_AND_ATTACK":
                    _muteJumpFromB = false;
                    _suppressMacroButtons = true;
                    AddMacro("AUTO_RUN", "L3");
                    AddMacro("KICK", "CIRCLE + R1");
                    AddMacro("LEAP_ATTACK", "CIRCLE + R2");
                    break;

                default:
                    // Unrecognised preset name: fall back to no macros rather than
                    // silently applying something the user did not ask for.
                    break;
            }
        }

        // SDL reports the stick Y axis inverted relative to XInput. The old '~' trick
        // was off by one (~0 = -1), so a resting stick never read as exactly centre.
        private static short InvertAxis(short v)
        {
            return (short)(v == short.MinValue ? short.MaxValue : -v);
        }

        private static SDL.SDL_GameControllerButton ParsePhysicalButton(string btn)
        {
            return btn.ToUpper() switch
            {
                "A" or "CROSS" or "SOUTH" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A,
                "B" or "CIRCLE" or "EAST" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B,
                "X" or "SQUARE" or "WEST" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X,
                "Y" or "TRIANGLE" or "NORTH" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y,
                "LB" or "L1" or "L" or "LEFTBUMPER" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER,
                "RB" or "R1" or "R" or "RIGHTBUMPER" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER,
                "LS" or "L3" or "LEFTSTICK" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSTICK,
                "RS" or "R3" or "RIGHTSTICK" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSTICK,
                "BACK" or "SELECT" or "SHARE" or "CREATE" or "MINUS" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK,
                "START" or "OPTIONS" or "PLUS" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START,
                "GUIDE" or "PS" or "HOME" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_GUIDE,
                "TOUCHPAD" or "TOUCH" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_TOUCHPAD,
                "DPAD_UP" or "UP" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP,
                "DPAD_DOWN" or "DOWN" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN,
                "DPAD_LEFT" or "LEFT" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT,
                "DPAD_RIGHT" or "RIGHT" => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT,
                _ => SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_INVALID
            };
        }

        private static SDL.SDL_GameControllerAxis ParsePhysicalAxis(string axis)
        {
            return axis.ToUpper() switch
            {
                "LT" or "L2" or "ZL" or "LEFTTRIGGER" => SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERLEFT,
                "RT" or "R2" or "ZR" or "RIGHTTRIGGER" => SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERRIGHT,
                _ => SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_INVALID
            };
        }

        private static ushort ParseVirtualButton(string btn)
        {
            return btn.ToUpper() switch
            {
                "A" => MASK_A,
                "B" => MASK_B,
                "X" => MASK_X,
                "Y" => MASK_Y,
                "LB" or "LEFTSHOULDER" => MASK_LB,
                "RB" or "RIGHTSHOULDER" => MASK_RB,
                "LS" or "LEFTSTICK" => MASK_LS,
                "RS" or "RIGHTSTICK" => MASK_RS,
                "START" or "OPTIONS" => MASK_START,
                "BACK" or "SELECT" => MASK_BACK,
                "DPAD_UP" => MASK_DPAD_UP,
                "DPAD_DOWN" => MASK_DPAD_DOWN,
                "DPAD_LEFT" => MASK_DPAD_LEFT,
                "DPAD_RIGHT" => MASK_DPAD_RIGHT,
                _ => 0
            };
        }

        private static void LoadConfig()
        {
            string iniPath = "DDTMControllerMappings.ini";
            if (!File.Exists(iniPath))
            {
                File.WriteAllText(iniPath,
@"# ===========================================================================
#  DDTM CONTROLLER FIX
#
#  QUICK START: pick a preset below and you are done.
#  Everything after the preset section is optional fine tuning.
#
#  TO RESET EVERYTHING: delete this file. The game writes a fresh one on
#  the next launch.
#
#  RULE: '#' only works at the START of a line. Never write a note on the
#  same line as a setting - it becomes part of the value and breaks it.
#    GOOD:  # my note
#           A = CROSS
#    BAD:   A = CROSS   # my note
# ===========================================================================


# ===========================================================================
#  1. PRESET  -  START HERE
# ===========================================================================
# A preset sets up the macros AND the behaviour switches that go with them, in
# one go. Presets never change your button mapping in section 2, so your
# controller layout (including the Japanese layout) is always kept.
#
# A preset overrides the macro lines in section 2 and settings in
# section 3.
# To build your own setup instead, leave this on NONE.
#
# Choose ONE:
#
# ---------------------------------------------------------------------------
#   NONE
# ---------------------------------------------------------------------------
#   Vanilla controls, no macros. You still get the controller support,
#   deadzones, sensitivity and rumble fixes.
#
# ---------------------------------------------------------------------------
#   DS3_JUMP          Jump on its own button, like Dark Souls 3
# ---------------------------------------------------------------------------
#     L3  = jump
#   Your roll button can no longer jump by accident, so the only jumps you get
#   are the ones you pressed L3 for. Rolling, sprinting, backstepping and
#   Cancel all behave exactly as normal.
#   The simplest preset, and the one that changes the least.
#
# ---------------------------------------------------------------------------
#   MORTAL_SHELL      Sprint moved onto CROSS
# ---------------------------------------------------------------------------
#     CROSS      = hold to sprint
#     L3         = toggle auto-run
#     CROSS + R1 = kick
#     CROSS + R2 = leaping attack
#   Adds a second sprint button on CROSS, so you can run without touching the
#   roll button. CIRCLE is left exactly as the game made it and still rolls,
#   sprints and jumps as normal. CROSS keeps its normal job too, so menus work.
#
# ---------------------------------------------------------------------------
#   ELDEN_RING        Jump on CROSS, like Elden Ring
# ---------------------------------------------------------------------------
#     CROSS      = jump
#     L3         = toggle auto-run
#     CROSS + R1 = kick
#     CROSS + R2 = leaping attack
#   Your roll button can no longer jump by accident. CROSS keeps its normal
#   job, so menus still work: standing still it just interacts, and it only
#   jumps while you are holding the stick in a direction.
#   Same warning as above - CROSS is also Interact, so a jump next to a lever
#   or item can set that thing off.
#
# ---------------------------------------------------------------------------
#   RUN_AND_ATTACK    Vanilla movement, better attacks
# ---------------------------------------------------------------------------
#     L3          = toggle auto-run
#     CIRCLE + R1 = kick
#     CIRCLE + R2 = leaping attack
#   Rolling, sprinting and jumping are left exactly as the game made them.
#   Adds only the auto-run for long runbacks and reliable kicks and leaping
#   attacks, with no stick flick needed.
#   The safest choice if you do not want your movement touched at all.
# ---------------------------------------------------------------------------

Preset = NONE


# ===========================================================================
#  2. BUTTON MAPPING
# ===========================================================================
[Bindings]

# Format:  GAME_INPUT = YOUR_CONTROLLER_BUTTON
# The left side is what the game expects. The right side is the real button
# you want to press. Leave as NONE to disable an input.
#
# Game inputs you can map (left side):
#   A, B, X, Y, LB, RB, LT, RT, LS, RS, BACK, START,
#   DPAD_UP, DPAD_DOWN, DPAD_LEFT, DPAD_RIGHT
#
# Buttons you can map them to (right side):
#   PlayStation  CROSS, CIRCLE, SQUARE, TRIANGLE, L1, R1, L2, R2, L3, R3,
#                SHARE, OPTIONS, PS, TOUCHPAD
#   Xbox         A, B, X, Y, LB, RB, LT, RT, LS, RS, BACK, START, GUIDE
#   Nintendo     SOUTH, EAST, WEST, NORTH, L, R, ZL, ZR, MINUS, PLUS
#   D-Pad        DPAD_UP, DPAD_DOWN, DPAD_LEFT, DPAD_RIGHT

A = CROSS
B = CIRCLE
X = SQUARE
Y = TRIANGLE
LB = L1
RB = R1
LT = L2
RT = R2
LS = L3
RS = R3
BACK = TOUCHPAD
BACK = SHARE
START = OPTIONS
DPAD_UP = DPAD_UP
DPAD_DOWN = DPAD_DOWN
DPAD_LEFT = DPAD_LEFT
DPAD_RIGHT = DPAD_RIGHT

# --- JAPANESE PS3 LAYOUT ---
# Confirm/Interact on CIRCLE, Roll/Sprint/Cancel on CROSS, as in the Japanese
# PS3 release. To use it, change the two lines above to read exactly:
#   A = CIRCLE
#   B = CROSS
# You must change BOTH. Changing only one puts two functions on one button and
# leaves the other button dead. Leave the game's own controller options at
# their defaults, or the two swaps cancel each other out.

# --- MACROS (only used when Preset = NONE) ---
# Assign one button (L3) or a combination joined with '+' (R1 + L3).
# Maximum of 3 buttons in a combination.
#
#   KICK              Instant kick.
#   LEAP_ATTACK       Instant jumping heavy attack.
#   AUTO_RUN          Toggle auto-sprint. Push the stick and you run on your
#                     own. Switches off when you stop, attack or open a menu.
#   DASH_SPRINT_ONLY  Hold to sprint. Taps can never roll, and pressing it
#                     again right after cannot jump.
#   ROLL_ONLY         Always rolls, never sprints, however long you hold it.
#                     Standing still it backsteps, as the game normally does.
#   JUMP_ONLY         Jump. You must be holding the stick in a direction;
#                     it does nothing standing still, because the game has no
#                     standing jump. Instant while already sprinting, with a
#                     short wind-up from a walk.


KICK = NONE
LEAP_ATTACK = NONE
AUTO_RUN = NONE
DASH_SPRINT_ONLY = NONE
ROLL_ONLY = NONE
JUMP_ONLY = NONE

# --- MACRO BEHAVIOUR ---
# AutoRun_CancelOnStop
#   TRUE  auto-sprint switches off once you let the stick go, so moving again
#         walks. Press the button again to turn it back on.
#   FALSE it stays on until you press the button, attack, or open a menu.
AutoRun_CancelOnStop = TRUE

# SuppressMacroButtons
#   TRUE  a button used by a single-button macro stops sending its normal
#         input, so you do not get both at once. Combinations are unaffected.
#   FALSE the button sends the macro AND its normal input.
SuppressMacroButtons = TRUE

# ===========================================================================
#  3. STICKS, TRIGGERS AND RUMBLE
# ===========================================================================
[Settings]

# --- STICK DEADZONES ---
# Range 0 to 32767. Calculated as a circle, not per axis.
# Min: the resting area. Raise it if your character drifts on its own.
# Max: how far you must push to get 100%. Lower it if diagonals will not run.
LS_DeadzoneMin = 7000
LS_DeadzoneMax = 22000
RS_DeadzoneMin = 3000
RS_DeadzoneMax = 22000

# --- STICK SENSITIVITY ---
# 1.0 = normal. 1.5 = 50% faster. 0.5 = half speed.
# LS is movement, RS is the camera.
LS_Sensitivity_X = 1.0
LS_Sensitivity_Y = 1.0
RS_Sensitivity_X = 1.5
RS_Sensitivity_Y = 1.5

# --- TRIGGERS ---
# Range 0 to 32767.
# Deadzone: how far you must pull before the game sees anything. Raising it
# stops accidental heavy attacks from a resting finger.
# ActivationThreshold: the point counted as fully pulled. Lowering it gives a
# hair trigger.
LT_Deadzone = 1000
RT_Deadzone = 4000
LT_ActivationThreshold = 30000
RT_ActivationThreshold = 30000

# --- RUMBLE ---
# 1.0 = normal strength. 0 turns a motor off.
# Left is heavy impacts, right is light feedback.
Rumble_LeftMotor = 1.0
Rumble_RightMotor = 1.0

# MUTE_JUMP_FROM_B
# The game has no jump button. A jump is 'let go of the run button while you are
# running, then press it again straight away'. That is easy to do by accident
# when you are repositioning, and it has killed a lot of characters.
#   TRUE  your normal roll/sprint button can no longer jump. Everything else it
#         does is untouched: tap to roll, hold to sprint, backstep standing
#         still, Cancel in menus. Only a press that lands immediately after a
#         sprint is swallowed, and only for the length of JumpGuard_Ms.
#   FALSE the button behaves exactly as the game made it, jumps included.
# This does NOT disable the JUMP_ONLY macro. If you want to keep jumping, set
# this to TRUE and put JUMP_ONLY on a button of its own, so the only jumps you
# ever get are the ones you asked for.
MUTE_JUMP_FROM_B = FALSE


# ===========================================================================
#  4. ADVANCED  -  you should not need to change these
# ===========================================================================
# These were measured against the game and are the same on every PC, so they
# are built in. The two lines below are switched off (they start with '#').
# Remove the '#' only if you have a reason to.
[Advanced]

# RunState_Ms: how long the game needs the run button held before it commits
# to a run. Everything about sprinting and jumping is timed from this.
# Raise it if a quick tap of the sprint macro still rolls you.
# RunState_Ms = 800

# JumpGuard_Ms: after a sprint ends, sprint presses are ignored for this long
# so the game leaves the run state and your next press sprints instead of
# jumping. Raise it if mashing sprint still jumps. Lower it if sprinting feels
# slow to start again after you let go.
# JumpGuard_Ms = 500");
            }

            _buttonMap.Clear();
            _macros.Clear();
            string[] lines = File.ReadAllLines(iniPath);
            bool inBindings = false, inSettings = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("#") || string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("["))
                {
                    inBindings = line.Equals("[Bindings]", StringComparison.OrdinalIgnoreCase);

                    // [Advanced] holds the same kind of numeric tuning as [Settings],
                    // so it is parsed by the same branch.
                    inSettings = line.Equals("[Settings]", StringComparison.OrdinalIgnoreCase)
                              || line.Equals("[Advanced]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                string[] parts = line.Split('=');
                if (parts.Length != 2) continue;

                string key = parts[0].Trim().ToUpper();
                string val = parts[1].Trim().ToUpper();

                // Accepted from any section, so a stray Preset line still works.
                if (key == "PRESET")
                {
                    _preset = val;
                    continue;
                }

                if (inBindings)
                {
                    // Catch Non-Standard Macros
                    string[] specialKeys = { "KICK", "LEAP_ATTACK", "AUTO_RUN", "JUMP_ONLY", "DASH_SPRINT_ONLY", "ROLL_ONLY" };
                    if (Array.Exists(specialKeys, k => k == key))
                    {
                        AddMacro(key, val);
                        continue;
                    }

                    if (key == "LT")
                    {
                        var axis = ParsePhysicalAxis(val);
                        if (axis != SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_INVALID) _ltAxis = axis;
                        continue;
                    }
                    if (key == "RT")
                    {
                        var axis = ParsePhysicalAxis(val);
                        if (axis != SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_INVALID) _rtAxis = axis;
                        continue;
                    }
                    ushort virtBtnMask = ParseVirtualButton(key);
                    SDL.SDL_GameControllerButton physBtn = ParsePhysicalButton(val);
                    if (physBtn != SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_INVALID && virtBtnMask != 0)
                    {
                        if (_buttonMap.ContainsKey(physBtn)) _buttonMap[physBtn] |= virtBtnMask;
                        else _buttonMap[physBtn] = virtBtnMask;
                    }
                }
                else if (inSettings)
                {
                    // Parse radial modifiers
                    if (key == "LS_DEADZONEMIN") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _lsDeadzoneMin);
                    if (key == "LS_DEADZONEMAX") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _lsDeadzoneMax);
                    if (key == "RS_DEADZONEMIN") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rsDeadzoneMin);
                    if (key == "RS_DEADZONEMAX") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rsDeadzoneMax);

                    // For backwards compatibility
                    if (key == "LS_DEADZONEMIN_X") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _lsDeadzoneMin);
                    if (key == "LS_DEADZONEMAX_X") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _lsDeadzoneMax);
                    if (key == "RS_DEADZONEMIN_X") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rsDeadzoneMin);
                    if (key == "RS_DEADZONEMAX_X") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rsDeadzoneMax);

                    // Fix the float parsing bug
                    if (key == "LS_SENSITIVITY_X") float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _lsSensX);
                    if (key == "LS_SENSITIVITY_Y") float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _lsSensY);
                    if (key == "RS_SENSITIVITY_X") float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rsSensX);
                    if (key == "RS_SENSITIVITY_Y") float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rsSensY);

                    if (key == "LT_DEADZONE") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _ltDeadzone);
                    if (key == "RT_DEADZONE") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rtDeadzone);
                    if (key == "LT_ACTIVATIONTHRESHOLD") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _ltThreshold);
                    if (key == "RT_ACTIVATIONTHRESHOLD") short.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rtThreshold);

                    // Macro behaviour
                    if (key == "SUPPRESSMACROBUTTONS") _suppressMacroButtons = ParseBool(val);
                    if (key == "AUTORUN_CANCELONSTOP") _autoRunCancelOnStop = ParseBool(val);
                    if (key == "MUTE_JUMP_FROM_B") _muteJumpFromB = ParseBool(val);

                    // Advanced overrides. SafeRelease_Ms is the old name for
                    // RunState_Ms and is still accepted so older INIs keep working.
                    if (key == "RUNSTATE_MS" || key == "SAFERELEASE_MS") ParseMs(val, ref _runStateMs, 3000);
                    if (key == "JUMPGUARD_MS") ParseMs(val, ref _jumpGuardMs, 2000);

                    // Fix the float parsing bug for rumble too
                    if (key == "RUMBLE_LEFTMOTOR") float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rumbleLeft);
                    if (key == "RUMBLE_RIGHTMOTOR") float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _rumbleRight);
                }
            }

            // A preset, if chosen, replaces whatever macros the file listed.
            ApplyPreset();

            // Stop a single-button macro's button from ALSO firing its normal binding.
            // Runs after the whole file is parsed, because [Settings] comes after [Bindings].
            // Combos are left alone: 'KICK = B + RT' still needs B and RT to work normally.
            if (_suppressMacroButtons)
            {
                foreach (var macro in _macros)
                {
                    if (macro.Buttons.Count == 1 && macro.Axes.Count == 0)
                        _buttonMap.Remove(macro.Buttons[0]);
                }
            }
        }

        // ---------------------------------------------------------
        // Axis Math Helpers
        // ---------------------------------------------------------

        private static void ApplyRadialStickMath(
            short rawX, short rawY,
            short minDz, short maxDz,
            float sensX, float sensY,
            out short outX, out short outY)
        {
            // Cast to double to prevent overflow and maintain decimal precision
            double x = rawX;
            double y = rawY;
            double magnitude = Math.Sqrt((x * x) + (y * y));

            // Inner Deadzone calculation
            if (magnitude < minDz)
            {
                outX = 0;
                outY = 0;
                return;
            }

            // Failsafe if user configured INI incorrectly
            if (maxDz <= minDz) maxDz = 32767;

            // Normalize magnitude between the min and max limits
            double normalizedMag = (magnitude - minDz) / (maxDz - minDz);

            // Cap normalized magnitude at 1.0 so edge snapping behaves smoothly 
            if (normalizedMag > 1.0) normalizedMag = 1.0;

            // Find ratio of raw axes to total magnitude 
            double ratioX = x / magnitude;
            double ratioY = y / magnitude;

            // Re-apply magnitude based on ratios, scale up to short.MaxValue, and multiply by sensitivity
            double finalX = ratioX * normalizedMag * 32767 * sensX;
            double finalY = ratioY * normalizedMag * 32767 * sensY;

            // Clamp results securely between -32768 and 32767 to avoid XInput crashes
            outX = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, finalX));
            outY = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, finalY));
        }

        private static byte ApplyTriggerMath(short rawValue, short deadzone, short threshold)
        {
            if (rawValue < deadzone) return 0;
            if (rawValue >= threshold || threshold <= deadzone) return 255;

            float normalized = (float)(rawValue - deadzone) / (threshold - deadzone);
            return (byte)Math.Min((int)(normalized * 255), 255);
        }

        // ---------------------------------------------------------
        // 3. The Boot Hook & DirectInput VTable Patch
        // ---------------------------------------------------------
        [UnmanagedCallersOnly(EntryPoint = "DirectInput8Create", CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        public static int DirectInput8Create(IntPtr hinst, uint dwVersion, IntPtr riidltf, IntPtr ppvOut, IntPtr punkOuter)
        {
            if (_hookEngine == null)
            {
                SDL.SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
                SDL.SDL_SetHint("SDL_XINPUT_ENABLED", "0");
                SDL.SDL_SetHint("SDL_DIRECTINPUT_ENABLED", "0");

                SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS4", "1");
                SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS5", "1");
                SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS4_RUMBLE", "1");
                SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS5_RUMBLE", "1");

                SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_HAPTIC);

                _hookEngine = new HookEngine();
                IntPtr xinputModule = NativeLibrary.Load("xinput1_3.dll");

                // Hook both GetState and SetState (Rumble)
                IntPtr xinputGetAddress = NativeLibrary.GetExport(xinputModule, "XInputGetState");
                IntPtr xinputSetAddress = NativeLibrary.GetExport(xinputModule, "XInputSetState");

                _hookEngine.CreateHook<XInputGetStateDelegate>(xinputGetAddress, DetourXInputGetState);
                _hookEngine.CreateHook<XInputSetStateDelegate>(xinputSetAddress, DetourXInputSetState);
                _hookEngine.EnableHooks();
            }

            IntPtr realDinput = NativeLibrary.Load("C:\\Windows\\SysWOW64\\dinput8.dll");
            IntPtr realCreate = NativeLibrary.GetExport(realDinput, "DirectInput8Create");

            int hr;
            unsafe
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr, int>)realCreate)(hinst, dwVersion, riidltf, ppvOut, punkOuter);
            }

            if (hr == 0 && ppvOut != IntPtr.Zero && _originalEnumDevices == null)
            {
                unsafe
                {
                    IntPtr pDI = Marshal.ReadIntPtr(ppvOut);
                    IntPtr vTable = Marshal.ReadIntPtr(pDI);
                    IntPtr enumDevicesAddr = vTable + (4 * IntPtr.Size);

                    VirtualProtect(enumDevicesAddr, (UIntPtr)IntPtr.Size, 0x40, out uint oldProtect);
                    IntPtr originalPtr = Marshal.ReadIntPtr(enumDevicesAddr);
                    _originalEnumDevices = Marshal.GetDelegateForFunctionPointer<EnumDevicesDelegate>(originalPtr);
                    _detourEnumDevices = new EnumDevicesDelegate(DetourEnumDevices);
                    Marshal.WriteIntPtr(enumDevicesAddr, Marshal.GetFunctionPointerForDelegate(_detourEnumDevices));
                    VirtualProtect(enumDevicesAddr, (UIntPtr)IntPtr.Size, oldProtect, out _);
                }
            }
            return hr;
        }

        public static int DetourEnumDevices(IntPtr pThis, uint dwDevType, IntPtr lpCallback, IntPtr pvRef, uint dwFlags)
        {
            if ((dwDevType & 0xFF) == 4) return 0;
            return _originalEnumDevices!(pThis, dwDevType, lpCallback, pvRef, dwFlags);
        }

        // ---------------------------------------------------------
        // 4. Dynamic Zero-Latency XInput Translation
        // ---------------------------------------------------------
        public static uint DetourXInputSetState(uint dwUserIndex, IntPtr pVibration)
        {
            if (dwUserIndex != 0 || _controller == IntPtr.Zero) return 1167; // ERROR_DEVICE_NOT_CONNECTED

            unsafe
            {
                XINPUT_VIBRATION* vib = (XINPUT_VIBRATION*)pVibration;

                // SDL rumble expects 0-65535. XInput gives 0-65535. Multiply by custom multiplier.
                ushort lowFreq = (ushort)Math.Min(vib->wLeftMotorSpeed * _rumbleLeft, 65535);
                ushort highFreq = (ushort)Math.Min(vib->wRightMotorSpeed * _rumbleRight, 65535);

                SDL.SDL_GameControllerRumble(_controller, lowFreq, highFreq, 100); // 100ms duration per tick
            }
            return 0;
        }

        public static uint DetourXInputGetState(uint dwUserIndex, IntPtr pState)
        {
            if (dwUserIndex != 0) return 1167;

            if (!_configLoaded)
            {
                LoadConfig();
                _configLoaded = true;
            }

            SDL.SDL_GameControllerUpdate();

            if (_controller == IntPtr.Zero)
            {
                for (int i = 0; i < SDL.SDL_NumJoysticks(); i++)
                {
                    if (SDL.SDL_IsGameController(i) == SDL.SDL_bool.SDL_TRUE)
                    {
                        _controller = SDL.SDL_GameControllerOpen(i);
                        break;
                    }
                }
                if (_controller == IntPtr.Zero) return 1167;
            }

            // One clock sample per poll, shared by every macro below.
            _nowMs = SDL.SDL_GetTicks();

            XINPUT_STATE state = new XINPUT_STATE();
            ushort buttons = 0;

            foreach (var mapping in _buttonMap)
            {
                if (SDL.SDL_GameControllerGetButton(_controller, mapping.Key) == 1)
                {
                    buttons |= mapping.Value;
                }
            }

            // MuteJumpFromB: lift the mapped B press out of the button word so it goes
            // through the sprint arbiter below instead of straight to the game. That is
            // the only way the jump guard can see it. Rolling, sprinting, backstepping
            // and Cancel all still work, because the guard only ever blocks a press that
            // arrives right after a sprint - which is exactly the jump input.
            bool physicalB = false;
            if (_muteJumpFromB && (buttons & MASK_B) != 0)
            {
                physicalB = true;
                buttons &= unchecked((ushort)~MASK_B);
            }

            state.Gamepad.wButtons = buttons;

            // Read raw axis data
            short rawLT = SDL.SDL_GameControllerGetAxis(_controller, _ltAxis);
            short rawRT = SDL.SDL_GameControllerGetAxis(_controller, _rtAxis);
            short rawLSX = SDL.SDL_GameControllerGetAxis(_controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX);
            short rawLSY = InvertAxis(SDL.SDL_GameControllerGetAxis(_controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY));
            short rawRSX = SDL.SDL_GameControllerGetAxis(_controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTX);
            short rawRSY = InvertAxis(SDL.SDL_GameControllerGetAxis(_controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTY));

            // Apply modifiers
            state.Gamepad.bLeftTrigger = ApplyTriggerMath(rawLT, _ltDeadzone, _ltThreshold);
            state.Gamepad.bRightTrigger = ApplyTriggerMath(rawRT, _rtDeadzone, _rtThreshold);

            // Process Radial Stick Math out to the struct parameters directly
            ApplyRadialStickMath(rawLSX, rawLSY, _lsDeadzoneMin, _lsDeadzoneMax, _lsSensX, _lsSensY, out state.Gamepad.sThumbLX, out state.Gamepad.sThumbLY);
            ApplyRadialStickMath(rawRSX, rawRSY, _rsDeadzoneMin, _rsDeadzoneMax, _rsSensX, _rsSensY, out state.Gamepad.sThumbRX, out state.Gamepad.sThumbRY);

            // Whether the GAME sees movement, measured after the deadzone is applied.
            bool stickLive = state.Gamepad.sThumbLX != 0 || state.Gamepad.sThumbLY != 0;

            // ---------------------------------------------------------
            // Non-Standard Bindings Execution
            // ---------------------------------------------------------
            bool kickPressed = false, leapPressed = false, autoRunPressed = false;
            bool jumpPressed = false, dashSprintPressed = false, rollPressed = false;

            foreach (var macro in _macros)
            {
                if (macro.IsPressed(_controller))
                {
                    switch (macro.ActionName)
                    {
                        case "KICK": kickPressed = true; break;
                        case "LEAP_ATTACK": leapPressed = true; break;
                        case "AUTO_RUN": autoRunPressed = true; break;
                        case "JUMP_ONLY": jumpPressed = true; break;
                        case "DASH_SPRINT_ONLY": dashSprintPressed = true; break;
                        case "ROLL_ONLY": rollPressed = true; break;
                    }
                }
            }

            // 1. KICK (forward flick + RB)
            if (kickPressed && !_wasKickBtnPressed)
            {
                _kickActive = true;
                _kickStartMs = _nowMs;
            }
            if (_kickActive)
            {
                uint elapsed = _nowMs - _kickStartMs;
                if (elapsed < MACRO_RELEASE_MS)
                {
                    state.Gamepad.sThumbLX = 0; state.Gamepad.sThumbLY = 0; // Drop stick to neutral
                    state.Gamepad.wButtons &= unchecked((ushort)~MASK_RB);  // Release RB
                }
                else if (elapsed < MACRO_RELEASE_MS + MACRO_PRESS_MS)
                {
                    state.Gamepad.sThumbLX = 0; state.Gamepad.sThumbLY = 32767; // Slam forward
                    state.Gamepad.wButtons |= MASK_RB;                          // Press RB
                }
                else
                {
                    _kickActive = false;
                }
            }
            _wasKickBtnPressed = kickPressed;

            // 2. LEAP ATTACK (forward flick + RT)
            if (leapPressed && !_wasLeapBtnPressed)
            {
                _leapActive = true;
                _leapStartMs = _nowMs;
            }
            if (_leapActive)
            {
                uint elapsed = _nowMs - _leapStartMs;
                if (elapsed < MACRO_RELEASE_MS)
                {
                    state.Gamepad.sThumbLX = 0; state.Gamepad.sThumbLY = 0;
                    state.Gamepad.bRightTrigger = 0;   // Release RT
                }
                else if (elapsed < MACRO_RELEASE_MS + MACRO_PRESS_MS)
                {
                    state.Gamepad.sThumbLX = 0; state.Gamepad.sThumbLY = 32767;
                    state.Gamepad.bRightTrigger = 255; // Press RT
                }
                else
                {
                    _leapActive = false;
                }
            }
            _wasLeapBtnPressed = leapPressed;

            // Sprint requests are collected here and arbitrated in one place further
            // down, so two macros can never produce a release/press gap between them.
            bool wantSprint = false;

            // 3. AUTO RUN (toggle, holds sprint only while actually moving)
            bool isAttacking = (state.Gamepad.wButtons & (MASK_LB | MASK_RB)) != 0
                               || state.Gamepad.bRightTrigger > 0
                               || state.Gamepad.bLeftTrigger > 0;

            // Rough "a menu is probably opening" test. A/START/BACK are the buttons
            // that open screens where a held B would back you straight out again.
            bool menuish = (state.Gamepad.wButtons & (MASK_A | MASK_START | MASK_BACK)) != 0;

            if (autoRunPressed && !_wasAutoRunBtnPressed)
            {
                _autoRunActive = !_autoRunActive;
                _autoRunEngaged = false;
                _autoRunHasRun = false;
                _autoRunLiveTiming = false;
                _autoRunNeutralTiming = false;
            }

            if (_autoRunActive && (isAttacking || menuish))
            {
                _autoRunActive = false;
                _autoRunEngaged = false;
            }

            if (_autoRunActive)
            {
                if (stickLive)
                {
                    // Timing uses explicit flags, never a zero timestamp as a sentinel:
                    // a single poll of stick bounce must not silently restart a timer.
                    _autoRunNeutralTiming = false;

                    if (!_autoRunLiveTiming)
                    {
                        _autoRunLiveTiming = true;
                        _autoRunLiveSince = _nowMs;
                    }

                    // Debounce before grabbing B. A momentary stick flick must never
                    // produce a B press, because a short B press with the stick back at
                    // neutral is exactly what the engine reads as a backstep.
                    if (_nowMs - _autoRunLiveSince >= AUTORUN_ENGAGE_MS)
                    {
                        _autoRunEngaged = true;
                        _autoRunHasRun = true; // the character has genuinely sprinted
                    }
                }
                else
                {
                    _autoRunLiveTiming = false;

                    if (!_autoRunNeutralTiming)
                    {
                        _autoRunNeutralTiming = true;
                        _autoRunNeutralSince = _nowMs;
                    }

                    if (_nowMs - _autoRunNeutralSince >= AUTORUN_STOP_MS)
                    {
                        _autoRunEngaged = false;

                        // Switch the toggle off so moving again does NOT silently sprint.
                        // Gated on _autoRunHasRun: arming auto-run while standing still
                        // must leave it armed, not cancel itself before you ever move.
                        if (_autoRunCancelOnStop && _autoRunHasRun) _autoRunActive = false;
                    }
                }

                if (_autoRunEngaged) wantSprint = true;
            }
            _wasAutoRunBtnPressed = autoRunPressed;

            // 4. DASH SPRINT ONLY (hold to sprint; taps become micro-sprints)
            if (dashSprintPressed)
            {
                if (!_wasDashSprintPressed)
                {
                    _dashPressStartMs = _nowMs;
                    _dashEngaged = false;
                    _dashNeutralSince = 0;
                    _dashBleeding = false;
                }

                if (stickLive)
                {
                    _dashEngaged = true;
                    _dashNeutralSince = 0;
                }
                else if (_dashEngaged)
                {
                    if (_dashNeutralSince == 0) _dashNeutralSince = _nowMs;
                    if (_nowMs - _dashNeutralSince >= STICK_SUSTAIN_MS) _dashEngaged = false;
                }

                // Pressing this with a neutral stick does nothing at all now. Holding B
                // while standing still only ever produces a backstep on release.
                if (_dashEngaged) wantSprint = true;
            }
            else
            {
                if (_wasDashSprintPressed && _dashEngaged && (_nowMs - _dashPressStartMs) < _runStateMs)
                    _dashBleeding = true;

                if (_dashBleeding)
                {
                    if ((_nowMs - _dashPressStartMs) < _runStateMs) wantSprint = true;
                    else _dashBleeding = false;
                }
            }
            _wasDashSprintPressed = dashSprintPressed;

            // ---------------------------------------------------------
            // SPRINT ARBITER + JUMP GUARD
            //
            // A Dark Souls jump is 'release the run button while in the run state,
            // then press it again quickly'. Any macro that holds sprint and lets go
            // sets that up, which is why re-pressing the sprint button used to jump.
            //
            // Two rules prevent it:
            //   - if sprint was already being emitted last poll, keep emitting it, so
            //     two overlapping sprint sources never leave a gap between them;
            //   - after a sprint that actually got established, swallow sprint
            //     re-presses until the guard window has passed, so the engine leaves
            //     the run state and the next press is read as a sprint, not a jump.
            // ---------------------------------------------------------
            if (_guardArmed && (_nowMs - _sprintEndMs) >= _jumpGuardMs) _guardArmed = false;

            // The mapped B button joins the macro sprint sources, so one set of rules
            // covers both. Only reached when MuteJumpFromB is on.
            if (physicalB) wantSprint = true;

            if (wantSprint)
            {
                if (_bHeldLast || !_guardArmed || _jumpGuardMs == 0)
                    state.Gamepad.wButtons |= MASK_B;
            }

            // 5. ROLL ONLY (always a roll, never a sprint)
            if (rollPressed && !_wasRollPressed)
            {
                _rollActive = true;
                _rollStartMs = _nowMs;
            }
            if (_rollActive)
            {
                uint elapsed = _nowMs - _rollStartMs;
                if (elapsed < ROLL_TAP_MS)
                {
                    state.Gamepad.wButtons |= MASK_B;
                }
                else
                {
                    // Force B back off even if the player is still holding the button,
                    // so a long hold can never turn into a sprint.
                    state.Gamepad.wButtons &= unchecked((ushort)~MASK_B);
                    if (!rollPressed) _rollActive = false;
                }
            }
            _wasRollPressed = rollPressed;

            // 6. JUMP ONLY (evaluated LAST so it can override every sprint source above)
            // Requires a live stick: DS1 has no standing jump, and the old ramp with a
            // neutral stick just produced two backsteps in a row.
            if (jumpPressed && !_wasJumpPressed && stickLive && !_jumpActive)
            {
                _jumpActive = true;
                _jumpStartMs = _nowMs;

                // If sprint is already being emitted this poll the run state exists and
                // the jump is immediate. Otherwise build the run state first.
                _jumpNeedsRamp = (state.Gamepad.wButtons & MASK_B) == 0;
            }
            if (_jumpActive)
            {
                uint elapsed = _nowMs - _jumpStartMs;
                uint rampEnd = _jumpNeedsRamp ? _runStateMs : 0;
                uint gapEnd = rampEnd + JUMP_GAP_MS;
                uint tapEnd = gapEnd + JUMP_TAP_MS;

                if (elapsed < rampEnd)
                {
                    state.Gamepad.wButtons |= MASK_B;  // Build the run state
                }
                else if (elapsed < gapEnd)
                {
                    state.Gamepad.wButtons &= unchecked((ushort)~MASK_B); // Release so the tap registers
                }
                else if (elapsed < tapEnd)
                {
                    state.Gamepad.wButtons |= MASK_B;  // The jump itself
                }
                else
                {
                    _jumpActive = false;
                }
            }
            _wasJumpPressed = jumpPressed;

            // ---------------------------------------------------------
            // Guard bookkeeping, using the FINAL B state for this poll.
            // Only a hold long enough to have become a real sprint arms the guard, so
            // an ordinary roll tap never blocks the sprint that follows it. JUMP_ONLY
            // is exempt because its own ramp/gap/tap pattern needs the release.
            // ---------------------------------------------------------
            bool bNow = (state.Gamepad.wButtons & MASK_B) != 0;

            if (bNow && !_bHeldLast)
            {
                _bHoldStartMs = _nowMs;
                _bHoldWasMoving = stickLive;
            }
            else if (bNow && stickLive)
            {
                _bHoldWasMoving = true;
            }

            // Arming also requires that the stick was moving during the hold: a sprint
            // always involves movement, while a long Back press in a menu does not. This
            // keeps the guard out of the menus entirely.
            if (!bNow && _bHeldLast && !_jumpActive && _bHoldWasMoving
                && (_nowMs - _bHoldStartMs) >= _runStateMs)
            {
                _sprintEndMs = _nowMs;
                _guardArmed = true;
            }
            _bHeldLast = bNow;

            // XInput contract: dwPacketNumber changes ONLY when the pad state changes.
            // Stamping it with the clock every poll told the game the pad changed on
            // every single frame, which can upset input edge detection.
            if (state.Gamepad.wButtons != _lastGamepad.wButtons
                || state.Gamepad.bLeftTrigger != _lastGamepad.bLeftTrigger
                || state.Gamepad.bRightTrigger != _lastGamepad.bRightTrigger
                || state.Gamepad.sThumbLX != _lastGamepad.sThumbLX
                || state.Gamepad.sThumbLY != _lastGamepad.sThumbLY
                || state.Gamepad.sThumbRX != _lastGamepad.sThumbRX
                || state.Gamepad.sThumbRY != _lastGamepad.sThumbRY)
            {
                _packetNumber++;
                _lastGamepad = state.Gamepad;
            }
            state.dwPacketNumber = _packetNumber;

            unsafe
            {
                XINPUT_STATE* p = (XINPUT_STATE*)pState;
                *p = state;
            }
            return 0;
        }
    }
}