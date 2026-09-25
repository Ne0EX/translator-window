using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;

namespace Translumo.Local;

/// <summary>Observes reader navigation without intercepting or delaying its input.</summary>
internal sealed class NavigationInputObserver : IDisposable
{
    private const int WmInput = 0x00ff;
    private const uint RidInput = 0x10000003;
    private const uint RidHeader = 0x10000005;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RimTypeHid = 2;
    private const ushort MouseLeftDown = 0x0001;
    private const ushort MouseLeftUp = 0x0002;
    private const ushort MouseWheel = 0x0400;
    private const ushort MouseHWheel = 0x0800;
    private const ushort KeyBreak = 0x0001;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;
    private const uint GwHwndNext = 2;
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int QuietMilliseconds = 450;

    private readonly HwndSource _source;
    private readonly Func<Rectangle?> _getBounds;
    private readonly nint _targetWindow;
    private readonly nint _controlsWindow;
    private readonly nint _overlayWindow;
    private readonly HashSet<nint> _leftButtonDevices = new();
    private bool _hookAdded;
    private bool _registrationAttempted;
    private bool _registered;
    private bool _disposed;
    private long _quietUntil;
    private long _generation;

    internal NavigationInputObserver(HwndSource source, Func<Rectangle?> getBounds,
        nint targetWindow, nint controlsWindow, nint overlayWindow)
    {
        _source = source;
        _getBounds = getBounds;
        _targetWindow = targetWindow;
        _controlsWindow = controlsWindow;
        _overlayWindow = overlayWindow;
        try
        {
            _source.AddHook(WndProc);
            _hookAdded = true;
            var devices = MouseAndKeyboard(RidevInputSink, _source.Handle);
            _registrationAttempted = true;
            if (!RegisterRawInputDevices(devices, (uint)devices.Length,
                    (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
            var touchpad = Touchpad(RidevInputSink, _source.Handle);
            RegisterRawInputDevices(touchpad, (uint)touchpad.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            RemoveRegistration();
            if (_hookAdded)
            {
                _source.RemoveHook(WndProc);
                _hookAdded = false;
            }
        }
    }

    internal event Action? Navigation;

    internal long Generation => Interlocked.Read(ref _generation);

    internal bool IsActive => RemainingQuietMilliseconds > 0;

    internal int RemainingQuietMilliseconds
        => (int)Math.Clamp(Volatile.Read(ref _quietUntil) - Environment.TickCount64, 0, QuietMilliseconds);

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmInput || _disposed || !_registered) return 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(lParam, RidHeader, out RawInputHeader header, ref headerSize,
                headerSize) == uint.MaxValue) return 0;
        try
        {
            if (header.Type == RimTypeHid)
            {
                if (IsScoped(keyboard: false)) Signal();
            }
            else
            {
                uint size = (uint)Marshal.SizeOf<RawInput>();
                if (GetRawInputData(lParam, RidInput, out RawInput input, ref size,
                        (uint)Marshal.SizeOf<RawInputHeader>()) == uint.MaxValue) return 0;
                if (input.Header.Type == RimTypeMouse) ProcessMouse(input.Header.Device, input.Data.Mouse);
                else if (input.Header.Type == RimTypeKeyboard) ProcessKeyboard(input.Data.Keyboard);
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // Observation is optional; pixel comparison remains the fallback if the target changes mid-message.
        }
        // Leave the message unhandled so HwndSource calls DefWindowProc for WM_INPUT cleanup.
        return 0;
    }

    private void ProcessMouse(nint device, RawMouse mouse)
    {
        ushort flags = mouse.ButtonFlags;
        bool held = _leftButtonDevices.Contains(device);
        bool movedWhileHeld = held && (mouse.LastX != 0 || mouse.LastY != 0);
        if ((flags & (MouseLeftDown | MouseLeftUp | MouseWheel | MouseHWheel)) == 0 && !movedWhileHeld) return;
        bool scoped = IsScoped(keyboard: false);
        if ((flags & MouseLeftDown) != 0)
        {
            if (scoped) _leftButtonDevices.Add(device);
            else _leftButtonDevices.Remove(device);
            if (scoped) Signal();
        }
        if ((flags & (MouseWheel | MouseHWheel)) != 0 && scoped) Signal();
        if (movedWhileHeld && scoped) Signal();
        if ((flags & MouseLeftUp) != 0)
        {
            bool wasHeld = _leftButtonDevices.Remove(device);
            if (wasHeld && scoped) Signal();
        }
    }

    private void ProcessKeyboard(RawKeyboard keyboard)
    {
        if ((keyboard.Flags & KeyBreak) != 0 || !IsNavigationKey(keyboard.VirtualKey)
            || !IsScoped(keyboard: true)) return;
        Signal();
    }

    private bool IsScoped(bool keyboard)
    {
        Rectangle? bounds;
        try { bounds = _getBounds(); }
        catch (InvalidOperationException) { return false; }
        if (bounds is null || bounds.Value.Width < 1 || bounds.Value.Height < 1) return false;

        nint inputWindow;
        if (keyboard) inputWindow = GetForegroundWindow();
        else
        {
            if (!GetCursorPos(out var point) || !bounds.Value.Contains(point.X, point.Y)) return false;
            inputWindow = WindowUnderOverlay(point);
        }
        if (inputWindow == 0 || SameRootWindow(inputWindow, _controlsWindow)) return false;
        if (_targetWindow != 0) return SameRootWindow(inputWindow, _targetWindow);
        nint root = GetAncestor(inputWindow, GaRootOwner);
        if (!GetWindowRect(root == 0 ? inputWindow : root, out var rect)) return false;
        var intersection = Rectangle.Intersect(bounds.Value, rect.ToRectangle());
        return intersection.Width > 0 && intersection.Height > 0;
    }

    private nint WindowUnderOverlay(Point point)
    {
        nint window = WindowFromPoint(point);
        nint root = GetAncestor(window, GaRoot);
        for (nint candidate = root == 0 ? window : root;
             candidate != 0; candidate = GetWindow(candidate, GwHwndNext))
        {
            if (!IsWindowVisible(candidate) || !GetWindowRect(candidate, out var rect)
                || !rect.ToRectangle().Contains(point.X, point.Y)
                || SameRootWindow(candidate, _overlayWindow)) continue;
            int style = GetWindowLong(candidate, GwlExStyle);
            if ((style & (WsExLayered | WsExTransparent)) == (WsExLayered | WsExTransparent)) continue;
            return candidate;
        }
        return 0;
    }

    private static bool SameRootWindow(nint first, nint second)
    {
        if (first == 0 || second == 0) return false;
        nint firstRoot = GetAncestor(first, GaRootOwner);
        nint secondRoot = GetAncestor(second, GaRootOwner);
        return (firstRoot == 0 ? first : firstRoot) == (secondRoot == 0 ? second : secondRoot);
    }

    private void Signal()
    {
        Volatile.Write(ref _quietUntil, Environment.TickCount64 + QuietMilliseconds);
        Interlocked.Increment(ref _generation);
        Navigation?.Invoke();
    }

    private static bool IsNavigationKey(ushort key)
        => key is 0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _leftButtonDevices.Clear();
        RemoveRegistration();
        if (_hookAdded)
        {
            _source.RemoveHook(WndProc);
            _hookAdded = false;
        }
        Navigation = null;
    }

    private void RemoveRegistration()
    {
        if (!_registered && !_registrationAttempted) return;
        var touchpad = Touchpad(RidevRemove, 0);
        RegisterRawInputDevices(touchpad, (uint)touchpad.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        var devices = MouseAndKeyboard(RidevRemove, 0);
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        _registered = false;
        _registrationAttempted = false;
    }

    private static RawInputDevice[] MouseAndKeyboard(uint flags, nint target) => new[]
    {
        new RawInputDevice { UsagePage = 1, Usage = 2, Flags = flags, Target = target },
        new RawInputDevice { UsagePage = 1, Usage = 6, Flags = flags, Target = target }
    };

    private static RawInputDevice[] Touchpad(uint flags, nint target) => new[]
    {
        new RawInputDevice { UsagePage = 0x0d, Usage = 0x05, Flags = flags, Target = target }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        internal RawInputHeader Header;
        internal RawInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RawInputUnion
    {
        [FieldOffset(0)] internal RawMouse Mouse;
        [FieldOffset(0)] internal RawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RawMouse
    {
        [FieldOffset(0)] internal ushort Flags;
        [FieldOffset(4)] internal uint Buttons;
        [FieldOffset(4)] internal ushort ButtonFlags;
        [FieldOffset(6)] internal ushort ButtonData;
        [FieldOffset(8)] internal uint RawButtons;
        [FieldOffset(12)] internal int LastX;
        [FieldOffset(16)] internal int LastY;
        [FieldOffset(20)] internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        internal ushort MakeCode;
        internal ushort Flags;
        internal ushort Reserved;
        internal ushort VirtualKey;
        internal uint Message;
        internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
        internal readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, out RawInput data,
        ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, out RawInputHeader data,
        ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);
}
