using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Translumo.Local;

public static class ScreenCapture
{
    internal sealed class Session : IDisposable
    {
        private const long NativeRetryDelayMs = 10_000;

        private DesktopDuplicationCapture? _native;
        private string? _failedTarget;
        private long _retryAt;
        private bool _disposed;

        public Bitmap Capture(Rectangle bounds, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateBounds(bounds);
            token.ThrowIfCancellationRequested();

            if (_native is not null && !_native.Contains(bounds))
            {
                _native.Dispose();
                _native = null;
                ClearFailure();
            }

            if (_native is null && CanAttemptNative(bounds))
            {
                try
                {
                    _native = new DesktopDuplicationCapture(bounds);
                    ClearFailure();
                }
                catch (Exception error) when (IsExpectedCaptureFailure(error))
                {
                    RecordFailure(bounds);
                }
            }

            if (_native is not null)
            {
                try
                {
                    if (_native.Bounds != bounds) _native.ResizeCrop(bounds);
                    return _native.Capture(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error) when (IsExpectedCaptureFailure(error))
                {
                    DisableNative(bounds);
                }
            }

            token.ThrowIfCancellationRequested();
            return ScreenCapture.Capture(bounds);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _native?.Dispose();
            _native = null;
            _disposed = true;
        }

        private bool CanAttemptNative(Rectangle bounds)
        {
            if (_failedTarget is null) return true;
            string target = TargetDisplay(bounds);
            if (!string.Equals(target, _failedTarget, StringComparison.Ordinal))
            {
                ClearFailure();
                return true;
            }
            return Environment.TickCount64 >= _retryAt;
        }

        private void DisableNative(Rectangle bounds)
        {
            _native?.Dispose();
            _native = null;
            RecordFailure(bounds);
        }

        private void RecordFailure(Rectangle bounds)
        {
            _failedTarget = TargetDisplay(bounds);
            _retryAt = unchecked(Environment.TickCount64 + NativeRetryDelayMs);
        }

        private void ClearFailure()
        {
            _failedTarget = null;
            _retryAt = 0;
        }

        private static bool IsExpectedCaptureFailure(Exception error)
            => error is NotSupportedException or ArgumentOutOfRangeException or TimeoutException or COMException;

        private static string TargetDisplay(Rectangle bounds)
        {
            var screens = System.Windows.Forms.Screen.AllScreens
                .Where(screen => screen.Bounds.IntersectsWith(bounds))
                .OrderBy(screen => screen.DeviceName, StringComparer.Ordinal)
                .ToArray();
            var containing = screens.FirstOrDefault(screen => screen.Bounds.Contains(bounds));
            return containing is not null
                ? $"contained:{containing.DeviceName}"
                : $"intersecting:{string.Join("|", screens.Select(screen => screen.DeviceName))}";
        }
    }

    // Coordinates are physical desktop pixels, including negative monitor origins.
    public static Rectangle VirtualBounds => System.Windows.Forms.SystemInformation.VirtualScreen;

    public static IReadOnlyList<WindowTarget> ListWindows()
    {
        var windows = new List<WindowTarget>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (processId == Environment.ProcessId || GetWindowBounds(handle) is null)
                return true;
            var title = new StringBuilder(GetWindowTextLength(handle) + 1);
            GetWindowText(handle, title, title.Capacity);
            if (!string.IsNullOrWhiteSpace(title.ToString()))
                windows.Add(new WindowTarget(handle, title.ToString()));
            return true;
        }, 0);
        return windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static Rectangle? GetWindowBounds(nint handle)
    {
        if (!IsWindowVisible(handle) || IsIconic(handle))
            return null;
        if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return null;
        if (DwmGetWindowAttribute(handle, 9, out NativeRect rect, Marshal.SizeOf<NativeRect>()) != 0
            && !GetWindowRect(handle, out rect))
            return null;
        var bounds = Rectangle.Intersect(VirtualBounds,
            Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }

    public static Bitmap Capture(Rectangle bounds)
    {
        ValidateBounds(bounds);
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void ValidateBounds(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || !VirtualBounds.Contains(bounds))
            throw new ArgumentOutOfRangeException(nameof(bounds), "Capture bounds must be inside the visible desktop.");
    }

    public static bool ExcludeFromCapture(nint handle) => SetWindowDisplayAffinity(handle, 0x11);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
}
