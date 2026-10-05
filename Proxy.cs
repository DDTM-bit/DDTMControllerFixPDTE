using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MinHook;
using SDL2;

namespace PlayStationPDTEinputFix
{
    public static class Proxy
    {
        //!!publish with developer cmd: dotnet publish DDTMControllerFix.csproj -c Release -r win-x86

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
                "A" => 0x1000,
                "B" => 0x2000,
                "X" => 0x4000,
                "Y" => 0x8000,
                "LB" or "LEFTSHOULDER" => 0x0100,
                "RB" or "RIGHTSHOULDER" => 0x0200,
                "LS" or "LEFTSTICK" => 0x0040,
                "RS" or "RIGHTSTICK" => 0x0080,
                "START" or "OPTIONS" => 0x0010,
                "BACK" or "SELECT" => 0x0020,
                "DPAD_UP" => 0x0001,
                "DPAD_DOWN" => 0x0002,
                "DPAD_LEFT" => 0x0004,
                "DPAD_RIGHT" => 0x0008,
                _ => 0
            };
        }

        private static void LoadConfig()
        {
            string iniPath = "DDTMControllerMappings.ini";
            if (!File.Exists(iniPath))
            {
                File.WriteAllText(iniPath,
@"[Bindings]
# FOR DEFAULT SETTINGS: DELETE THIS .INI FILE. A new one will be generated at the start of the game.
# IF something is typed incorrectly, controller might not work. Delete this .ini file and regenerate a new one.

# Format: GAME_INPUT = YOUR_CONTROLLER_BUTTON
# Map what the game expects (Xbox inputs) to the physical buttons on your controller.
#
# Available Game Inputs (Left Side): A, B, X, Y, LB, RB, LT, RT, LS, RS, BACK, START, DPAD_UP, DPAD_DOWN, DPAD_LEFT, DPAD_RIGHT
#
# Available Physical Buttons (Right Side):
# - PlayStation: CROSS, CIRCLE, SQUARE, TRIANGLE, L1, R1, L2, R2, L3, R3, SHARE, OPTIONS, PS, TOUCHPAD
# - Standard/Xbox: A, B, X, Y, LB, RB, LT, RT, LS, RS, BACK, START, GUIDE
# - Nintendo: SOUTH, EAST, WEST, NORTH, L, R, ZL, ZR, MINUS, PLUS
# - D-Pad: DPAD_UP, DPAD_DOWN, DPAD_LEFT, DPAD_RIGHT

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

[Settings]
# --- RADIAL STICK DEADZONES ---
# Deadzones range from 0 to 32767. Mod uses circular radial calculations. The default settings (7000-22000) are recommended for this game.
# Min Deadzones: Defines the inner resting area of the sticks. Raising this eliminates ""stick drift"".
# Max Deadzones: Defines the outer boundary required to reach 100% input. Lower this if diagonals won't register a run state.
LS_DeadzoneMin = 7000
LS_DeadzoneMax = 22000
RS_DeadzoneMin = 7000
RS_DeadzoneMax = 22000

# --- STICK SENSITIVITY ---
# Applies a direct multiplier to stick output. 
# 1.0 = Default 1:1 movement. 1.5 = 50% faster. 0.5 = Half speed.
# You can adjust X (horizontal) and Y (vertical) independently.
LS_Sensitivity_X = 1.0
LS_Sensitivity_Y = 1.0
RS_Sensitivity_X = 1.5
RS_Sensitivity_Y = 1.5

# --- TRIGGERS ---
# Max raw value is 32767.
# Deadzone: How far the physical trigger must be pressed before the game reads input. Raising this prevents accidental heavy attacks.
# Activation Threshold: The point at which the game considers the trigger 100% pulled. Lowering this creates a hair-trigger effect.
LT_Deadzone = 1000
RT_Deadzone = 4000
LT_ActivationThreshold = 30000
RT_ActivationThreshold = 30000

# --- RUMBLE INTENSITY ---
# Scales the vibration commands sent from the game. 1.0 = Default strength.
# Left Motor: Heavy, low-frequency impacts (like getting hit by a boss).
# Right Motor: Light, high-frequency feedback (like weapon clashes).
Rumble_LeftMotor = 1.0
Rumble_RightMotor = 1.0");
            }

            _buttonMap.Clear();
            string[] lines = File.ReadAllLines(iniPath);
            bool inBindings = false, inSettings = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("#") || string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("["))
                {
                    inBindings = line.Equals("[Bindings]", StringComparison.OrdinalIgnoreCase);
                    inSettings = line.Equals("[Settings]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                string[] parts = line.Split('=');
                if (parts.Length != 2) continue;

                string key = parts[0].Trim().ToUpper();
                string val = parts[1].Trim().ToUpper();

                if (inBindings)
                {
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
                    if (key == "LS_DEADZONEMIN") short.TryParse(val, out _lsDeadzoneMin);
                    if (key == "LS_DEADZONEMAX") short.TryParse(val, out _lsDeadzoneMax);
                    if (key == "RS_DEADZONEMIN") short.TryParse(val, out _rsDeadzoneMin);
                    if (key == "RS_DEADZONEMAX") short.TryParse(val, out _rsDeadzoneMax);

                    // For backwards compatibility in case users don't delete old INI file
                    if (key == "LS_DEADZONEMIN_X") short.TryParse(val, out _lsDeadzoneMin);
                    if (key == "LS_DEADZONEMAX_X") short.TryParse(val, out _lsDeadzoneMax);
                    if (key == "RS_DEADZONEMIN_X") short.TryParse(val, out _rsDeadzoneMin);
                    if (key == "RS_DEADZONEMAX_X") short.TryParse(val, out _rsDeadzoneMax);

                    if (key == "LS_SENSITIVITY_X") float.TryParse(val, out _lsSensX);
                    if (key == "LS_SENSITIVITY_Y") float.TryParse(val, out _lsSensY);
                    if (key == "RS_SENSITIVITY_X") float.TryParse(val, out _rsSensX);
                    if (key == "RS_SENSITIVITY_Y") float.TryParse(val, out _rsSensY);

                    if (key == "LT_DEADZONE") short.TryParse(val, out _ltDeadzone);
                    if (key == "RT_DEADZONE") short.TryParse(val, out _rtDeadzone);
                    if (key == "LT_ACTIVATIONTHRESHOLD") short.TryParse(val, out _ltThreshold);
                    if (key == "RT_ACTIVATIONTHRESHOLD") short.TryParse(val, out _rtThreshold);

                    if (key == "RUMBLE_LEFTMOTOR") float.TryParse(val, out _rumbleLeft);
                    if (key == "RUMBLE_RIGHTMOTOR") float.TryParse(val, out _rumbleRight);
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

            XINPUT_STATE state = new XINPUT_STATE();
            state.dwPacketNumber = SDL.SDL_GetTicks();
            ushort buttons = 0;

            foreach (var mapping in _buttonMap)
            {
                if (SDL.SDL_GameControllerGetButton(_controller, mapping.Key) == 1)
                {
                    buttons |= mapping.Value;
                }
            }

            state.Gamepad.wButtons = buttons;

            // Read raw axis data
            short rawLT = SDL.SDL_GameControllerGetAxis(_controller, _ltAxis);
            short rawRT = SDL.SDL_GameControllerGetAxis(_controller, _rtAxis);
            short rawLSX = SDL.SDL_GameControllerGetAxis(_controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX);
            short rawLSY = (short)~SDL.SDL_GameControllerGetAxis(_controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY);
            short rawRSX = SDL.SDL_GameControllerGetAxis(_controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTX);
            short rawRSY = (short)~SDL.SDL_GameControllerGetAxis(_controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTY);

            // Apply modifiers
            state.Gamepad.bLeftTrigger = ApplyTriggerMath(rawLT, _ltDeadzone, _ltThreshold);
            state.Gamepad.bRightTrigger = ApplyTriggerMath(rawRT, _rtDeadzone, _rtThreshold);

            // Process Radial Stick Math out to the struct parameters directly
            ApplyRadialStickMath(rawLSX, rawLSY, _lsDeadzoneMin, _lsDeadzoneMax, _lsSensX, _lsSensY, out state.Gamepad.sThumbLX, out state.Gamepad.sThumbLY);
            ApplyRadialStickMath(rawRSX, rawRSY, _rsDeadzoneMin, _rsDeadzoneMax, _rsSensX, _rsSensY, out state.Gamepad.sThumbRX, out state.Gamepad.sThumbRY);

            unsafe
            {
                XINPUT_STATE* p = (XINPUT_STATE*)pState;
                *p = state;
            }
            return 0;
        }
    }
}