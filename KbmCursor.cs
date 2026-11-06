// =====================================================================================
//  Keyboard + mouse: the mouse cursor.
//
//  - The cursor picture is built into the dll (resources\cursor.png, see the .csproj):
//    one square master (64 x 64), shown at 64 x window height / 2160 (at most its own
//    size): 32 px at 1080p, 43 px at 1440p, 64 px at 2160p and more. It is scaled down
//    smoothly (area average with premultiplied alpha) only when the window height
//    changes. The click point is the tip of the pointing finger (the top-left-most
//    visible pixel of the master), so a new picture works without any setting.
//  - With DSfix (shared block version 3) the cursor is drawn by DSfix inside the game
//    picture, so it has exactly the game's colours; the Windows cursor is then hidden.
//    DSfix uses the same size and click point. Without DSfix nothing changes.
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
        [DllImport("user32.dll")] private static extern int ScreenToClient(IntPtr hwnd, int* point);
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
        // Where the cursor was when the last menu closed (client coordinates). In game the hidden cursor is kept
        // near the middle of the window, so without this every menu would start with the cursor in the middle.
        private static bool _hasMenuCursorPos;
        private static int _menuCursorX, _menuCursorY;
        // Between two windows (e.g. bonfire menu -> level up -> bonfire menu) the game shows no menu for a moment.
        // The cursor is only hidden when no menu is back after this long, so it never blinks between windows.
        private const long MENU_HIDE_DELAY_MS = 200;
        private static long _menuGoneSince;
        private static bool _counterFixedGame, _counterFixedWindow;
        private static int _cursorSize;
        private static long _nextSizeCheck;
        private static readonly System.Collections.Generic.Dictionary<int, IntPtr> _cursorCache = new();
        private static byte[]? _master;                 // resources\cursor.png as RGBA (null: missing or unreadable)
        private static int _masterW, _masterH, _masterHotX, _masterHotY;
        private static bool _masterTried, _hostDraws;

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
            if (menuShown) _menuGoneSince = 0;
            else if (_wasMenuShown)
            {
                if (_menuGoneSince == 0) _menuGoneSince = _now;
                if (_now - _menuGoneSince < MENU_HIDE_DELAY_MS) menuShown = true;   // maybe only between two windows
            }
            if (_focused && menuShown != _wasMenuShown)
            {
                int* pt = stackalloc int[2];
                if (!menuShown)
                {
                    // a menu closes: remember where its cursor was
                    if (GetCursorPos(pt) != 0 && ScreenToClient(_hwnd, pt) != 0)
                    {
                        _menuCursorX = pt[0]; _menuCursorY = pt[1]; _hasMenuCursorPos = true;
                    }
                }
                else if (_hasMenuCursorPos)
                {
                    // a menu opens: the cursor goes back where it was in the last menu (if still inside the window)
                    int* r = stackalloc int[4];
                    if (GetClientRect(_hwnd, r) != 0 && _menuCursorX >= 0 && _menuCursorY >= 0 && _menuCursorX < r[2] && _menuCursorY < r[3])
                    {
                        pt[0] = _menuCursorX; pt[1] = _menuCursorY;
                        if (ClientToScreen(_hwnd, pt) != 0) SetCursorPos(pt[0], pt[1]);
                    }
                }
            }
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
            Shared->CursorVisible = vis ? 1 : 0;
            // DSfix starts or stops drawing the cursor: show / hide the Windows cursor at once
            bool host = HostDrawsCursor();
            if (host != _hostDraws)
            {
                _hostDraws = host;
                Log(host ? "cursor: drawn by DSfix (Windows cursor hidden)" : "cursor: Windows cursor");
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
                            SetCursor(_cursorVisible && !HostDrawsCursor() ? CurrentCursor() : IntPtr.Zero);
                            return (IntPtr)1;
                        }
                        break;
                    case WM_KBM_CURSOR:
                        {
                            int* pt = stackalloc int[2];
                            if (GetCursorPos(pt) != 0 && WindowFromPoint(((long)pt[1] << 32) | (uint)pt[0]) == hwnd)
                                SetCursor(_cursorVisible && !HostDrawsCursor() ? CurrentCursor() : IntPtr.Zero);
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

        // DSfix draws the cursor itself while its heartbeat (shared block version 3) is less than 250 ms old.
        // Signed difference: a heartbeat written a moment "after" this thread read the clock counts as fresh too.
        private static bool HostDrawsCursor()
        {
            uint t = Shared->HostCursorTick;
            if (t == 0) return false;
            int age = unchecked(Environment.TickCount - (int)t);
            return age > -1000 && age < 250;
        }

        private static IntPtr CurrentCursor()
        {
            IntPtr c = _hCursor;
            return c != IntPtr.Zero ? c : LoadCursorW(IntPtr.Zero, (IntPtr)32512);   // IDC_ARROW
        }

        // =============================================================================
        // Cursor picture
        // =============================================================================
        // The cursor's size for a window height: 64 x height / 2160 of the master, at most the master's own size
        // (DSfix uses the same formula for the cursor it draws).
        private static int CursorSizeFor(int height, int masterSize)
        {
            int size = (int)Math.Floor(64.0 * height / 2160.0 + 0.5);
            if (size > masterSize) size = masterSize;
            if (size < 16) size = 16;
            return size;
        }

        private static void ChooseCursorPicture()
        {
            int* r = stackalloc int[4];
            if (GetClientRect(_hwnd, r) == 0) return;
            int h = r[3] - r[1];
            if (h <= 0) return;
            if (!LoadMaster()) { _hCursor = IntPtr.Zero; return; }
            int size = CursorSizeFor(h, Math.Max(_masterW, _masterH));
            if (size == _cursorSize) return;               // only when the window height gives another size
            _cursorSize = size;

            if (!_cursorCache.TryGetValue(size, out IntPtr cur))
            {
                cur = MakeCursor(size);
                _cursorCache[size] = cur;
            }
            _hCursor = cur;
            PostMessageW(_hwnd, WM_KBM_CURSOR, IntPtr.Zero, IntPtr.Zero);
            Log($"window height {h}: cursor {size} px{(cur == IntPtr.Zero ? " NOT AVAILABLE, Windows arrow used" : "")}");
        }

        // resources\cursor.png, read once
        private static bool LoadMaster()
        {
            if (_masterTried) return _master != null;
            _masterTried = true;
            try
            {
                using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("cursor.png");
                if (s == null) { Log("cursor.png: missing from the dll"); return false; }
                byte[] png = new byte[s.Length];
                s.ReadExactly(png);
                byte[]? rgba = DecodePng(png, out int w, out int h);
                if (rgba == null) { Log("cursor.png: not a supported PNG (needs 8-bit RGBA or RGB, not interlaced)"); return false; }
                // Click point: the top-left-most visible pixel (the finger tip).
                int hx = 0, hy = 0, best = int.MaxValue;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (rgba[(y * w + x) * 4 + 3] >= 128 && x + y < best) { best = x + y; hx = x; hy = y; }
                _master = rgba; _masterW = w; _masterH = h; _masterHotX = hx; _masterHotY = hy;
                Log($"cursor.png: {w}x{h}, click point {hx},{hy}");
                return true;
            }
            catch (Exception e) { Log($"cursor.png: {e.Message}"); return false; }
        }

        // The master scaled so its larger side is 'size' px: each new pixel is the area average of the master pixels
        // it covers, with premultiplied alpha (transparent pixels do not darken the edges). Returns straight RGBA.
        private static byte[] Downscale(byte[] src, int sw, int sh, int dw, int dh)
        {
            byte[] dst = new byte[dw * dh * 4];
            double fx = (double)sw / dw, fy = (double)sh / dh;
            for (int y = 0; y < dh; y++)
            {
                double y0 = y * fy, y1 = y0 + fy;
                for (int x = 0; x < dw; x++)
                {
                    double x0 = x * fx, x1 = x0 + fx;
                    double r = 0, g = 0, b = 0, a = 0, area = 0;
                    for (int sy = (int)y0; sy < sh && sy < y1; sy++)
                    {
                        double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                        if (wy <= 0) continue;
                        for (int sx = (int)x0; sx < sw && sx < x1; sx++)
                        {
                            double wx = Math.Min(sx + 1, x1) - Math.Max(sx, x0);
                            if (wx <= 0) continue;
                            double wgt = wx * wy;
                            int i = (sy * sw + sx) * 4;
                            double al = src[i + 3] / 255.0;
                            r += src[i] * al * wgt; g += src[i + 1] * al * wgt; b += src[i + 2] * al * wgt; a += al * wgt;
                            area += wgt;
                        }
                    }
                    int o = (y * dw + x) * 4;
                    if (a <= 0 || area <= 0) continue;            // fully transparent
                    dst[o] = (byte)Math.Min(255, Math.Floor(r / a + 0.5));
                    dst[o + 1] = (byte)Math.Min(255, Math.Floor(g / a + 0.5));
                    dst[o + 2] = (byte)Math.Min(255, Math.Floor(b / a + 0.5));
                    dst[o + 3] = (byte)Math.Min(255, Math.Floor(a / area * 255.0 + 0.5));
                }
            }
            return dst;
        }

        private static IntPtr MakeCursor(int size)
        {
            try
            {
                byte[] master = _master!;
                int big = Math.Max(_masterW, _masterH);
                int w = Math.Max(1, (int)Math.Floor((double)_masterW * size / big + 0.5));
                int h = Math.Max(1, (int)Math.Floor((double)_masterH * size / big + 0.5));
                byte[] rgba = (w == _masterW && h == _masterH) ? master : Downscale(master, _masterW, _masterH, w, h);
                // the click point scaled the same way (DSfix uses the same rounding)
                int hx = Math.Min(w - 1, (int)Math.Floor((double)_masterHotX * w / _masterW + 0.5));
                int hy = Math.Min(h - 1, (int)Math.Floor((double)_masterHotY * h / _masterH + 0.5));

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
                Log($"cursor {w}x{h}, click point {hx},{hy}{(cur == IntPtr.Zero ? ", CreateIconIndirect FAILED" : "")}");
                return cur;
            }
            catch (Exception e) { Log($"cursor: {e.Message}"); return IntPtr.Zero; }
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
