// =====================================================================================
//  Keyboard + mouse: the mouse cursor.
//
//  - The cursor picture is built into the dll (resources\cursor_*.png, see the
//    .csproj) and chosen by the game window's height:
//        up to 1080 -> cursor_1080p.png, 1081..2159 -> cursor_1440p.png,
//        2160 and more -> cursor_2160p.png
//    The click point is the tip of the pointing finger (the top-left-most visible
//    pixel), so a new picture works without any setting.
//  - In game the cursor is hidden and kept in the middle of the window. In menus it
//    shows when the menu was opened with keyboard or mouse, or when the mouse moves,
//    and hides again when the controller is used.
//  - DSfix "disableCursor 1" is undone, so these rules always apply.
//  - A single ALT press no longer opens the window menu (which froze the game for a
//    moment). ALT+TAB and ALT+F4 still work.
// =====================================================================================
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PlayStationPDTEinputFix
{
    internal static unsafe partial class KeyboardMouse
    {
        [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern IntPtr CallWindowProcW(IntPtr proc, IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] private static extern int ShowCursor(int show);
        [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
        [DllImport("user32.dll")] private static extern int PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern int GetClientRect(IntPtr hwnd, int* rect);
        [DllImport("user32.dll")] private static extern int ClientToScreen(IntPtr hwnd, int* point);
        [DllImport("user32.dll")] private static extern int SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern int GetCursorPos(int* point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(long point);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(IconInfo* info);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, void* bmi, uint usage, void** bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, void* bits);
        [DllImport("gdi32.dll")] private static extern int DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo { public int fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

        private const uint WM_SETCURSOR = 0x0020, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_SYSCOMMAND = 0x0112;
        private const uint WM_KBM_CURSOR = 0x8000 + 0x1D3;   // WM_APP + n: "apply the cursor now"

        private static volatile bool _cursorVisible;
        private static volatile IntPtr _hCursor;
        private static IntPtr _oldWndProc, _subclassed;
        private static bool _lastWasKbm = true, _wasMenuShown;
        private static bool _counterFixedGame, _counterFixedWindow;
        private static string _cursorName = "";
        private static long _nextSizeCheck;
        private static readonly System.Collections.Generic.Dictionary<string, IntPtr> _cursorCache = new();

        // =============================================================================
        // Per poll (game thread)
        // =============================================================================
        private static void UpdateCursor(bool menuOrLoading, bool loading, bool mouseActivity, bool kbmUsed, bool controllerUsed)
        {
            if (_hwnd == IntPtr.Zero) return;
            EnsureSubclass();
            if (_now >= _nextSizeCheck) { _nextSizeCheck = _now + 1000; ChooseCursorPicture(); }

            if (!_counterFixedGame)
            {
                // DSfix disableCursor may have hidden the cursor from this thread.
                _counterFixedGame = true;
                for (int n = 0; n < 50 && ShowCursor(1) < 0; n++) { }
            }

            if (controllerUsed) _lastWasKbm = false;
            else if (kbmUsed) _lastWasKbm = true;

            bool menuShown = menuOrLoading && !loading;
            bool vis;
            if (!menuShown) vis = false;
            else if (!_wasMenuShown) vis = _lastWasKbm;          // a menu just opened
            else
            {
                vis = _cursorVisible;
                if (mouseActivity) vis = true;
                if (controllerUsed) vis = false;
            }
            _wasMenuShown = menuShown;

            if (vis != _cursorVisible)
            {
                _cursorVisible = vis;
                PostMessageW(_hwnd, WM_KBM_CURSOR, IntPtr.Zero, IntPtr.Zero);
            }

            // In game: keep the hidden cursor near the middle, so it never leaves the window. It is only
            // moved back when it has gone more than a quarter of the window away (not every frame).
            if (!menuShown && _focused)
            {
                int* r = stackalloc int[4];
                int* cur = stackalloc int[2];
                if (GetClientRect(_hwnd, r) != 0 && GetCursorPos(cur) != 0)
                {
                    int* pt = stackalloc int[2];
                    int w = r[2] - r[0], h = r[3] - r[1];
                    pt[0] = w / 2; pt[1] = h / 2;
                    ClientToScreen(_hwnd, pt);
                    if (Math.Abs(cur[0] - pt[0]) > w / 4 || Math.Abs(cur[1] - pt[1]) > h / 4) SetCursorPos(pt[0], pt[1]);
                }
            }
        }

        private static void EnsureSubclass()
        {
            if (_subclassed == _hwnd) return;
            _subclassed = _hwnd;
            _oldWndProc = (IntPtr)SetWindowLongW(_hwnd, -4, (int)(IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc);
            Log($"window 0x{(uint)_hwnd:X8} subclassed (old procedure 0x{(uint)_oldWndProc:X8})");
        }

        // Runs on the window's thread.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            try
            {
                if (!_counterFixedWindow)
                {
                    _counterFixedWindow = true;
                    for (int n = 0; n < 50 && ShowCursor(1) < 0; n++) { }
                }
                switch (msg)
                {
                    case WM_SETCURSOR:
                        if (((int)l & 0xFFFF) == 1)   // HTCLIENT: the cursor is over the game picture
                        {
                            SetCursor(_cursorVisible ? CurrentCursor() : IntPtr.Zero);
                            return (IntPtr)1;
                        }
                        break;
                    case WM_KBM_CURSOR:
                        {
                            int* pt = stackalloc int[2];
                            if (GetCursorPos(pt) != 0 && WindowFromPoint(((long)pt[1] << 32) | (uint)pt[0]) == hwnd)
                                SetCursor(_cursorVisible ? CurrentCursor() : IntPtr.Zero);
                            return IntPtr.Zero;
                        }
                    case WM_SYSKEYDOWN:
                    case WM_SYSKEYUP:
                        if ((int)w == 0x12) return IntPtr.Zero;   // ALT alone: no window menu
                        break;
                    case WM_SYSCOMMAND:
                        if (((int)w & 0xFFF0) == 0xF100 && l == IntPtr.Zero) return IntPtr.Zero;  // SC_KEYMENU from the keyboard
                        break;
                }
            }
            catch { }
            return CallWindowProcW(_oldWndProc, hwnd, msg, w, l);
        }

        private static IntPtr CurrentCursor()
        {
            IntPtr c = _hCursor;
            return c != IntPtr.Zero ? c : LoadCursorW(IntPtr.Zero, (IntPtr)32512);   // IDC_ARROW
        }

        // =============================================================================
        // Cursor picture
        // =============================================================================
        private static void ChooseCursorPicture()
        {
            int* r = stackalloc int[4];
            if (GetClientRect(_hwnd, r) == 0) return;
            int h = r[3] - r[1];
            if (h <= 0) return;
            string name = h <= 1080 ? "cursor_1080p.png" : h < 2160 ? "cursor_1440p.png" : "cursor_2160p.png";
            if (name == _cursorName) return;
            _cursorName = name;

            if (!_cursorCache.TryGetValue(name, out IntPtr cur))
            {
                cur = LoadCursorPicture(name);
                _cursorCache[name] = cur;
            }
            _hCursor = cur;
            PostMessageW(_hwnd, WM_KBM_CURSOR, IntPtr.Zero, IntPtr.Zero);
            Log($"window height {h}: cursor picture {name}{(cur == IntPtr.Zero ? " NOT AVAILABLE, Windows arrow used" : "")}");
        }

        private static IntPtr LoadCursorPicture(string name)
        {
            try
            {
                using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (s == null) return IntPtr.Zero;
                byte[] png = new byte[s.Length];
                s.ReadExactly(png);
                byte[]? rgba = DecodePng(png, out int w, out int h);
                if (rgba == null) { Log($"{name}: not a supported PNG (needs 8-bit RGBA or RGB, not interlaced)"); return IntPtr.Zero; }

                // Click point: the top-left-most visible pixel (the finger tip).
                int hx = 0, hy = 0, best = int.MaxValue;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (rgba[(y * w + x) * 4 + 3] >= 128 && x + y < best) { best = x + y; hx = x; hy = y; }

                int* bmi = stackalloc int[10];
                bmi[0] = 40; bmi[1] = w; bmi[2] = -h;              // top-down
                bmi[3] = 1 | (32 << 16);                            // planes 1, 32 bits
                IntPtr dc = GetDC(IntPtr.Zero);
                void* bits;
                IntPtr color = CreateDIBSection(dc, bmi, 0, &bits, IntPtr.Zero, 0);
                ReleaseDC(IntPtr.Zero, dc);
                if (color == IntPtr.Zero) return IntPtr.Zero;
                byte* p = (byte*)bits;
                for (int i = 0; i < w * h; i++)
                {
                    p[i * 4 + 0] = rgba[i * 4 + 2];
                    p[i * 4 + 1] = rgba[i * 4 + 1];
                    p[i * 4 + 2] = rgba[i * 4 + 0];
                    p[i * 4 + 3] = rgba[i * 4 + 3];
                }
                byte[] maskBits = new byte[((w + 15) / 16) * 2 * h];
                IntPtr mask;
                fixed (byte* m = maskBits) mask = CreateBitmap(w, h, 1, 1, m);
                IconInfo ii = new IconInfo { fIcon = 0, xHotspot = hx, yHotspot = hy, hbmMask = mask, hbmColor = color };
                IntPtr cur = CreateIconIndirect(&ii);
                DeleteObject(color);
                DeleteObject(mask);
                Log($"{name}: {w}x{h}, click point {hx},{hy}{(cur == IntPtr.Zero ? ", CreateIconIndirect FAILED" : "")}");
                return cur;
            }
            catch (Exception e) { Log($"{name}: {e.Message}"); return IntPtr.Zero; }
        }

        // Minimal PNG reader: 8-bit RGBA or RGB, not interlaced. Returns RGBA.
        private static byte[]? DecodePng(byte[] d, out int w, out int h)
        {
            w = h = 0;
            if (d.Length < 33 || d[0] != 0x89 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G') return null;
            static int BE(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
            int pos = 8, depth = 0, type = 0, interlace = 0;
            var idat = new MemoryStream();
            while (pos + 12 <= d.Length)
            {
                int len = BE(d, pos);
                if (len < 0 || pos + 12 + len > d.Length) return null;
                string t = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
                if (t == "IHDR") { w = BE(d, pos + 8); h = BE(d, pos + 12); depth = d[pos + 16]; type = d[pos + 17]; interlace = d[pos + 20]; }
                else if (t == "IDAT") idat.Write(d, pos + 8, len);
                else if (t == "IEND") break;
                pos += 12 + len;
            }
            if (depth != 8 || (type != 6 && type != 2) || interlace != 0 || w <= 0 || h <= 0 || w > 256 || h > 256) return null;
            int bpp = type == 6 ? 4 : 3, stride = w * bpp;
            byte[] raw = new byte[(stride + 1) * h];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);

            byte[] img = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                int f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? img[dst + x - bpp] : 0;
                    int b = y > 0 ? img[dst - stride + x] : 0;
                    int c = (x >= bpp && y > 0) ? img[dst - stride + x - bpp] : 0;
                    int v = raw[src + x];
                    switch (f)
                    {
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) / 2; break;
                        case 4:
                            int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                            v += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                            break;
                    }
                    img[dst + x] = (byte)v;
                }
            }
            if (bpp == 4) return img;
            byte[] rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { rgba[i * 4] = img[i * 3]; rgba[i * 4 + 1] = img[i * 3 + 1]; rgba[i * 4 + 2] = img[i * 3 + 2]; rgba[i * 4 + 3] = 255; }
            return rgba;
        }
    }
}
