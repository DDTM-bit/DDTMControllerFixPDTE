// =====================================================================================
//  Keyboard + mouse: RTX Remix compatibility (ds1-rtx-remix-proxy, its d3d9.dll next to
//  the game). Only on when that d3d9.dll is loaded (it exports "lookdev_host_feed");
//  never with DSfix or the game's own d3d9.dll.
//
//  - That mod's F4 menu (its settings window) uses the mouse: while it is open, every
//    DirectInput mouse read comes back empty, from any caller (ours too), so our mouse
//    camera and clicks already stop by themselves. But our "keep the hidden cursor in the
//    middle" (KbmCursor.cs) pulled the menu's cursor back.
//  - Nothing of that mod is read: we watch what it does. The cursor moves while our mouse
//    reads no movement, two polls in a row: the menu has the mouse, and the cursor is left
//    alone. It ends as soon as our mouse reads movement again (the menu closed).
//  - The keyboard is not held by that menu (its keys reach the game too); left as it is.
//    Its Alt+X menu (the Remix runtime's own) already holds everything.
//  - Cost: one GetCursorPos per poll in game, only with that mod.
//  - The DirectInput fixes in Proxy.cs and KbmCore.cs (one call at a time, wide objects
//    only, our own object for our mouse) were found with this mod too, but help with any
//    program that makes its own DirectInput objects in the game, so they are always on.
// =====================================================================================
using System;
using System.Runtime.InteropServices;

namespace PlayStationPDTEinputFix
{
    internal static unsafe partial class KeyboardMouse
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern IntPtr GetModuleHandleByName(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static bool _rtxChecked, _rtxActive;
        private static bool _rtxHeld;
        private static int _rtxMiss;              // polls in a row that looked held
        private static int _rtxCurX, _rtxCurY;
        private static bool _rtxHaveCur;

        // The RTX Remix d3d9.dll is a static import of the game, so it is loaded before the first poll.
        private static bool RtxActive()
        {
            if (_rtxChecked) return _rtxActive;
            _rtxChecked = true;
            IntPtr d3d9 = GetModuleHandleByName("d3d9.dll");
            _rtxActive = d3d9 != IntPtr.Zero && GetProcAddress(d3d9, "lookdev_host_feed") != IntPtr.Zero;
            return _rtxActive;
        }

        /// <summary>KbmCursor.cs, in game with the window in front, before the cursor is moved back to the middle.
        /// True = the RTX Remix F4 menu has the mouse.</summary>
        private static bool RtxMenuHasMouse()
        {
            if (!RtxActive()) return false;
            bool mouseReads = _mouseDX != 0 || _mouseDY != 0;
            bool cursorMoved = false;
            int* cur = stackalloc int[2];
            if (GetCursorPos(cur) != 0)
            {
                cursorMoved = _rtxHaveCur && (cur[0] != _rtxCurX || cur[1] != _rtxCurY);
                _rtxCurX = cur[0]; _rtxCurY = cur[1]; _rtxHaveCur = true;
            }

            if (mouseReads) { _rtxMiss = 0; _rtxHeld = false; }                                          // our mouse reads: nothing holds it
            else if (cursorMoved && _ownMouse != IntPtr.Zero) { if (++_rtxMiss >= 2) _rtxHeld = true; }   // moved, but not for us
            else _rtxMiss = 0;                                                                            // nothing to tell: as before
            return _rtxHeld;
        }
    }
}
