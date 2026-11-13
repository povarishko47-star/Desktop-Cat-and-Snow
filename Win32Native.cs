using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopSnowSim
{
    public static class Win32Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint VK_S = 0x53;

        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
        public static extern void ZeroMemory(IntPtr dest, IntPtr size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeMessage
        {
            public IntPtr Handle;
            public uint Message;
            public IntPtr WParameter;
            public IntPtr LParameter;
            public uint Time;
            public POINT Location;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width;
            public int Height;
            public SIZE(int w, int h) { Width = w; Height = h; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        public struct TrackedWindowInfo
        {
            public IntPtr Handle;
            public Rectangle Bounds;
            public int ZOrder;
            public bool CanHoldSnow;
        }

        public static void MakeOverlayClickThrough(IntPtr handle)
        {
            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        public static List<TrackedWindowInfo> GetVisibleWindowsInZOrder(IntPtr ownHandle, Rectangle virtualScreen)
        {
            var results = new List<TrackedWindowInfo>(32);
            IntPtr shellWindow = GetShellWindow();
            int zIndex = 0;

            EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == ownHandle || hWnd == shellWindow)
                    return true;

                if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                    return true;

                int cloaked = 0;
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                    return true;

                var className = new StringBuilder(64);
                GetClassName(hWnd, className, className.Capacity);
                string cls = className.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                    return true;

                RECT rect;
                if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(RECT))) != 0)
                    return true;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width < 80 || height < 40)
                    return true;

                var bounds = new Rectangle(rect.Left, rect.Top, width, height);
                bool canHoldSnow = bounds.Top > virtualScreen.Top + 8;

                results.Add(new TrackedWindowInfo
                {
                    Handle = hWnd,
                    Bounds = bounds,
                    ZOrder = zIndex++,
                    CanHoldSnow = canHoldSnow
                });

                return true;
            }, IntPtr.Zero);

            return results;
        }
    }
}