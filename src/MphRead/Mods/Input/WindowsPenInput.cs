using System;
using System.Runtime.InteropServices;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Windowing.Desktop;

namespace MphRead.Mods.Input
{
    /// <summary>
    /// Optional observer on the existing GLFW window. Messages still reach GLFW,
    /// including mouse promotion for menus. Only gameplay resolves pen and mouse separately.
    /// </summary>
    public static class WindowsPenInput
    {
        private const uint PointerUpdate = 0x0245, PointerDown = 0x0246, PointerUp = 0x0247;
        private const uint PointerEnter = 0x0249, PointerLeave = 0x024A, PointerCaptureChanged = 0x024C;
        private static readonly SubclassProc _callback = WindowProc;
        private static IntPtr _window;
        private static PointerSample _pen;
        private static bool _releasePending;
        private static bool _physicalPrimary;
        private static bool _failed;

        public static bool Attached => _window != IntPtr.Zero;

        public static unsafe void Attach(NativeWindow window)
        {
            if (!OperatingSystem.IsWindows() || Attached)
            {
                return;
            }
            try
            {
                IntPtr handle = GLFW.GetWin32Window(window.WindowPtr);
                if (SetWindowSubclass(handle, _callback, 1, 0))
                {
                    _window = handle;
                    _failed = false;
                    DebugLog.Line("input", "Windows pen observer attached; GLFW fallback available");
                }
                else
                {
                    DebugLog.Line("input", "Windows pen observer unavailable; using GLFW pointer input");
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                DebugLog.Line("input", $"Windows pen observer unavailable: {ex.Message}");
            }
        }

        public static PointerSample Read(MouseState mouse, int width, int height, out bool independentPrimary)
        {
            independentPrimary = false;
            if (Attached && !_failed && (_pen.InRange || _pen.InContact || _releasePending))
            {
                _releasePending = false;
                independentPrimary = _physicalPrimary;
                float scaleX = 1, scaleY = 1;
                if (GetClientRect(_window, out Rect rect) && rect.Right > 0 && rect.Bottom > 0)
                {
                    scaleX = width / (float)rect.Right;
                    scaleY = height / (float)rect.Bottom;
                }
                return _pen with { X = _pen.X * scaleX, Y = _pen.Y * scaleY };
            }
            // Unknown is intentional: GLFW alone cannot distinguish a mouse from a tablet.
            bool down = mouse.IsButtonDown(MouseButton.Left);
            return new PointerSample(PointerDeviceType.Unknown, 0, mouse.X, mouse.Y, down, down, true);
        }

        internal static bool IsPromotedPointer(nuint extraInfo)
            => (extraInfo & 0xFFFFFF00u) == 0xFF515700u;

        internal static bool IsPromotedPrimaryRelease(uint message, nuint extraInfo)
            => message == 0x0202 && IsPromotedPointer(extraInfo);

        private static void EndContact()
        {
            _releasePending = _pen.InRange || _pen.InContact;
            _pen = _pen with { PrimaryDown = false, InContact = false, InRange = false };
        }

        private static IntPtr WindowProc(IntPtr hwnd, uint message, nuint wParam, nint lParam,
            nuint id, nuint data)
        {
            try
            {
                if (message == 0x0082) // WM_NCDESTROY: detach even if the window bypasses OnClosing.
                {
                    RemoveWindowSubclass(hwnd, _callback, id);
                    _window = IntPtr.Zero;
                    _pen = default;
                    _physicalPrimary = _releasePending = false;
                    PointerDevice.Reset();
                }
                else if (message == 0x0008) // WM_KILLFOCUS
                {
                    EndContact();
                    _physicalPrimary = false;
                    PointerDevice.Reset();
                }
                else if (message == 0x0215) // WM_CAPTURECHANGED: the active gesture no longer owns the window.
                {
                    _physicalPrimary = false;
                    if (_pen.InContact)
                    {
                        EndContact();
                    }
                }
                else if (message == 0x0201 || message == 0x0202 || message == 0x0203)
                {
                    nuint extraInfo = (nuint)GetMessageExtraInfo();
                    if (IsPromotedPrimaryRelease(message, extraInfo))
                    {
                        // Some tablet drivers promote the pen tip to mouse input even
                        // when their native WM_POINTERUP is delayed or absent. Treat the
                        // promoted release as a fallback edge only while native contact
                        // is still active; a normal native release remains authoritative.
                        if (_pen.InContact)
                        {
                            EndContact();
                        }
                    }
                    else if (!IsPromotedPointer(extraInfo))
                    {
                        _physicalPrimary = message != 0x0202;
                    }
                }
                else if (!_failed)
                {
                    uint pointerId = (uint)(wParam & 0xFFFF);
                    if (message == PointerCaptureChanged && pointerId == _pen.Id)
                    {
                        EndContact();
                    }
                    else if (message == PointerDown || message == PointerUpdate || message == PointerUp
                        || message == PointerEnter || message == PointerLeave)
                    {
                        ReadPen(hwnd, message, pointerId);
                    }
                }
            }
            catch (Exception ex)
            {
                // Never unwind a managed exception through the native window procedure.
                // Failure disables only this optional observer, leaving GLFW operational.
                if (!_failed)
                {
                    _failed = true;
                    EndContact();
                    PointerDevice.Reset();
                    try
                    {
                        DebugLog.Line("input", $"Windows pen observer failed; using GLFW: {ex.Message}");
                    }
                    catch (Exception)
                    {
                        // A full or removed log drive must not unwind through Win32 either.
                    }
                }
            }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        private static void ReadPen(IntPtr hwnd, uint message, uint id)
        {
            // Another pen cannot steal an active gesture. A lifted pen may be replaced.
            if (_pen.InContact && id != _pen.Id)
            {
                return;
            }
            if (!GetPointerType(id, out uint type))
            {
                if (id == _pen.Id)
                {
                    EndContact();
                }
                return;
            }
            if (type != 3) // PT_PEN
            {
                return;
            }
            if (!GetPointerPenInfo(id, out PenInfo info))
            {
                if (id == _pen.Id)
                {
                    EndContact();
                }
                return;
            }
            Point point = info.Pointer.PixelLocation;
            if (!ScreenToClient(hwnd, ref point))
            {
                EndContact();
                return;
            }
            bool contact = (info.Pointer.Flags & 0x4) != 0 && message != PointerUp;
            bool inRange = (info.Pointer.Flags & 0x2) != 0;
            if ((info.Pointer.Flags & 0x8000) != 0 // POINTER_FLAG_CANCELED
                || (message == PointerLeave && !contact))
            {
                contact = inRange = false;
            }
            _pen = new PointerSample(PointerDeviceType.Pen, id, point.X, point.Y,
                contact, contact, inRange,
                (info.Mask & 1) != 0 ? info.Pressure / 1024f : 0,
                (info.Mask & 4) != 0 ? info.TiltX : 0,
                (info.Mask & 8) != 0 ? info.TiltY : 0);
            _releasePending = !inRange;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, nuint wParam,
            nint lParam, nuint id, nuint data);

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PointerInfo
        {
            public uint Type, Id, FrameId, Flags;
            public IntPtr SourceDevice, Target;
            public Point PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
            public uint Time, HistoryCount;
            public int InputData;
            public uint KeyStates;
            public ulong PerformanceCount;
            public uint ButtonChangeType;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PenInfo
        {
            public PointerInfo Pointer;
            public uint Flags, Mask, Pressure, Rotation;
            public int TiltX, TiltY;
        }

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPointerType(uint id, out uint type);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPointerPenInfo(uint id, out PenInfo info);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")]
        private static extern nint GetMessageExtraInfo();
    }
}
