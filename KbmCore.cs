// =====================================================================================
//  Keyboard + mouse for Dark Souls PTDE (Steam). Always on, works together with the
//  controller.
//
//  How it works
//   - The keys and mouse buttons are turned into a "keyboard pad" (Xbox buttons and
//     stick values), which is mixed into what the game reads from XInput.
//   - The game's own keyboard and mouse reading (DirectInput) is blocked, so the
//     in-game keyboard settings do nothing. Only in menus the mouse, the arrow keys,
//     ENTER and BACKSPACE reach the game as before.
//   - The mouse turns the camera directly: its movement is added to the camera's angles
//     inside the game's own camera update, once per frame. No stick emulation, no
//     acceleration, the same feel at any FPS.
//
//  Safety: every game address used is checked byte for byte at start-up. If anything
//  does not match (another game version), the whole keyboard + mouse feature stays off
//  and nothing in the game is patched.
//
//  Proxy.cs calls only three functions: OnDirectInputCreated, PollWithController and
//  PollWithoutController. Nothing of the controller code is used or changed here.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PlayStationPDTEinputFix
{
    internal static unsafe partial class KeyboardMouse
    {
        // ---------------------------------------------------------------------------------
        // Game addresses (PTDE Steam exe, timestamp 0x557F2FD0), all verified in the exe
        // ---------------------------------------------------------------------------------
        private const uint EXE_TIMESTAMP = 0x557F2FD0;
        private const uint ADDR_MENUMAN = 0x013786D0;      // MenuMan; +0x7EC loading screen state (1..4 while shown)
        private const uint ADDR_CAMHOLDER = 0x0137D6C8;    // [this]+0x1024 = ChrCam
        private const uint ADDR_WORLDCHRMAN = 0x0137D644;  // [this]+0x3C = local PlayerIns
        private const uint ADDR_GAMEDATAMAN = 0x01378700;  // [this]+0x8 = player game data
        private const uint ADDR_ISSHOWMENU = 0x00C2ABE0;   // cdecl, no args, mask of open menus (0 = none)
        // Follow camera: the last step of its update, after the stick, the pitch pull-back and every
        // limit, right before the final yaw/pitch are turned into the camera rotation
        // (0x00F079F6 fld [ebx+0x144] ... 0x00F07A08 call). ebx = the camera.
        // Earlier tries: at the start of the update the game's angle rebuild overwrote the mouse;
        // after that rebuild the pitch was pulled back to +0x150 and the yaw fought the auto-follow.
        private const uint ADDR_FOLLOWCAM_FINAL = 0x00F079F6;   // fld dword ptr [ebx+0x144] (6 bytes)
        private const uint ADDR_AIMCAM_UPDATE = 0x00F18860;    // aim (bow sniper) camera update (cam, dt, ...)

        // ChrCam
        private const int CHRCAM_FOLLOW = 0x60, CHRCAM_AIM = 0x64, CHRCAM_MODE = 0x74;   // mode 0 normal, 1/2/3 aim
        // Follow camera: angles in radians, pitch limits in radians
        private const int FOLLOW_PITCH = 0x140, FOLLOW_YAW = 0x144, FOLLOW_PITCH_MAX = 0x1E4, FOLLOW_PITCH_MIN = 0x1E8;
        // What the stick sets while it turns the camera (0x00F0796A..0x00F0798C), so the camera does not
        // pull the pitch back (+0x150 target pitch) or start following behind the player on its own
        // (+0x130 manual control 1.0, +0x134 wait time = +0x1D8).
        private const int FOLLOW_TARGET_PITCH = 0x150, FOLLOW_CTRL_RATE = 0x130, FOLLOW_CTRL_WAIT = 0x134, FOLLOW_CTRL_WAIT_TIME = 0x1D8;
        private const int FOLLOW_RESET = 0x263, FOLLOW_EVENT_LOCK = 0x260;   // camera reset running / event camera lock
        // How fast the camera follows the character (ChrTransChaseRateForNormal) and its look-at point
        // (TargetChaseRateXZForNormal): the share of the remaining gap closed per 1/30 s (vanilla 0.1). The
        // current rates (+0x190 / +0x1A4) blend toward these every frame (0x00F0B53C). The game does not
        // rewrite them during play; DSfix's camera fix scales the current rates for the frame rate.
        private const int FOLLOW_CHR_CHASE = 0x194, FOLLOW_TARGET_CHASE = 0x1A8;
        // Aim camera: angles in radians, pitch limits in DEGREES
        private const int AIM_PITCH = 0x148, AIM_YAW = 0x14C, AIM_PITCH_MAX_DEG = 0xF0, AIM_PITCH_MIN_DEG = 0xF4;
        // Player game data: weapon hold style and equipped weapons
        private const int GD_STANCE = 0x230, GD_LEFT_SLOT = 0x234, GD_RIGHT_SLOT = 0x238, GD_LEFT_WEAPONS = 0x24C, GD_RIGHT_WEAPONS = 0x250;
        // PlayerIns: bit 0 of this byte is set while locked on (confirmed by the test log)
        private const int PI_LOCK = 0x128;

        // Groups: CORE (menus and camera state, needed by everything), FOLLOW (mouse camera in normal
        // view), AIM (mouse camera while aiming). A failed CORE check turns keyboard + mouse off; a failed
        // camera check only turns that camera's mouse control off.
        // Note: DSfix (CameraFix.cpp) patches 0x00F079D3..0x00F079D8 at start-up, before DDTM loads, so no
        // check may include those bytes. Checked against every address DSfix patches.
        private const int SIG_CORE = 0, SIG_FOLLOW = 1, SIG_AIM = 2;
        private static readonly (uint addr, byte[] bytes, string what, int group)[] _signatures =
        {
            (0x00F0796A, new byte[] { 0xD9, 0x83, 0x40, 0x01, 0x00, 0x00, 0xF3, 0x0F, 0x10, 0x05, 0x9C, 0xF5, 0x15, 0x01, 0xD9, 0x9B, 0x50, 0x01, 0x00, 0x00,
                                      0xF3, 0x0F, 0x11, 0x83, 0x30, 0x01, 0x00, 0x00, 0xD9, 0x83, 0xD8, 0x01, 0x00, 0x00, 0xD9, 0x9B, 0x34, 0x01, 0x00, 0x00 }, "follow camera manual control", SIG_FOLLOW),
            (0x00F079D9, new byte[] { 0xD8, 0x44, 0x24, 0x40, 0xD9, 0x5C, 0x24, 0x40, 0xD9, 0x44, 0x24, 0x40, 0xD9, 0x1C, 0x24,
                                      0xE8, 0x83, 0xAE, 0x4F, 0xFF, 0xD9, 0x9B, 0x40, 0x01, 0x00, 0x00, 0x83, 0xC4, 0x04, 0xD9, 0x83, 0x44, 0x01, 0x00, 0x00,
                                      0x51, 0x8D, 0x8C, 0x24, 0x04, 0x0E, 0x00, 0x00, 0xD9, 0x1C, 0x24, 0x51, 0xE8, 0x93, 0xB2, 0x50, 0xFF, 0xD9, 0x83, 0x40, 0x01, 0x00, 0x00 }, "follow camera final angles", SIG_FOLLOW),
            (0x00F18860, new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0x81, 0xEC, 0xF4, 0x01, 0x00, 0x00, 0x53, 0x8B, 0x5D, 0x08 }, "aim camera update", SIG_AIM),
            (0x00C2ABE0, new byte[] { 0x56, 0x8B, 0x35, 0xD0, 0x86, 0x37, 0x01, 0x57, 0x33, 0xFF, 0x39, 0x7E, 0x24, 0x74, 0x05, 0xBF }, "IsShowMenu", SIG_CORE),
            (0x00F0752D, new byte[] { 0xF3, 0x0F, 0x11, 0x83, 0x44, 0x01, 0x00, 0x00 }, "follow camera yaw write", SIG_FOLLOW),
            (0x00F07628, new byte[] { 0xD9, 0x9B, 0x40, 0x01, 0x00, 0x00 }, "follow camera pitch write", SIG_FOLLOW),
            (0x00F0283B, new byte[] { 0xF3, 0x0F, 0x10, 0x83, 0xE8, 0x01, 0x00, 0x00 }, "follow camera pitch limit", SIG_FOLLOW),
            (0x00F18D70, new byte[] { 0xD9, 0x9B, 0x48, 0x01, 0x00, 0x00, 0xF3, 0x0F, 0x11, 0x9B, 0x4C, 0x01, 0x00, 0x00 }, "aim camera angle write", SIG_AIM),
            (0x00EFC61E, new byte[] { 0x8B, 0x0D, 0xC8, 0xD6, 0x37, 0x01, 0x8B, 0x3D, 0xA0, 0x84, 0x37, 0x01, 0x89, 0x81, 0x24, 0x10, 0x00, 0x00 }, "camera holder", SIG_CORE),
            (0x00F148BD, new byte[] { 0x8B, 0x43, 0x74, 0x83, 0xF8, 0x04, 0x0F, 0x87, 0x2C, 0x19, 0x00, 0x00, 0xFF, 0x24, 0x85, 0xD4, 0x62, 0xF1, 0x00 }, "camera mode switch", SIG_CORE),
        };

        // ---------------------------------------------------------------------------------
        // Tuning
        // ---------------------------------------------------------------------------------
        private const float RAD_PER_COUNT = 0.0015f;   // camera turn per mouse count at sensitivity 1.0
        private const short WALK_STICK = 16384;        // left stick value for walking (full = 32767). 12000 was inside the game's deadzone
        private const long WHEEL_PULSE_MS = 80;        // one wheel notch = a press this long ...
        private const long WHEEL_GAP_MS = 40;          // ... then released this long
        private const long FLICK_MS = 100;             // lock-on target switch: right stick flick length
        private const long FLICK_GAP_MS = 120;         // released at least this long between two flicks
        private const int FLICK_COUNTS = 120;          // mouse counts within FLICK_WINDOW_MS that switch target
        private const long FLICK_WINDOW_MS = 80;
        private const long FLICK_COOLDOWN_MS = 350;
        // Same game timings as the controller macros (Proxy.cs), kept separately here.
        private const long RUN_STATE_MS = 800, JUMP_GUARD_MS = 500, JUMP_GAP_MS = 60, JUMP_TAP_MS = 120;
        private const long MACRO_RELEASE_MS = 40, MACRO_PRESS_MS = 110, SPRINT_ENGAGE_MS = 120, SPRINT_STOP_MS = 150;

        // ---------------------------------------------------------------------------------
        // Win32
        // ---------------------------------------------------------------------------------
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr name);
        [DllImport("kernel32.dll")] private static extern int ReadProcessMemory(IntPtr proc, IntPtr addr, void* buf, IntPtr size, IntPtr* read);
        [DllImport("kernel32.dll")] private static extern int VirtualProtect(IntPtr addr, IntPtr size, uint newProt, uint* oldProt);
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr addr, IntPtr size, uint type, uint prot);
        [DllImport("kernel32.dll")] private static extern int FlushInstructionCache(IntPtr proc, IntPtr addr, IntPtr size);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, uint* pid);

        // ---------------------------------------------------------------------------------
        // Log: written only with "DebugLog = TRUE" in DDTMControllerMappings.ini (the same switch as the
        // controller's log), to DDTMControllerFix_kbm_log.txt. Lines from before the ini is read are kept in
        // memory and written or dropped once it is read. Only start-up and setup messages, never per frame.
        // ---------------------------------------------------------------------------------
        private const string LOG_PATH = "DDTMControllerFix_kbm_log.txt";
        private static StreamWriter? _log;
        private static readonly object _logLock = new object();
        private static int _logState;   // 0 not decided yet, 1 on, 2 off
        private static List<string>? _logEarly = new List<string>();

        private static void Log(string s)
        {
            try
            {
                lock (_logLock)
                {
                    if (_logState == 2) return;
                    string line = $"[{NowMs() / 1000.0,9:F3}s] {s}";
                    if (_logState == 0) { if (_logEarly != null && _logEarly.Count < 100) _logEarly.Add(line); return; }
                    _log ??= new StreamWriter(LOG_PATH, false, Encoding.ASCII) { AutoFlush = true };
                    _log.WriteLine(line);
                }
            }
            catch { /* a log must never break input */ }
        }

        private static void SetLogEnabled(bool on)
        {
            lock (_logLock)
            {
                _logState = on ? 1 : 2;
                List<string>? early = _logEarly;
                _logEarly = null;
                if (!on || early == null) return;
                try
                {
                    _log ??= new StreamWriter(LOG_PATH, false, Encoding.ASCII) { AutoFlush = true };
                    foreach (string l in early) _log.WriteLine(l);
                }
                catch { }
            }
        }

        private static readonly long _t0 = Stopwatch.GetTimestamp();
        private static long NowMs() => (Stopwatch.GetTimestamp() - _t0) * 1000 / Stopwatch.Frequency;

        // ---------------------------------------------------------------------------------
        // Safe memory reads (game pointers can be stale during loading)
        // ---------------------------------------------------------------------------------
        private static bool Read(uint addr, void* buf, int size)
        {
            if (addr < 0x10000) return false;
            IntPtr got;
            return ReadProcessMemory(GetCurrentProcess(), (IntPtr)addr, buf, (IntPtr)size, &got) != 0 && got == (IntPtr)size;
        }
        private static uint RdU32(uint addr) { uint v = 0; return Read(addr, &v, 4) ? v : 0; }
        private static int RdI32(uint addr) { int v = 0; return Read(addr, &v, 4) ? v : 0; }
        private static byte RdU8(uint addr) { byte v = 0; return Read(addr, &v, 1) ? v : (byte)0; }
        private static float RdF(uint addr) { float v = 0; return Read(addr, &v, 4) ? v : 0f; }

        // =================================================================================
        // Start-up
        // =================================================================================
        private static bool _enabled;      // all signatures matched, hooks installed
        private static bool _initDone;
        private static bool _followHooked, _aimHooked;

        private static void Init()
        {
            if (_initDone) return;
            _initDone = true;

            uint exeBase = (uint)GetModuleHandleW(IntPtr.Zero);
            uint stamp = RdU32(exeBase + RdU32(exeBase + 0x3C) + 8);
            if (exeBase != 0x400000 || stamp != EXE_TIMESTAMP)
            {
                Log($"exe base 0x{exeBase:X8} timestamp 0x{stamp:X8}: not the PTDE Steam exe, keyboard + mouse OFF");
                return;
            }
            bool[] groupOk = { true, true, true };
            foreach (var (addr, bytes, what, group) in _signatures)
            {
                byte[] got = new byte[bytes.Length];
                fixed (byte* p = got)
                    if (!Read(addr, p, got.Length) || !got.AsSpan().SequenceEqual(bytes))
                    {
                        groupOk[group] = false;
                        Log($"signature mismatch at 0x{addr:X8} ({what}): got {Convert.ToHexString(got)}");
                    }
            }
            if (!groupOk[SIG_CORE]) { Log("keyboard + mouse OFF (menu/camera state not recognised), nothing patched"); return; }

            // follow camera: argument = ebx, replays "fld dword ptr [ebx+0x144]" (so the new yaw is used)
            if (!groupOk[SIG_FOLLOW]) Log("mouse camera in normal view OFF (code not recognised), keyboard still works");
            else if (InstallHook(ADDR_FOLLOWCAM_FINAL, new byte[] { 0xD9, 0x83, 0x44, 0x01, 0x00, 0x00 }, false,
                                 (IntPtr)(delegate* unmanaged[Stdcall]<byte*, void>)&OnFollowCamUpdate, "follow camera"))
                _followHooked = true;
            // aim camera: at the start of its update, argument = the first stack argument
            if (!groupOk[SIG_AIM]) Log("mouse camera while aiming OFF (code not recognised), keyboard still works");
            else if (InstallHook(ADDR_AIMCAM_UPDATE, new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0 }, true,
                                 (IntPtr)(delegate* unmanaged[Stdcall]<byte*, void>)&OnAimCamUpdate, "aim camera"))
                _aimHooked = true;

            _enabled = true;
            Log($"keyboard + mouse ON (mouse camera: normal view {(_followHooked ? "on" : "OFF")}, aiming {(_aimHooked ? "on" : "OFF")})");
        }

        // Camera hook: the instruction(s) at 'target' (exactly 'orig', verified by the signatures) are
        // replaced by a jump to a small stub that saves everything, calls ours, restores everything,
        // runs the replaced instruction(s) and jumps back:
        //    pushad ; save xmm0-7 ; fnsave ; push <camera> ; call ours ; frstor ; restore xmm0-7 ; popad
        //    <orig> ; jmp target+orig.Length
        // <camera> is ebx (fromStack = false) or the function's first stack argument (fromStack = true).
        private static bool InstallHook(uint target, byte[] orig, bool fromStack, IntPtr fn, string what)
        {
            byte* stub = (byte*)VirtualAlloc(IntPtr.Zero, (IntPtr)256, 0x3000, 0x40);
            if (stub == null) { Log($"{what}: could not allocate the hook stub, keyboard + mouse OFF"); return false; }
            int i = 0;
            void B(params byte[] bytes) { foreach (byte x in bytes) stub[i++] = x; }
            void D(uint v) { *(uint*)(stub + i) = v; i += 4; }

            B(0x60);                                            // pushad                 (0x20 bytes)
            B(0x81, 0xEC); D(0x80);                             // sub esp, 80h
            for (int r = 0; r < 8; r++) B(0xF3, 0x0F, 0x7F, (byte)(0x44 + r * 8), 0x24, (byte)(r * 16));   // movdqu [esp+r*16], xmmR
            B(0x81, 0xEC); D(0x6C);                             // sub esp, 6Ch
            B(0xDD, 0x34, 0x24);                                // fnsave [esp]
            if (fromStack) { B(0xFF, 0xB4, 0x24); D(0x6C + 0x80 + 0x20 + 4); }   // push dword [esp+110h] (first argument)
            else B(0x53);                                       // push ebx
            B(0xFF, 0x15); D((uint)(stub + 240));               // call [stub+240]
            B(0xDD, 0x24, 0x24);                                // frstor [esp]
            B(0x81, 0xC4); D(0x6C);                             // add esp, 6Ch
            for (int r = 0; r < 8; r++) B(0xF3, 0x0F, 0x6F, (byte)(0x44 + r * 8), 0x24, (byte)(r * 16));   // movdqu xmmR, [esp+r*16]
            B(0x81, 0xC4); D(0x80);                             // add esp, 80h
            B(0x61);                                            // popad
            B(orig);                                            // the replaced instruction(s)
            B(0xE9); D((uint)((int)(target + (uint)orig.Length) - (int)(stub + i + 4)));   // jmp back
            *(IntPtr*)(stub + 240) = fn;

            uint old;
            if (VirtualProtect((IntPtr)target, (IntPtr)orig.Length, 0x40, &old) == 0) { Log($"{what}: VirtualProtect failed, keyboard + mouse OFF"); return false; }
            byte* t = (byte*)target;
            t[0] = 0xE9; *(int*)(t + 1) = (int)stub - (int)(target + 5);
            for (int k = 5; k < orig.Length; k++) t[k] = 0x90;
            VirtualProtect((IntPtr)target, (IntPtr)orig.Length, old, &old);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)target, (IntPtr)orig.Length);
            Log($"{what}: hook installed at 0x{target:X8}");
            return true;
        }

        // =================================================================================
        // DirectInput: the game's keyboard and mouse devices are filtered, and we read the
        // mouse with a device of our own (raw counts, no Windows acceleration)
        // =================================================================================
        private static readonly Guid GUID_SysMouse = new Guid("6F1D2B60-D5A0-11CF-BFC7-444553540000");
        private static readonly Guid GUID_SysKeyboard = new Guid("6F1D2B61-D5A0-11CF-BFC7-444553540000");

        private static IntPtr _di, _origCreateDevice, _origGetState, _origSetFormat, _origSetCoop, _origAcquire, _devVtable;
        private static readonly HashSet<IntPtr> _gameKeyboards = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _gameMice = new HashSet<IntPtr>();
        private static IntPtr _mouseFormat;   // the game's own mouse data format (DIMOUSESTATE2)
        private static IntPtr _hwnd;
        private static IntPtr _ownMouse;
        private static bool _ownMouseFailed;

        private static IntPtr PatchSlot(IntPtr vtable, int index, IntPtr fn)
        {
            IntPtr slot = vtable + index * 4;
            uint old;
            VirtualProtect(slot, (IntPtr)4, 0x40, &old);
            IntPtr orig = *(IntPtr*)slot;
            *(IntPtr*)slot = fn;
            VirtualProtect(slot, (IntPtr)4, old, &old);
            return orig;
        }

        /// <summary>Called from Proxy.DirectInput8Create after the real DirectInput8Create.</summary>
        public static void OnDirectInputCreated(int hr, IntPtr ppvOut)
        {
            try
            {
                Init();
                if (!_enabled || hr != 0 || ppvOut == IntPtr.Zero || _di != IntPtr.Zero) return;
                _di = *(IntPtr*)ppvOut;
                _origCreateDevice = PatchSlot(*(IntPtr*)_di, 3, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, IntPtr, int>)&HookCreateDevice);
            }
            catch (Exception e) { Log("OnDirectInputCreated error: " + e.Message); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int HookCreateDevice(IntPtr self, Guid* guid, IntPtr* ppDev, IntPtr outer)
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, IntPtr, int>)_origCreateDevice)(self, guid, ppDev, outer);
            try
            {
                if (hr != 0 || ppDev == null || *ppDev == IntPtr.Zero || guid == null) return hr;
                IntPtr dev = *ppDev;
                bool kb = *guid == GUID_SysKeyboard, mouse = *guid == GUID_SysMouse;
                if (!kb && !mouse) return hr;

                // A released device's address can be reused, so it is always re-sorted.
                _gameKeyboards.Remove(dev); _gameMice.Remove(dev);
                if (kb) _gameKeyboards.Add(dev); else _gameMice.Add(dev);

                if (_devVtable == IntPtr.Zero)
                {
                    _devVtable = *(IntPtr*)dev;
                    _origAcquire = *(IntPtr*)(_devVtable + 7 * 4);
                    _origGetState = PatchSlot(_devVtable, 9, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int>)&HookGetDeviceState);
                    _origSetFormat = PatchSlot(_devVtable, 11, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&HookSetDataFormat);
                    _origSetCoop = PatchSlot(_devVtable, 13, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)&HookSetCooperativeLevel);
                }
                else if (*(IntPtr*)dev != _devVtable) Log($"warning: device 0x{(uint)dev:X8} has another vtable, it is not filtered");
            }
            catch (Exception e) { Log("CreateDevice hook error: " + e.Message); }
            return hr;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int HookSetDataFormat(IntPtr self, IntPtr fmt)
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_origSetFormat)(self, fmt);
            if (hr == 0 && _mouseFormat == IntPtr.Zero && _gameMice.Contains(self)) _mouseFormat = fmt;
            return hr;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int HookSetCooperativeLevel(IntPtr self, IntPtr hwnd, uint flags)
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)_origSetCoop)(self, hwnd, flags);
            if (hwnd != IntPtr.Zero && hwnd != _hwnd && (_gameKeyboards.Contains(self) || _gameMice.Contains(self)))
                _hwnd = hwnd;
            return hr;
        }

        // The game's keyboard and mouse, filtered by phase.
        private static readonly byte[] _keepMenuKeys = { 0xC8, 0xD0, 0xCB, 0xCD, 0x1C, 0x9C, 0x0E };   // arrows, ENTER, numpad ENTER, BACKSPACE
        private static readonly byte[] _keepAimKeys = { 0xC8, 0xD0 };                                   // arrow up / down = zoom
        private const int DIK_UP = 0xC8, DIK_DOWN = 0xD0;
        // A key that was held while the game's keys were hidden (in game) reaches the game only after it was let go:
        // otherwise, when a menu or dialog opens while it is still held, the game sees a new press. (ENTER on "Offer
        // Humanity": between the talk list and the "Offer humanity?" dialog there is a moment with no menu, and the
        // still-held ENTER answered the dialog at once.)
        private static readonly bool[] _keyWaitUp = new bool[256];

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int HookGetDeviceState(IntPtr self, uint cb, byte* data)
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int>)_origGetState)(self, cb, data);
            if (hr != 0 || data == null || _phase == Phase.Unknown) return hr;
            try
            {
                if (cb >= 256 && _gameKeyboards.Contains(self))
                {
                    byte[] keep = (_phase == Phase.Menu || _phase == Phase.Loading) ? _keepMenuKeys
                                : _phase == Phase.Aim ? _keepAimKeys : Array.Empty<byte>();
                    byte* saved = stackalloc byte[8];
                    for (int i = 0; i < keep.Length; i++)
                    {
                        int k = keep[i];
                        if ((data[k] & 0x80) == 0) _keyWaitUp[k] = false;     // let go: it may reach the game again
                        saved[i] = _keyWaitUp[k] ? (byte)0 : data[k];
                    }
                    // the menu keys that are hidden now and held: they wait for their release
                    if (keep.Length != _keepMenuKeys.Length)
                        for (int i = 0; i < _keepMenuKeys.Length; i++)
                        {
                            int k = _keepMenuKeys[i];
                            if ((data[k] & 0x80) != 0 && Array.IndexOf(keep, (byte)k) < 0) _keyWaitUp[k] = true;
                        }
                    new Span<byte>(data, 256).Clear();
                    for (int i = 0; i < keep.Length; i++) data[keep[i]] = saved[i];
                    if (_phase == Phase.Aim)
                    {
                        if (_zoomIn.Active) data[DIK_UP] = 0x80;
                        if (_zoomOut.Active) data[DIK_DOWN] = 0x80;
                    }
                }
                else if (cb >= 16 && _gameMice.Contains(self))
                {
                    // In menus the game gets the clicks and the wheel, but no movement: the menu cursor
                    // comes from the Windows cursor, and the movement would turn the camera behind the menu.
                    new Span<byte>(data, _phase == Phase.Menu ? 8 : (int)cb).Clear();
                }
            }
            catch { }
            return hr;
        }

        // Our own mouse device: reads movement, wheel and buttons every poll.
        private static readonly byte[] _mouseState = new byte[20];

        private static bool ReadOwnMouse()
        {
            if (_ownMouseFailed) return false;
            if (_ownMouse == IntPtr.Zero)
            {
                if (_di == IntPtr.Zero || _mouseFormat == IntPtr.Zero || _hwnd == IntPtr.Zero || _devVtable == IntPtr.Zero) return false;
                Guid g = GUID_SysMouse;
                IntPtr dev;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, IntPtr, int>)_origCreateDevice)(_di, &g, &dev, IntPtr.Zero);
                if (hr != 0 || dev == IntPtr.Zero) { _ownMouseFailed = true; Log($"own mouse: CreateDevice failed hr=0x{hr:X8}, mouse OFF"); return false; }
                _gameMice.Remove(dev); _gameKeyboards.Remove(dev);
                IntPtr vt = *(IntPtr*)dev;
                int hr2 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_origSetFormat)(dev, _mouseFormat);
                int hr3 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)_origSetCoop)(dev, _hwnd, 0x0A); // NONEXCLUSIVE | BACKGROUND
                Log($"own mouse device 0x{(uint)dev:X8} format hr=0x{hr2:X8} coop hr=0x{hr3:X8}{(vt != _devVtable ? " (other vtable!)" : "")}");
                if (hr2 != 0 || hr3 != 0) { _ownMouseFailed = true; return false; }
                _ownMouse = dev;
            }
            int r;
            fixed (byte* p = _mouseState)
            {
                r = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int>)_origGetState)(_ownMouse, 20, p);
                if (r != 0)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)_origAcquire)(_ownMouse);
                    r = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int>)_origGetState)(_ownMouse, 20, p);
                }
            }
            if (r != 0) { Array.Clear(_mouseState); return false; }
            return true;
        }

        // =================================================================================
        // Phase: what the game is doing right now (decided once per poll, game thread)
        // =================================================================================
        private enum Phase { Unknown, Menu, Loading, Gameplay, BowReady, Aim }
        private static Phase _phase = Phase.Unknown;
        private static bool _locked;
        private static uint _menuMask, _player;
        private const uint MENU_QUICK = 0x40;   // IsShowMenu bit of the quick (START) menu, also set while its sub-menus are open
        private static long _nextEquipRead;
        private static int _stance, _leftWeapon = -1, _rightWeapon = -1;
        // The game's own rule: its "can the player act" checks test the open-menu mask with 0x7FFFF77F, so only
        // the hint messages (0x800, e.g. the tutorial texts, MenuMan+0x98) and the free message state (0x80,
        // MenuMan+0x40 = 4: "Used Dungeon Cell Key", parts of NPC dialogue) let the player move. Those count as
        // "in game" here too. Everything else is a menu: the menu keys work and the game's own keys (arrows, ENTER,
        // BACKSPACE) reach it. The movement keys move the hero in every menu too (the game allows it, as with the
        // controller's left stick).
        // A message box alone (bit 0x8 with dialog type 9 or 0 at MenuMan+0x60, the types the game itself treats as
        // plain messages: "It's locked / OK", "Used Dungeon Cell Key") is a menu for the keys, but without the cursor.
        // The other dialogs (the number picker "Offer humanity?", the YES/NO questions) and a dialog over a menu (e.g.
        // the bonfire menu) keep the cursor.
        private const uint MENU_BLOCKING = 0x7FFFF77F, MENU_DIALOG = 0x8;
        private static bool _dialogAlone;
        // NPC dialogue (subtitles) in talk state 1 (bit 0x80000, MenuMan+0x40 = 1): a menu for the keys, but nothing
        // can be clicked, so the cursor hides.
        private const uint MENU_TALK_1 = 0x80000;
        private static bool _npcTalk;

        private static bool IsBowOrCrossbow(int id) => id >= 1200000 && id < 1300000;

        private static void UpdatePhase()
        {
            uint menuMan = RdU32(ADDR_MENUMAN);
            uint player = RdU32(RdU32(ADDR_WORLDCHRMAN) + 0x3C);
            uint chrCam = RdU32(RdU32(ADDR_CAMHOLDER) + 0x1024);
            uint gameData = RdU32(RdU32(ADDR_GAMEDATAMAN) + 0x8);
            int loading = menuMan != 0 ? RdI32(menuMan + 0x7EC) : 0;
            uint menuMask = menuMan != 0 ? ((delegate* unmanaged[Cdecl]<uint>)ADDR_ISSHOWMENU)() : 0;
            int camMode = chrCam != 0 ? RdI32(chrCam + CHRCAM_MODE) : 0;
            // Weapons and how they are held change rarely: read in one go, 10 times per second.
            if (_now >= _nextEquipRead)
            {
                _nextEquipRead = _now + 100;
                _stance = 0; _leftWeapon = -1; _rightWeapon = -1;
                int* eq = stackalloc int[11];   // +0x230 .. +0x25B
                if (gameData != 0 && Read(gameData + GD_STANCE, eq, 44))
                {
                    _stance = eq[0];
                    int ls = eq[(GD_LEFT_SLOT - GD_STANCE) / 4], rs = eq[(GD_RIGHT_SLOT - GD_STANCE) / 4];
                    if (ls >= 0 && ls <= 1) _leftWeapon = eq[(GD_LEFT_WEAPONS - GD_STANCE) / 4 + 2 * ls];
                    if (rs >= 0 && rs <= 1) _rightWeapon = eq[(GD_RIGHT_WEAPONS - GD_STANCE) / 4 + 2 * rs];
                }
            }
            int stance = _stance, left = _leftWeapon, right = _rightWeapon;
            bool lockOn = player != 0 && (RdU8(player + PI_LOCK) & 1) != 0;

            uint blocking = menuMask & MENU_BLOCKING;
            int dialogType = blocking == MENU_DIALOG && menuMan != 0 ? RdI32(menuMan + 0x60) : -1;
            _dialogAlone = dialogType == 9 || dialogType == 0;

            Phase p;
            if (loading >= 1 && loading <= 4) p = Phase.Loading;
            else if (player == 0 || blocking != 0) p = Phase.Menu;
            else if (camMode >= 1 && camMode <= 3) p = Phase.Aim;
            else if (stance != 1 && (IsBowOrCrossbow(left) || IsBowOrCrossbow(right))) p = Phase.BowReady;
            else p = Phase.Gameplay;

            // NPC dialogue only (no other menu open): no cursor
            bool talk = blocking == MENU_TALK_1 && menuMan != 0 && RdI32(menuMan + 0x40) == 1;
            _npcTalk = talk;

            _phase = p;
            _menuMask = menuMask;
            _player = player;
            _locked = (p == Phase.Gameplay || p == Phase.BowReady) && lockOn;
        }

        // =================================================================================
        // Per poll (once per frame, game thread)
        // =================================================================================
        private static bool _configLoaded;
        private static int[] _polledKeys = Array.Empty<int>();
        private static readonly bool[] _down = new bool[IN_COUNT];
        private static readonly bool[] _act = new bool[(int)Act.Count];
        private static readonly bool[] _actPrev = new bool[(int)Act.Count];
        private static long _now;
        private static bool _focused;
        private static int _mouseDX, _mouseDY;
        private static int _wheelAcc;

        // A queue of short presses (wheel notches, target switches, zoom steps).
        private struct Pulser
        {
            public int Queued;
            public long Until, NextAllowed;
            public bool Active;
            public void Add(int n) { Queued = Math.Min(Queued + n, 6); }
            public void Clear() { Queued = 0; Active = false; }
            public void Tick(long now, long press, long gap)
            {
                if (Active && now >= Until) { Active = false; NextAllowed = now + gap; }
                if (!Active && Queued > 0 && now >= NextAllowed) { Queued--; Active = true; Until = now + press; }
            }
        }
        private static Pulser _wheelUp, _wheelDown, _zoomIn, _zoomOut, _flickLeft, _flickRight;

        private static Proxy.XINPUT_GAMEPAD _kbPad;
        private static uint _ownPacket;
        private static Proxy.XINPUT_GAMEPAD _lastOwnPad;

        /// <summary>Proxy: no controller connected. Gives the game the keyboard pad instead of "not connected".</summary>
        public static uint PollWithoutController(IntPtr pState)
        {
            if (!_enabled) return 1167;   // feature off: exactly as before (ERROR_DEVICE_NOT_CONNECTED)
            Proxy.XINPUT_GAMEPAD pad = default;
            Poll(ref pad, false);
            if (!PadEquals(pad, _lastOwnPad)) { _ownPacket++; _lastOwnPad = pad; }
            Proxy.XINPUT_STATE* st = (Proxy.XINPUT_STATE*)pState;
            st->dwPacketNumber = _ownPacket;
            st->Gamepad = pad;
            return 0;
        }

        /// <summary>Proxy: the controller state is final. Mixes the keyboard pad into it.</summary>
        public static void PollWithController(ref Proxy.XINPUT_GAMEPAD pad) => Poll(ref pad, true);

        private static bool PadEquals(in Proxy.XINPUT_GAMEPAD a, in Proxy.XINPUT_GAMEPAD b) =>
            a.wButtons == b.wButtons && a.bLeftTrigger == b.bLeftTrigger && a.bRightTrigger == b.bRightTrigger &&
            a.sThumbLX == b.sThumbLX && a.sThumbLY == b.sThumbLY && a.sThumbRX == b.sThumbRX && a.sThumbRY == b.sThumbRY;

        private static void Poll(ref Proxy.XINPUT_GAMEPAD pad, bool hasController)
        {
            try
            {
                if (!_enabled) return;
                if (!_configLoaded)
                {
                    _configLoaded = true;
                    LoadKeyboardConfig();
                    FillSharedCells();   // the prompt keys for DSfix (KbmShared.cs)
                    var keys = new HashSet<int> { 0x1B };   // ESC (goes back in menus)
                    foreach (var bd in _bindings)
                    {
                        if (bd.A < 256) keys.Add(bd.A);
                        if (bd.B >= 0 && bd.B < 256) keys.Add(bd.B);
                    }
                    _polledKeys = new int[keys.Count];
                    keys.CopyTo(_polledKeys);
                }
                _now = NowMs();

                UpdatePhase();
                bool controllerUsed = hasController && ControllerActive(pad);
                _ctrlCamActive = hasController && (Math.Abs((int)pad.sThumbRX) > 3000 || Math.Abs((int)pad.sThumbRY) > 3000);

                // ---- read keyboard and mouse (only while the game window is in front) ----
                IntPtr fg = GetForegroundWindow();
                uint pid = 0;
                if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, &pid);
                _focused = pid == GetCurrentProcessId();

                bool mouseOk = ReadOwnMouse();
                Array.Clear(_down);
                _mouseDX = _mouseDY = 0;
                bool kbmUsed = false;
                if (_focused)
                {
                    // Only the keys that are bound (plus ESC) are read: a few calls per frame.
                    foreach (int vk in _polledKeys)
                        if ((GetAsyncKeyState(vk) & 0x8000) != 0) { _down[vk] = true; kbmUsed = true; }
                    if (mouseOk)
                    {
                        _mouseDX = BitConverter.ToInt32(_mouseState, 0);
                        _mouseDY = BitConverter.ToInt32(_mouseState, 4);
                        _wheelAcc += BitConverter.ToInt32(_mouseState, 8);
                        for (int b = 0; b < 5; b++) if ((_mouseState[12 + b] & 0x80) != 0) _down[IN_MOUSE_LEFT + b] = true;
                    }
                    else
                    {
                        // fallback: mouse buttons from Windows (no movement, no wheel)
                        for (int b = 0; b < 5; b++)
                            if ((GetAsyncKeyState(b < 2 ? b + 1 : b + 2) & 0x8000) != 0) _down[IN_MOUSE_LEFT + b] = true;
                    }
                }
                else _wheelAcc = 0;

                bool mouseMoved = Math.Abs(_mouseDX) + Math.Abs(_mouseDY) > 2;
                for (int b = IN_MOUSE_LEFT; b <= IN_MOUSE_5; b++) if (_down[b]) kbmUsed = true;

                // ---- wheel notches ----
                while (_wheelAcc >= 120) { _wheelAcc -= 120; OnWheel(+1); kbmUsed = true; }
                while (_wheelAcc <= -120) { _wheelAcc += 120; OnWheel(-1); kbmUsed = true; }
                _wheelUp.Tick(_now, WHEEL_PULSE_MS, WHEEL_GAP_MS);
                _wheelDown.Tick(_now, WHEEL_PULSE_MS, WHEEL_GAP_MS);
                _zoomIn.Tick(_now, WHEEL_PULSE_MS, WHEEL_GAP_MS);
                _zoomOut.Tick(_now, WHEEL_PULSE_MS, WHEEL_GAP_MS);
                _down[IN_WHEEL_UP] = _wheelUp.Active;
                _down[IN_WHEEL_DOWN] = _wheelDown.Active;

                // ---- actions ----
                bool menu = _phase == Phase.Menu || _phase == Phase.Loading;
                bool aiming = _phase == Phase.Aim;
                bool forcedMouse = aiming || _phase == Phase.BowReady;
                bool menuTabs = _phase == Phase.Menu && (_menuMask & MENU_QUICK) != 0;
                EvaluateActions(_down, _act, menu, forcedMouse, aiming, menuTabs);

                BuildKeyboardPad(menu, forcedMouse);
                CameraFromMouse(menu);

                UpdateCursor(menu && !_npcTalk && !_dialogAlone, _phase == Phase.Loading, mouseMoved || _down[IN_MOUSE_LEFT] || _down[IN_MOUSE_RIGHT], kbmUsed || mouseMoved, controllerUsed);
                UpdateShared(hasController, controllerUsed, kbmUsed || mouseMoved, menu, menuTabs);   // for DSfix (KbmShared.cs)

                Array.Copy(_act, _actPrev, _act.Length);

                // ---- mix into the controller state ----
                pad.wButtons |= _kbPad.wButtons;
                pad.bLeftTrigger = Math.Max(pad.bLeftTrigger, _kbPad.bLeftTrigger);
                pad.bRightTrigger = Math.Max(pad.bRightTrigger, _kbPad.bRightTrigger);
                if (_kbPad.sThumbLX != 0 || _kbPad.sThumbLY != 0) { pad.sThumbLX = _kbPad.sThumbLX; pad.sThumbLY = _kbPad.sThumbLY; }
                if (_kbPad.sThumbRX != 0 || _kbPad.sThumbRY != 0) { pad.sThumbRX = _kbPad.sThumbRX; pad.sThumbRY = _kbPad.sThumbRY; }

            }
            catch (Exception e) { Log("poll error: " + e); }
        }

        private static void OnWheel(int dir)
        {
            if (_phase == Phase.Aim) { if (dir > 0) _zoomIn.Add(1); else _zoomOut.Add(1); }
            else if (dir > 0) _wheelUp.Add(1); else _wheelDown.Add(1);
        }

        private static Proxy.XINPUT_GAMEPAD _ctrlPrev;
        private static bool ControllerActive(in Proxy.XINPUT_GAMEPAD p)
        {
            bool active = p.wButtons != _ctrlPrev.wButtons || Math.Abs(p.bLeftTrigger - _ctrlPrev.bLeftTrigger) > 30
                       || Math.Abs(p.bRightTrigger - _ctrlPrev.bRightTrigger) > 30
                       || Math.Abs((int)p.sThumbLX) > 8000 || Math.Abs((int)p.sThumbLY) > 8000
                       || Math.Abs((int)p.sThumbRX) > 8000 || Math.Abs((int)p.sThumbRY) > 8000;
            _ctrlPrev = p;
            return active;
        }

        // =================================================================================
        // The keyboard pad
        // =================================================================================
        private const ushort MASK_DPAD_UP = 0x0001, MASK_DPAD_DOWN = 0x0002, MASK_DPAD_LEFT = 0x0004, MASK_DPAD_RIGHT = 0x0008;
        private const ushort MASK_START = 0x0010, MASK_BACK = 0x0020, MASK_RS = 0x0080, MASK_LB = 0x0100, MASK_RB = 0x0200;
        private const ushort MASK_A = 0x1000, MASK_B = 0x2000, MASK_X = 0x4000, MASK_Y = 0x8000;

        // Dash / jump state (the keyboard's own copy of the controller's mute-jump and JUMP_ONLY logic)
        private static bool _bHeldLast, _guardArmed, _bHoldWasMoving, _jumpActive, _jumpNeedsRamp;
        private static long _bHoldStart, _sprintEnd, _jumpStart;
        private static long _flickWindowStart, _flickCooldownUntil;
        private static bool _kickActive, _leapActive, _sprintOn, _sprintEngaged, _sprintHasRun, _sprintLiveTiming, _sprintNeutralTiming;
        private static long _kickStart, _leapStart, _sprintLiveSince, _sprintNeutralSince;
        private static int _flickAcc;

        private static bool Act_(Act a) => _act[(int)a];
        private static bool Pressed(Act a) => _act[(int)a] && !_actPrev[(int)a];

        // The movement keys as the left stick (WALK = half way). Returns true while moving.
        private static bool MoveStick(ref Proxy.XINPUT_GAMEPAD k)
        {
            int mx = (Act_(Act.MoveRight) ? 1 : 0) - (Act_(Act.MoveLeft) ? 1 : 0);
            int my = (Act_(Act.MoveForward) ? 1 : 0) - (Act_(Act.MoveBackward) ? 1 : 0);
            if (mx == 0 && my == 0) return false;
            double mag = Act_(Act.Walk) ? WALK_STICK : 32767.0;
            double len = Math.Sqrt(mx * mx + my * my);
            k.sThumbLX = (short)Math.Round(mx / len * mag);
            k.sThumbLY = (short)Math.Round(my / len * mag);
            return true;
        }

        private static void BuildKeyboardPad(bool menu, bool forcedMouse)
        {
            Proxy.XINPUT_GAMEPAD k = default;
            if (menu)
            {
                // Menus: only A / B / X / Y / START / BACK (and MENU_LEFT / MENU_RIGHT).
                // ESC works like START, which closes the whole menu at once. On the title screen (no
                // character loaded) it goes back (B) instead, because START there would start the game.
                bool title = _player == 0;
                bool escB = title && _down[0x1B];
                if (!title && _down[0x1B]) k.wButtons |= MASK_START;

                if (Act_(Act.Interact)) k.wButtons |= MASK_A;
                if (Act_(Act.Dash) || escB) k.wButtons |= MASK_B;
                if (Act_(Act.MenuLeft)) k.wButtons |= MASK_LB;
                if (Act_(Act.MenuRight)) k.wButtons |= MASK_RB;
                if (Act_(Act.UseItem)) k.wButtons |= MASK_X;
                if (Act_(Act.TwoHand)) k.wButtons |= MASK_Y;
                if (Act_(Act.Menu)) k.wButtons |= MASK_START;
                if (Act_(Act.Gestures)) k.wButtons |= MASK_BACK;
                // the hero can walk while a menu is open (not on the title screen, where there is no hero)
                if (!title) MoveStick(ref k);
                _bHeldLast = false; _guardArmed = false; _jumpActive = false;
                _sprintOn = false; _sprintEngaged = false; _kickActive = false; _leapActive = false;
                _flickLeft.Clear(); _flickRight.Clear();
                _kbPad = k;
                return;
            }


            // ---- movement ----
            bool moving = MoveStick(ref k);

            // ---- plain buttons ----
            if (Act_(Act.SwitchLeftWeapon)) k.wButtons |= MASK_DPAD_LEFT;
            if (Act_(Act.SwitchRightWeapon)) k.wButtons |= MASK_DPAD_RIGHT;
            if (Act_(Act.SwitchItem)) k.wButtons |= MASK_DPAD_DOWN;
            if (Act_(Act.SwitchMagic)) k.wButtons |= MASK_DPAD_UP;
            if (Act_(Act.LightAttack)) k.wButtons |= MASK_RB;
            if (Act_(Act.HeavyAttack)) k.bRightTrigger = 255;
            if (Act_(Act.Block)) k.wButtons |= MASK_LB;
            if (Act_(Act.Parry)) k.bLeftTrigger = 255;
            if (Act_(Act.Interact)) k.wButtons |= MASK_A;
            if (Act_(Act.UseItem)) k.wButtons |= MASK_X;
            if (Act_(Act.TwoHand)) k.wButtons |= MASK_Y;
            if (Act_(Act.Menu)) k.wButtons |= MASK_START;
            if (Act_(Act.Gestures)) k.wButtons |= MASK_BACK;
            if (Act_(Act.LockOn)) k.wButtons |= MASK_RS;

            // Bow two-handed or aiming: left click shoots, right click changes the arrow type.
            if (forcedMouse)
            {
                if (_down[IN_MOUSE_LEFT]) k.wButtons |= MASK_RB;
                if (_down[IN_MOUSE_RIGHT]) k.bRightTrigger = 255;
            }

            // ---- lock-on target switching: wheel, keys or a fast mouse flick ----
            if (_locked)
            {
                if (Pressed(Act.NextTarget)) _flickRight.Add(1);
                if (Pressed(Act.PreviousTarget)) _flickLeft.Add(1);

                if (_now - _flickWindowStart > FLICK_WINDOW_MS) { _flickWindowStart = _now; _flickAcc = 0; }
                _flickAcc += _invertX ? -_mouseDX : _mouseDX;
                if (Math.Abs(_flickAcc) >= FLICK_COUNTS && _now >= _flickCooldownUntil)
                {
                    if (_flickAcc > 0) _flickRight.Add(1); else _flickLeft.Add(1);
                    _flickAcc = 0;
                    _flickCooldownUntil = _now + FLICK_COOLDOWN_MS;
                }
            }
            else { _flickLeft.Clear(); _flickRight.Clear(); _flickAcc = 0; }
            _flickLeft.Tick(_now, FLICK_MS, FLICK_GAP_MS);
            _flickRight.Tick(_now, FLICK_MS, FLICK_GAP_MS);
            if (_flickRight.Active) k.sThumbRX = 32767;
            else if (_flickLeft.Active) k.sThumbRX = -32767;

            // ---- kick / leaping attack (same as the controller's KICK and LEAP_ATTACK macros) ----
            // Stick to neutral with the attack released, then stick fully forward with the attack.
            if (Pressed(Act.Kick)) { _kickActive = true; _kickStart = _now; }
            if (_kickActive)
            {
                long e = _now - _kickStart;
                if (e < MACRO_RELEASE_MS) { k.sThumbLX = 0; k.sThumbLY = 0; k.wButtons &= unchecked((ushort)~MASK_RB); }
                else if (e < MACRO_RELEASE_MS + MACRO_PRESS_MS) { k.sThumbLX = 0; k.sThumbLY = 32767; k.wButtons |= MASK_RB; }
                else _kickActive = false;
            }
            if (Pressed(Act.LeapAttack)) { _leapActive = true; _leapStart = _now; }
            if (_leapActive)
            {
                long e = _now - _leapStart;
                if (e < MACRO_RELEASE_MS) { k.sThumbLX = 0; k.sThumbLY = 0; k.bRightTrigger = 0; }
                else if (e < MACRO_RELEASE_MS + MACRO_PRESS_MS) { k.sThumbLX = 0; k.sThumbLY = 32767; k.bRightTrigger = 255; }
                else _leapActive = false;
            }

            // ---- DASH_SPRINT_ONLY: press once, sprint while moving ----
            // Like the controller's AUTO_RUN with AutoRun_CancelOnStop = TRUE: it switches off when you
            // stop moving, attack, or press a button that opens something. Walking pauses it.
            bool wantSprint = false;
            if (Pressed(Act.DashSprintOnly))
            {
                _sprintOn = !_sprintOn;
                _sprintEngaged = false; _sprintHasRun = false; _sprintLiveTiming = false; _sprintNeutralTiming = false;
            }
            bool attacking = (k.wButtons & (MASK_LB | MASK_RB)) != 0 || k.bRightTrigger > 0 || k.bLeftTrigger > 0;
            bool opening = (k.wButtons & (MASK_A | MASK_START | MASK_BACK)) != 0;
            if (_sprintOn && (attacking || opening)) { _sprintOn = false; _sprintEngaged = false; }
            if (_sprintOn)
            {
                bool live = moving && !Act_(Act.Walk);
                if (live)
                {
                    _sprintNeutralTiming = false;
                    if (!_sprintLiveTiming) { _sprintLiveTiming = true; _sprintLiveSince = _now; }
                    // A short wait first: a quick tap of a movement key must never give a short B press
                    // (that is a backstep).
                    if (_now - _sprintLiveSince >= SPRINT_ENGAGE_MS) { _sprintEngaged = true; _sprintHasRun = true; }
                }
                else
                {
                    _sprintLiveTiming = false;
                    if (!_sprintNeutralTiming) { _sprintNeutralTiming = true; _sprintNeutralSince = _now; }
                    if (_now - _sprintNeutralSince >= SPRINT_STOP_MS)
                    {
                        _sprintEngaged = false;
                        if (_sprintHasRun) _sprintOn = false;   // switched on while standing: stays armed
                    }
                }
                if (_sprintEngaged) wantSprint = true;
            }

            // ---- dash / roll / backstep: B, but it can never jump ----
            // A jump is "release B while running, then press B again quickly". After a real
            // sprint, B presses are swallowed for JUMP_GUARD_MS so the game leaves the run
            // state first (same rule as the controller's MUTE_JUMP_FROM_B).
            if (_guardArmed && _now - _sprintEnd >= JUMP_GUARD_MS) _guardArmed = false;
            if ((Act_(Act.Dash) || wantSprint) && (_bHeldLast || !_guardArmed)) k.wButtons |= MASK_B;

            // ---- jump (same as the controller's JUMP_ONLY macro) ----
            if (Pressed(Act.Jump) && moving && !_jumpActive)
            {
                _jumpActive = true;
                _jumpStart = _now;
                _jumpNeedsRamp = (k.wButtons & MASK_B) == 0;   // already sprinting = jump at once
            }
            if (_jumpActive)
            {
                long e = _now - _jumpStart;
                long rampEnd = _jumpNeedsRamp ? RUN_STATE_MS : 0;
                if (e < rampEnd) k.wButtons |= MASK_B;                       // build the run state
                else if (e < rampEnd + JUMP_GAP_MS) k.wButtons &= unchecked((ushort)~MASK_B);
                else if (e < rampEnd + JUMP_GAP_MS + JUMP_TAP_MS) k.wButtons |= MASK_B;   // the jump
                else _jumpActive = false;
            }

            bool bNow = (k.wButtons & MASK_B) != 0;
            if (bNow && !_bHeldLast) { _bHoldStart = _now; _bHoldWasMoving = moving; }
            else if (bNow && moving) _bHoldWasMoving = true;
            if (!bNow && _bHeldLast && !_jumpActive && _bHoldWasMoving && _now - _bHoldStart >= RUN_STATE_MS)
            {
                _sprintEnd = _now;
                _guardArmed = true;
            }
            _bHeldLast = bNow;

            _kbPad = k;
        }

        // =================================================================================
        // Mouse camera
        // =================================================================================
        private static float _pendYaw, _pendPitch;
        private static bool _ctrlCamActive;
        // Mouse follow speed: while the mouse was the last thing that turned the camera, the camera follows the
        // character faster than vanilla, so turning while running feels free like in modern games. The game's
        // own values are kept and put back as soon as the controller's right stick is used.
        private static bool _mouseCam;
        private const float MOUSE_FOLLOW_FACTOR = 5f;   // vanilla 0.1 -> 0.5 (tested: 0.5 and 0.9 feel the same, 0.5 keeps a little smoothing)
        private static byte* _chaseCam;
        private static float _chaseOrigChr, _chaseOrigTarget, _chaseSetChr, _chaseSetTarget;

        private static void CameraFromMouse(bool menu)
        {
            // The controller's right stick has priority: while it turns the camera, the mouse does not, and the
            // camera follows the character at the game's normal speed again.
            if (_ctrlCamActive) { _pendYaw = 0; _pendPitch = 0; _mouseCam = false; return; }

            if (menu || !_focused || (_mouseDX == 0 && _mouseDY == 0) || _locked)
            {
                if (menu || _locked) { _pendYaw = 0; _pendPitch = 0; }
                return;
            }
            bool aim = _phase == Phase.Aim;
            if (aim ? !_aimHooked : !_followHooked) { _pendYaw = 0; _pendPitch = 0; return; }
            if (!aim) _mouseCam = true;
            float sx = aim ? _bowSensX : _sensX, sy = aim ? _bowSensY : _sensY;
            // Kept small if the camera has not taken it yet (cutscene, camera not updating), so it never jumps.
            _pendYaw = Math.Clamp(_pendYaw + _mouseDX * RAD_PER_COUNT * sx * (_invertX ? -1 : 1), -0.5f, 0.5f);
            _pendPitch = Math.Clamp(_pendPitch + _mouseDY * RAD_PER_COUNT * sy * (_invertY ? -1 : 1), -0.5f, 0.5f);
        }

        private static float WrapPi(float a)
        {
            const float TWO_PI = 6.28318531f;
            while (a > 3.14159265f) a -= TWO_PI;
            while (a < -3.14159265f) a += TWO_PI;
            return a;
        }

        // Called by the game's follow camera update, right before the final yaw/pitch become the camera
        // rotation (game thread). The game rebuilds yaw/pitch from the camera position next frame, so the
        // turn made here stays; the manual-control fields keep the game from pulling it back.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static void OnFollowCamUpdate(byte* cam)
        {
            try
            {
                FollowCamApply(cam);
            }
            catch { }
        }

        private static void FollowCamApply(byte* cam)
        {
            ApplyFollowSpeed(cam);
            bool canTurn = (_phase == Phase.Gameplay || _phase == Phase.BowReady) && !_locked && cam[FOLLOW_RESET] == 0 && cam[FOLLOW_EVENT_LOCK] == 0;
            if (canTurn && (_pendYaw != 0 || _pendPitch != 0))
            {
                float* f = (float*)cam;
                f[FOLLOW_YAW / 4] = WrapPi(f[FOLLOW_YAW / 4] + _pendYaw);
                float min = f[FOLLOW_PITCH_MIN / 4], max = f[FOLLOW_PITCH_MAX / 4];
                f[FOLLOW_PITCH / 4] = Math.Clamp(f[FOLLOW_PITCH / 4] + _pendPitch, Math.Min(min, max), Math.Max(min, max));
                f[FOLLOW_TARGET_PITCH / 4] = f[FOLLOW_PITCH / 4];
                f[FOLLOW_CTRL_RATE / 4] = 1.0f;
                f[FOLLOW_CTRL_WAIT / 4] = f[FOLLOW_CTRL_WAIT_TIME / 4];
            }
            _pendYaw = 0; _pendPitch = 0;
        }

        private static void ApplyFollowSpeed(byte* cam)
        {
            float* f = (float*)cam;
            float chr = f[FOLLOW_CHR_CHASE / 4], tgt = f[FOLLOW_TARGET_CHASE / 4];
            // A new camera object, or the game changed a value itself: those are the game's own values.
            if (cam != _chaseCam) { _chaseCam = cam; _chaseOrigChr = chr; _chaseOrigTarget = tgt; _chaseSetChr = chr; _chaseSetTarget = tgt; }
            if (chr != _chaseSetChr) _chaseOrigChr = chr;
            if (tgt != _chaseSetTarget) _chaseOrigTarget = tgt;

            float factor = _mouseCam && (_phase == Phase.Gameplay || _phase == Phase.BowReady) ? MOUSE_FOLLOW_FACTOR : 1f;
            float wantChr = _chaseOrigChr, wantTgt = _chaseOrigTarget;
            if (factor != 1f && _chaseOrigChr > 0 && _chaseOrigChr < 1 && _chaseOrigTarget > 0 && _chaseOrigTarget < 1)
            {
                wantChr = Math.Min(_chaseOrigChr * factor, 0.9f);
                wantTgt = Math.Min(_chaseOrigTarget * factor, 0.9f);
            }
            if (chr != wantChr) f[FOLLOW_CHR_CHASE / 4] = wantChr;
            if (tgt != wantTgt) f[FOLLOW_TARGET_CHASE / 4] = wantTgt;
            _chaseSetChr = wantChr; _chaseSetTarget = wantTgt;
        }

        // Called by the game's aim (bow sniper) camera update, before it runs (game thread).
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static void OnAimCamUpdate(byte* cam)
        {
            try
            {
                if (_pendYaw == 0 && _pendPitch == 0) return;
                if (_phase == Phase.Aim)
                {
                    const float DEG = 0.0174532925f;
                    float* f = (float*)cam;
                    f[AIM_YAW / 4] = WrapPi(f[AIM_YAW / 4] + _pendYaw);
                    float max = f[AIM_PITCH_MAX_DEG / 4] * DEG, min = f[AIM_PITCH_MIN_DEG / 4] * DEG;
                    f[AIM_PITCH / 4] = Math.Clamp(f[AIM_PITCH / 4] + _pendPitch, Math.Min(min, max), Math.Max(min, max));
                }
                _pendYaw = 0; _pendPitch = 0;
            }
            catch { }
        }
    }
}
