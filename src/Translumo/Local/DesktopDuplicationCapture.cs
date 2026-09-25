using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace Translumo.Local;

// The narrow ABI below is limited to the methods used here. Vtable slots were checked against
// Windows SDK 10.0.26100.0 dxgi*.h and d3d11.h.
internal sealed class DesktopDuplicationCapture : IDisposable
{
    private const int S_OK = 0;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    private const int DXGI_MODE_ROTATION_IDENTITY = 1;
    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const int D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const int D3D11_MAP_READ = 1;

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidOutput5 = new("80a07424-ab52-42eb-833c-0c42fd282d98");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private readonly object _gate = new();
    private nint _adapter;
    private nint _output;
    private nint _output5;
    private nint _device;
    private nint _context;
    private nint _duplication;
    private nint _staging;
    private nint _heldTexture;
    private OutputDesc _outputDesc;
    private Rectangle _bounds;
    private bool _frameHeld;
    private bool _heldHasDesktopImage;
    private bool _hasPixels;
    private bool _disposed;
    private int _stagingFormat = DXGI_FORMAT_R16G16B16A16_FLOAT;
    private float _sdrWhiteScale = 1;
    private byte[]? _scRgbToByte;

    public Rectangle OutputBounds => _outputDesc.DesktopCoordinates.ToRectangle();
    public Rectangle Bounds => _bounds;

    public DesktopDuplicationCapture(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        _bounds = bounds;
        try
        {
            InitializeNative();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool Contains(Rectangle bounds)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return bounds.Width > 0 && bounds.Height > 0 && OutputBounds.Contains(bounds);
        }
    }

    public Bitmap Capture(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            long acquireStart = Stopwatch.GetTimestamp();
            ReleaseHeldFrame(throwOnFailure: true);

            bool recovered = false;
            int result;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int elapsedMs = (int)Math.Min(int.MaxValue,
                    Stopwatch.GetElapsedTime(acquireStart).TotalMilliseconds);
                if (!_hasPixels && elapsedMs >= 250)
                {
                    result = DXGI_ERROR_WAIT_TIMEOUT;
                    break;
                }
                int waitMs = _hasPixels ? 0 : Math.Min(25, 250 - elapsedMs);
                result = Acquire(waitMs);
                if (result == DXGI_ERROR_ACCESS_LOST)
                {
                    if (recovered)
                        HResult(result, "IDXGIOutputDuplication.AcquireNextFrame after recovery");
                    recovered = true;
                    InitializeNative();
                    continue;
                }
                if (result == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    if (_hasPixels) break;
                    continue;
                }
                HResult(result, "IDXGIOutputDuplication.AcquireNextFrame");
                if (_hasPixels || _heldHasDesktopImage) break;

                // A resource without a desktop-present timestamp cannot establish pixel freshness.
                // The 4K qualification observed one such first resource before an exact fresh frame.
                ReleaseHeldFrame(throwOnFailure: true);
            }

            if (result == DXGI_ERROR_WAIT_TIMEOUT)
            {
                if (!_hasPixels)
                    throw new TimeoutException("DXGI produced no fresh first frame within 250 ms.");
                token.ThrowIfCancellationRequested();
                SetSdrWhiteScale(GetSdrWhiteScale(_outputDesc.DeviceName));
                token.ThrowIfCancellationRequested();
                return ReadStaging(token);
            }

            if (!_hasPixels || _heldHasDesktopImage)
            {
                CopyHeldFrameToStaging();
                _hasPixels = true;
            }
            token.ThrowIfCancellationRequested();
            SetSdrWhiteScale(GetSdrWhiteScale(_outputDesc.DeviceName));
            token.ThrowIfCancellationRequested();
            return ReadStaging(token);
        }
    }

    public void ResizeCrop(Rectangle bounds)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(bounds));
            ValidateBounds(bounds, _outputDesc);
            if (bounds == _bounds) return;
            ReleaseCom(ref _staging);
            _bounds = bounds;
            CreateStaging();
            if (_frameHeld && _heldTexture != 0 && _heldHasDesktopImage)
            {
                CopyHeldFrameToStaging();
                _hasPixels = true;
            }
            else
            {
                _hasPixels = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ReleaseNativeState();
            _scRgbToByte = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private FrameInfo _lastFrameInfo;

    private int Acquire(int waitMs)
    {
        nint resource = 0;
        _lastFrameInfo = default;
        int result = Method<AcquireNextFrameDelegate>(_duplication, 8)(
            _duplication, checked((uint)Math.Max(0, waitMs)), out _lastFrameInfo, out resource);
        if (result != S_OK)
        {
            if (resource != 0) ReleaseCom(ref resource);
            return result;
        }

        _frameHeld = true;
        _heldHasDesktopImage = _lastFrameInfo.LastPresentTime != 0;
        int qi = QueryInterface(resource, IidTexture2D, out _heldTexture);
        ReleaseCom(ref resource);
        if (qi < 0)
        {
            ReleaseHeldFrame(throwOnFailure: false);
            HResult(qi, "IDXGIResource.QueryInterface(ID3D11Texture2D)");
        }
        return S_OK;
    }

    private void CopyHeldFrameToStaging()
    {
        if (!_frameHeld || _heldTexture == 0)
            throw new InvalidOperationException("No acquired desktop texture is available.");
        var output = _outputDesc.DesktopCoordinates.ToRectangle();
        Method<GetTextureDescDelegate>(_heldTexture, 10)(_heldTexture, out var sourceDesc);
        Method<GetTextureDescDelegate>(_staging, 10)(_staging, out var stagingDesc);
        if (sourceDesc.Format != DXGI_FORMAT_R16G16B16A16_FLOAT || sourceDesc.SampleDesc.Count != 1)
            throw new NotSupportedException(
                $"Desktop texture format {sourceDesc.Format} is not FP16 scRGB.");
        if (stagingDesc.Width != (uint)_bounds.Width || stagingDesc.Height != (uint)_bounds.Height
            || stagingDesc.Format != sourceDesc.Format)
            throw new InvalidOperationException(
                $"Unexpected texture descriptors: source={sourceDesc.Width}x{sourceDesc.Height}/fmt{sourceDesc.Format}/samples{sourceDesc.SampleDesc.Count}, " +
                $"staging={stagingDesc.Width}x{stagingDesc.Height}/fmt{stagingDesc.Format}.");
        var source = new D3D11Box
        {
            Left = checked((uint)(_bounds.Left - output.Left)),
            Top = checked((uint)(_bounds.Top - output.Top)),
            Front = 0,
            Right = checked((uint)(_bounds.Right - output.Left)),
            Bottom = checked((uint)(_bounds.Bottom - output.Top)),
            Back = 1
        };
        if (source.Right > sourceDesc.Width || source.Bottom > sourceDesc.Height)
            throw new InvalidOperationException(
                $"Crop box [{source.Left},{source.Top},{source.Right},{source.Bottom}] exceeds source texture {sourceDesc.Width}x{sourceDesc.Height}.");
        Method<CopySubresourceRegionDelegate>(_context, 46)(
            _context, _staging, 0, 0, 0, 0, _heldTexture, 0, ref source);
    }

    private Bitmap ReadStaging(CancellationToken token)
    {
        int result = Method<MapDelegate>(_context, 14)(
            _context, _staging, 0, D3D11_MAP_READ, 0, out var mapped);
        HResult(result, "ID3D11DeviceContext.Map");
        try
        {
            var bitmap = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
            BitmapData? data = null;
            try
            {
                data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                int rowBytes = checked(_bounds.Width * 4);
                byte[] convert = _scRgbToByte
                    ?? throw new InvalidOperationException("scRGB lookup table was not initialized.");
                var halfRow = new short[checked(_bounds.Width * 4)];
                var bgraRow = new byte[rowBytes];
                for (int y = 0; y < _bounds.Height; y++)
                {
                    if ((y & 31) == 0) token.ThrowIfCancellationRequested();
                    Marshal.Copy(mapped.Data + checked((int)(y * mapped.RowPitch)), halfRow, 0, halfRow.Length);
                    for (int x = 0; x < _bounds.Width; x++)
                    {
                        int input = x * 4;
                        int output = x * 4;
                        bgraRow[output] = convert[unchecked((ushort)halfRow[input + 2])];
                        bgraRow[output + 1] = convert[unchecked((ushort)halfRow[input + 1])];
                        bgraRow[output + 2] = convert[unchecked((ushort)halfRow[input])];
                        bgraRow[output + 3] = 255;
                    }
                    Marshal.Copy(bgraRow, 0, data.Scan0 + y * data.Stride, bgraRow.Length);
                }
                bitmap.UnlockBits(data);
                data = null;
                token.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch
            {
                if (data is not null) bitmap.UnlockBits(data);
                bitmap.Dispose();
                throw;
            }
        }
        finally
        {
            Method<UnmapDelegate>(_context, 15)(_context, _staging, 0);
        }
    }

    private void SelectSingleOutput(Rectangle bounds)
    {
        nint factory = 0;
        int factoryResult = CreateDXGIFactory1(IidFactory1, out factory);
        if (factoryResult < 0)
        {
            ReleaseCom(ref factory);
            HResult(factoryResult, "CreateDXGIFactory1");
        }
        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                nint adapter = 0;
                int adapterResult = Method<EnumAdapters1Delegate>(factory, 12)(factory, adapterIndex, out adapter);
                if (adapterResult == DXGI_ERROR_NOT_FOUND)
                {
                    ReleaseCom(ref adapter);
                    break;
                }
                if (adapterResult < 0)
                {
                    ReleaseCom(ref adapter);
                    HResult(adapterResult, "IDXGIFactory1.EnumAdapters1");
                }
                bool keepAdapter = false;
                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        nint output = 0;
                        int outputResult = Method<EnumOutputsDelegate>(adapter, 7)(adapter, outputIndex, out output);
                        if (outputResult == DXGI_ERROR_NOT_FOUND)
                        {
                            ReleaseCom(ref output);
                            break;
                        }
                        if (outputResult < 0)
                        {
                            ReleaseCom(ref output);
                            HResult(outputResult, "IDXGIAdapter.EnumOutputs");
                        }
                        bool keepOutput = false;
                        try
                        {
                            HResult(Method<GetOutputDescDelegate>(output, 7)(output, out var desc),
                                "IDXGIOutput.GetDesc");
                            if (!desc.AttachedToDesktop) continue;
                            var outputBounds = desc.DesktopCoordinates.ToRectangle();
                            if (!outputBounds.IntersectsWith(bounds)) continue;
                            if (!outputBounds.Contains(bounds))
                                throw new NotSupportedException(
                                    $"Capture rectangle {bounds} spans outputs; desktop duplication requires one output ({outputBounds}).");
                            if (_output != 0)
                                throw new NotSupportedException("Capture rectangle intersects more than one output.");
                            _adapter = adapter;
                            _output = output;
                            _outputDesc = desc;
                            keepAdapter = keepOutput = true;
                        }
                        finally
                        {
                            if (!keepOutput) ReleaseCom(ref output);
                        }
                    }
                }
                finally
                {
                    if (!keepAdapter) ReleaseCom(ref adapter);
                }
            }

            if (_output == 0)
                throw new ArgumentOutOfRangeException(nameof(bounds), "Capture rectangle is outside active outputs.");
            ValidateBounds(bounds, _outputDesc);
            HResult(QueryInterface(_output, IidOutput5, out _output5), "IDXGIOutput.QueryInterface(IDXGIOutput5)");
        }
        catch
        {
            ReleaseCom(ref _output5);
            ReleaseCom(ref _output);
            ReleaseCom(ref _adapter);
            throw;
        }
        finally
        {
            ReleaseCom(ref factory);
        }
    }

    private void CreateDeviceAndDuplication()
    {
        int result = D3D11CreateDevice(_adapter, D3D_DRIVER_TYPE_UNKNOWN, 0,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0, 0, D3D11_SDK_VERSION,
            out _device, out _, out _context);
        HResult(result, "D3D11CreateDevice");
        CreateDuplication();
    }

    private void InitializeNative()
    {
        ReleaseNativeState();
        try
        {
            SelectSingleOutput(_bounds);
            CreateDeviceAndDuplication();
            SetSdrWhiteScale(GetSdrWhiteScale(_outputDesc.DeviceName));
            CreateStaging();
            _hasPixels = false;
        }
        catch
        {
            ReleaseNativeState();
            throw;
        }
    }

    private void CreateDuplication()
    {
        // Although DXGI recommends BGRA8, this display's measured native conversion was
        // overbright. FP16 scRGB plus the reported SDR white level passed the exact-pixel gate.
        int format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        int result = Method<DuplicateOutput1Delegate>(_output5, 26)(
            _output5, _device, 0, 1, ref format, out _duplication);
        HResult(result, "IDXGIOutput5.DuplicateOutput1(FP16 scRGB)");
    }

    private void ReleaseNativeState()
    {
        ReleaseHeldFrame(throwOnFailure: false);
        ReleaseCom(ref _staging);
        ReleaseCom(ref _duplication);
        ReleaseCom(ref _context);
        ReleaseCom(ref _device);
        ReleaseCom(ref _output5);
        ReleaseCom(ref _output);
        ReleaseCom(ref _adapter);
        _outputDesc = default;
        _hasPixels = false;
    }

    private void CreateStaging()
    {
        var desc = new Texture2DDesc
        {
            Width = checked((uint)_bounds.Width),
            Height = checked((uint)_bounds.Height),
            MipLevels = 1,
            ArraySize = 1,
            Format = _stagingFormat,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CpuAccessFlags = D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };
        int result = Method<CreateTexture2DDelegate>(_device, 5)(_device, ref desc, 0, out _staging);
        HResult(result, "ID3D11Device.CreateTexture2D");
    }

    private void ReleaseHeldFrame(bool throwOnFailure)
    {
        ReleaseCom(ref _heldTexture);
        if (!_frameHeld) return;
        _frameHeld = false;
        _heldHasDesktopImage = false;
        int result = Method<ReleaseFrameDelegate>(_duplication, 14)(_duplication);
        if (throwOnFailure && result < 0 && result != DXGI_ERROR_ACCESS_LOST)
            HResult(result, "IDXGIOutputDuplication.ReleaseFrame");
    }

    private static void ValidateBounds(Rectangle bounds, OutputDesc output)
    {
        var outputBounds = output.DesktopCoordinates.ToRectangle();
        if (!output.AttachedToDesktop || !outputBounds.Contains(bounds))
            throw new NotSupportedException($"Capture rectangle {bounds} must stay inside output {outputBounds}.");
        if (output.Rotation != DXGI_MODE_ROTATION_IDENTITY)
            throw new NotSupportedException($"Rotated outputs are not supported (rotation={output.Rotation}).");
    }

    private void SetSdrWhiteScale(float scale)
    {
        if (_scRgbToByte is not null && _sdrWhiteScale == scale) return;
        _sdrWhiteScale = scale;
        var lookup = new byte[ushort.MaxValue + 1];
        for (int bits = 0; bits < lookup.Length; bits++)
            lookup[bits] = ScRgbToByte(unchecked((short)(ushort)bits), scale);
        _scRgbToByte = lookup;
    }

    private static byte ScRgbToByte(short bits, float scale)
    {
        float linear = (float)BitConverter.UInt16BitsToHalf(unchecked((ushort)bits));
        if (!float.IsFinite(linear)) linear = 0;
        linear = Math.Clamp(linear / scale, 0, 1);
        float encoded = linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate
    {
        if (instance == 0) throw new ObjectDisposedException(typeof(T).Name);
        nint table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }

    private static float GetSdrWhiteScale(string gdiDeviceName)
    {
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int result = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (result != 0) ThrowWin32(result, "GetDisplayConfigBufferSizes");
            var paths = new DisplayConfigPathInfo[pathCount];
            nint modes = Marshal.AllocHGlobal(checked((int)modeCount * 256));
            try
            {
                result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths,
                    ref modeCount, modes, 0);
                if (result == 122) continue;
                if (result != 0) ThrowWin32(result, "QueryDisplayConfig");
                for (int index = 0; index < pathCount; index++)
                {
                    var path = paths[index];
                    var source = new DisplayConfigSourceDeviceName
                    {
                        Header = new DisplayConfigDeviceInfoHeader
                        {
                            Type = 1,
                            Size = checked((uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>()),
                            AdapterId = path.SourceInfo.AdapterId,
                            Id = path.SourceInfo.Id
                        },
                        ViewGdiDeviceName = string.Empty
                    };
                    if (DisplayConfigGetSourceName(ref source) != 0
                        || !string.Equals(source.ViewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var white = new DisplayConfigSdrWhiteLevel
                    {
                        Header = new DisplayConfigDeviceInfoHeader
                        {
                            Type = 11,
                            Size = checked((uint)Marshal.SizeOf<DisplayConfigSdrWhiteLevel>()),
                            AdapterId = path.TargetInfo.AdapterId,
                            Id = path.TargetInfo.Id
                        }
                    };
                    result = DisplayConfigGetSdrWhiteLevel(ref white);
                    if (result != 0) ThrowWin32(result, "DisplayConfigGetDeviceInfo(SDR white level)");
                    if (white.SdrWhiteLevel == 0)
                        throw new NotSupportedException("The output reported an invalid zero SDR white level.");
                    return white.SdrWhiteLevel / 1000f;
                }
                throw new NotSupportedException($"No active display path matched {gdiDeviceName}.");
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        throw new COMException("Display configuration changed repeatedly while querying SDR white level.",
            unchecked((int)0x8007007A));
    }

    private static void ThrowWin32(int result, string operation)
    {
        int hresult = unchecked((int)(0x80070000u | (uint)(result & 0xFFFF)));
        throw new COMException($"{operation} failed (Win32 {result}).", hresult);
    }

    private static int QueryInterface(nint instance, Guid iid, out nint result) =>
        Method<QueryInterfaceDelegate>(instance, 0)(instance, ref iid, out result);

    private static void ReleaseCom(ref nint instance)
    {
        if (instance == 0) return;
        Method<ReleaseDelegate>(instance, 2)(instance);
        instance = 0;
    }

    private static void HResult(int result, string operation)
    {
        if (result < 0) throw new COMException($"{operation} failed (0x{result:X8}).", result);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software,
        uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion,
        out nint device, out int featureLevel, out nint immediateContext);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths, ref uint modeCount, nint modes, nint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetSourceName(ref DisplayConfigSourceDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetSdrWhiteLevel(ref DisplayConfigSdrWhiteLevel request);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceDelegate(nint self, ref Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseDelegate(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1Delegate(nint self, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputsDelegate(nint self, uint index, out nint output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescDelegate(nint self, out OutputDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutput1Delegate(nint self, nint device,
        uint flags, uint supportedFormatsCount, ref int supportedFormats, out nint duplication);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireNextFrameDelegate(nint self, uint timeoutMs, out FrameInfo info, out nint resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFrameDelegate(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DDelegate(nint self, ref Texture2DDesc desc, nint initialData, out nint texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetTextureDescDelegate(nint self, out Texture2DDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapDelegate(nint self, nint resource, uint subresource, int mapType, uint flags, out MappedSubresource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapDelegate(nint self, nint resource, uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopySubresourceRegionDelegate(nint self, nint destination, uint destinationSubresource,
        uint destinationX, uint destinationY, uint destinationZ, nint source, uint sourceSubresource, ref D3D11Box sourceBox);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public NativeRect DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPosition
    {
        public PointNative Position;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        [MarshalAs(UnmanagedType.Bool)] public bool RectsCoalesced;
        [MarshalAs(UnmanagedType.Bool)] public bool ProtectedContentMaskedOut;
        public PointerPosition PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleDesc { public uint Count, Quality; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public SampleDesc SampleDesc;
        public int Usage;
        public uint BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public nint Data;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id, ModeInfoIdx, StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id, ModeInfoIdx;
        public int OutputTechnology, Rotation, Scaling;
        public DisplayConfigRational RefreshRate;
        public int ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type, Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSdrWhiteLevel
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }
}
