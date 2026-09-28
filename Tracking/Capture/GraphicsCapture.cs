using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;
#if WINDOWS
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage.Streams;
using WinRT;
#endif

namespace Foot_Tracker.Tracking.Capture;

/// <summary>
/// §257. Windows Graphics Capture: the PRO client's window read through the
/// compositor instead of through the window itself.
///
/// WHY. Players are told by PRO's own staff to run the game as
/// administrator. An elevated window is a higher-integrity window, and
/// Windows refuses to let a normal process send it messages - which is what
/// PrintWindow (ScreenCapture.cs) is underneath. So for those players the
/// tracker captured nothing, or black, unless it was elevated too, and "run
/// the tracker as administrator" became the folk fix. This API has no such
/// rule: the desktop compositor already holds every window's pixels and
/// hands them out to any process that asks, elevated target or not,
/// occluded or not. It is what OBS uses for its window capture.
///
/// WHEN. Only after PrintWindow has failed or painted black for a window,
/// and then for that window from then on - see ScreenCapture.CaptureProWindow.
/// A client that is not elevated never comes here and sees no change.
///
/// THE BORDER. Windows 10 draws a thin yellow border around any window being
/// captured this way and offers no way to turn it off; Windows 11 does
/// (IsBorderRequired, behind RequestAccessAsync), and it is turned off there.
/// The decision to accept the Windows 10 border was made explicitly: a
/// border around a game that is being tracked beats a tracker that needs
/// administrator.
///
/// HOW. One Direct3D device for the process, one capture session for the
/// window in use, frames copied to a SoftwareBitmap and encoded as PNG -
/// which is what IWindowCaptureService hands out anyway, and which keeps the
/// GPU-side work inside two documented WinRT calls rather than hand-written
/// Direct3D interop. The two unavoidable interop points, creating the WinRT
/// device from a DXGI one and creating the capture item from a window
/// handle, are the same two every Windows Graphics Capture sample uses.
///
/// Compiled only into the Windows build (the -windows target framework, which
/// carries the SDK projections); every other build gets the stub at the foot
/// of the file, which reports itself unavailable and lets PrintWindow's
/// screen-rectangle fallback carry on as before.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GraphicsCapture
{
#if WINDOWS
    /// <summary>Windows 10 version 1903, the first with the API.</summary>
    private const int MinimumBuild = 18362;

    /// <summary>How long one capture waits for the compositor to deliver a
    /// frame. A running game redraws far faster than this; the wait only
    /// matters for the first frame of a session, and for a window that has
    /// genuinely not changed - which then gets the previous frame back,
    /// because the previous frame IS what is on it.</summary>
    private static readonly TimeSpan FrameWait = TimeSpan.FromMilliseconds(400);

    /// <summary>After this many failures in a row the API is not tried again
    /// this run. Every capture would otherwise pay for a broken device or a
    /// missing feature several times a second.</summary>
    private const int GiveUpAfterFailures = 5;

    private static readonly object Gate = new();
    private static bool? supported;
    private static string unavailableReason = string.Empty;
    private static IDirect3DDevice? device;
    private static WindowSession? session;
    private static int consecutiveFailures;
    private static string lastFailure = string.Empty;
    private static byte[]? lastPng;
    private static IntPtr lastPngHandle;
    private static bool lastSessionBorderless;

    /// <summary>A session left open after the hunt stops would keep the
    /// Windows 10 border on the game for as long as the tracker ran. Ten
    /// seconds without a capture and the session goes; the next capture
    /// starts a new one.</summary>
    private static readonly TimeSpan IdleRelease = TimeSpan.FromSeconds(10);
    private static Timer? idleTimer;
    private static DateTime lastCaptureUtc;

    /// <summary>True on Windows 10 1903 or later where Windows says the API
    /// works. Cheap after the first call.</summary>
    public static bool IsSupported
    {
        get { lock (Gate) return Probe(); }
    }

    /// <summary>Why IsSupported is false, in a sentence fit for a log line.</summary>
    public static string UnavailableReason
    {
        get { lock (Gate) { Probe(); return unavailableReason; } }
    }

    /// <summary>One sentence for the log the first time this is in use: on
    /// Windows 10 the border is Windows marking the capture, not a fault.</summary>
    public static string BorderNote =>
        lastSessionBorderless
            ? string.Empty
            : " Windows draws a thin yellow border around the client while it is captured this way; that is Windows marking the capture, not a fault, and Windows 11 lets it be turned off (it is).";

    /// <summary>
    /// One frame of <paramref name="handle"/> as a PNG, or null with
    /// <paramref name="failure"/> saying why. The first call for a window
    /// starts its session; later calls reuse it. A window that has not
    /// redrawn since the last call gets the last frame again.
    /// </summary>
    public static byte[]? CapturePng(IntPtr handle, out string? failure)
    {
        failure = null;

        lock (Gate)
        {
            if (!Probe())
            {
                failure = unavailableReason;
                return null;
            }

            if (consecutiveFailures >= GiveUpAfterFailures)
            {
                failure = $"not tried again this run after {consecutiveFailures} failures in a row (last: {lastFailure})";
                return null;
            }

            try
            {
                device ??= CreateDevice();

                if (session is null || session.Handle != handle || session.IsClosed)
                {
                    session?.Dispose();
                    session = new WindowSession(device, handle);
                    lastSessionBorderless = session.Borderless;
                }

                byte[]? png = session.CaptureFrame(FrameWait);

                // Idle means no capture ASKED for, not no frame delivered: a
                // game that sits still is still being tracked, and its
                // session (and border) must not come and go every ten seconds.
                lastCaptureUtc = DateTime.UtcNow;

                if (png is null)
                {
                    if (lastPng is not null && lastPngHandle == handle)
                        return lastPng;

                    failure = "no frame arrived from the compositor";
                    consecutiveFailures++;
                    lastFailure = failure;
                    return null;
                }

                lastPng = png;
                lastPngHandle = handle;
                consecutiveFailures = 0;
                idleTimer ??= new Timer(_ => ReleaseIfIdle(), null, IdleRelease, IdleRelease);
                return png;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                lastFailure = ex.Message;
                failure = ex.Message;

                Log.Warning(ex, "Windows Graphics Capture failed ({Count} in a row)", consecutiveFailures);

                session?.Dispose();
                session = null;

                if (consecutiveFailures >= GiveUpAfterFailures)
                {
                    Log.Warning("Windows Graphics Capture is not being tried again this run - PrintWindow and the screen-rectangle copy carry on.");
                    device = null;
                }

                return null;
            }
        }
    }

    /// <summary>Ends the session, if any; the next capture starts afresh.
    /// The border goes with it.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            session?.Dispose();
            session = null;
            lastPng = null;
            lastPngHandle = IntPtr.Zero;
        }
    }

    private static void ReleaseIfIdle()
    {
        lock (Gate)
        {
            if (session is null || DateTime.UtcNow - lastCaptureUtc < IdleRelease)
                return;

            Log.Debug("Windows Graphics Capture: session released after {Seconds}s without a capture", (int)IdleRelease.TotalSeconds);
            session.Dispose();
            session = null;
            lastPng = null;
            lastPngHandle = IntPtr.Zero;
        }
    }

    private static bool Probe()
    {
        if (supported is { } known)
            return known;

        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild))
            {
                unavailableReason = "needs Windows 10 version 1903 or later";
                supported = false;
            }
            else if (!GraphicsCaptureSession.IsSupported())
            {
                unavailableReason = "Windows reports Graphics Capture as unsupported on this machine";
                supported = false;
            }
            else
            {
                supported = true;
            }
        }
        catch (Exception ex)
        {
            unavailableReason = "Windows Graphics Capture could not be reached: " + ex.Message;
            supported = false;
        }

        return supported.Value;
    }

    // ------------------------------------------------------------ device

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount,
        uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>IUnknown::QueryInterface, called through the object's own
    /// vtable so no assumption is made about Marshal.QueryInterface's
    /// signature, which has changed between .NET releases.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr result);

    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    /// <summary>A Direct3D 11 device the compositor can render captured
    /// frames into. Hardware first, WARP (software) if there is none - a
    /// remote desktop session, say - because a slow capture beats none.</summary>
    private static IDirect3DDevice CreateDevice()
    {
        const uint HardwareDriver = 1;
        const uint WarpDriver = 5;
        const uint BgraSupport = 0x20;
        const uint SdkVersion = 7;

        int hr = D3D11CreateDevice(IntPtr.Zero, HardwareDriver, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion,
            out IntPtr d3d, out _, out IntPtr context);

        if (hr < 0)
        {
            hr = D3D11CreateDevice(IntPtr.Zero, WarpDriver, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion,
                out d3d, out _, out context);
        }

        Marshal.ThrowExceptionForHR(hr);

        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(d3d);
            var queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(Marshal.ReadIntPtr(vtable, 0));
            Guid iid = DxgiDeviceIid;

            Marshal.ThrowExceptionForHR(queryInterface(d3d, ref iid, out IntPtr dxgi));

            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr inspectable));

                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            if (context != IntPtr.Zero)
                Marshal.Release(context);

            Marshal.Release(d3d);
        }
    }

    // ------------------------------------------------------------ item

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>The capture item for a window handle, through the interop
    /// interface on the class's activation factory - the documented route
    /// for a desktop app, which has no picker to hand it one.</summary>
    private static GraphicsCaptureItem CreateItem(IntPtr handle)
    {
        IGraphicsCaptureItemInterop interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr abi = interop.CreateForWindow(handle, ref iid);

        try
        {
            return GraphicsCaptureItem.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    // ------------------------------------------------------------ session

    /// <summary>One window's live capture: item, frame pool, session. Frames
    /// arrive on the compositor's schedule (free-threaded, so no dispatcher
    /// is needed on the capturing thread); CaptureFrame takes the newest
    /// one waiting, or waits briefly for the next.</summary>
    private sealed class WindowSession : IDisposable
    {
        private const int PoolFrames = 2;

        private readonly IDirect3DDevice device;
        private readonly GraphicsCaptureItem item;
        private readonly Direct3D11CaptureFramePool framePool;
        private readonly GraphicsCaptureSession captureSession;
        private readonly ManualResetEventSlim frameArrived = new(false);
        private SizeInt32 poolSize;

        public IntPtr Handle { get; }

        /// <summary>Set by the item's Closed event - the window went away.</summary>
        public bool IsClosed { get; private set; }

        public bool Borderless { get; }

        public WindowSession(IDirect3DDevice device, IntPtr handle)
        {
            this.device = device;
            Handle = handle;

            item = CreateItem(handle);
            poolSize = item.Size;

            if (poolSize.Width <= 0 || poolSize.Height <= 0)
                throw new InvalidOperationException("the client window has no size to capture - it may be minimized");

            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolFrames, poolSize);
            framePool.FrameArrived += (_, _) =>
            {
                // A frame can land while Dispose is running; an exception
                // thrown back into the compositor's callback would take the
                // process down for a race that means nothing.
                try { frameArrived.Set(); } catch (ObjectDisposedException) { }
            };
            item.Closed += (_, _) => IsClosed = true;

            captureSession = framePool.CreateCaptureSession(item);
            Borderless = TryDisableBorder(captureSession);
            TryDisableCursor(captureSession);
            captureSession.StartCapture();
        }

        /// <summary>The newest frame as PNG, or null when none arrived inside
        /// <paramref name="wait"/>. A resize is handled by re-making the pool
        /// at the new size and taking the next frame instead - the one in
        /// hand was rendered into the old size.</summary>
        public byte[]? CaptureFrame(TimeSpan wait)
        {
            Direct3D11CaptureFrame? frame = AwaitFrame(wait);

            if (frame is null)
                return null;

            try
            {
                SizeInt32 size = frame.ContentSize;

                if (size.Width != poolSize.Width || size.Height != poolSize.Height)
                {
                    poolSize = size;
                    framePool.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolFrames, size);

                    frame.Dispose();
                    frame = AwaitFrame(wait);

                    if (frame is null)
                        return null;
                }

                return EncodePng(frame.Surface);
            }
            finally
            {
                frame?.Dispose();
            }
        }

        /// <summary>The newest waiting frame, or the next to arrive within
        /// the wait. The reset-then-recheck order closes the gap in which a
        /// frame could land between the first look and the wait.</summary>
        private Direct3D11CaptureFrame? AwaitFrame(TimeSpan wait)
        {
            Direct3D11CaptureFrame? frame = Newest();

            if (frame is not null)
                return frame;

            frameArrived.Reset();
            frame = Newest();

            if (frame is not null)
                return frame;

            frameArrived.Wait(wait);
            return Newest();
        }

        private Direct3D11CaptureFrame? Newest()
        {
            Direct3D11CaptureFrame? newest = null;

            while (true)
            {
                Direct3D11CaptureFrame? next = framePool.TryGetNextFrame();

                if (next is null)
                    break;

                newest?.Dispose();
                newest = next;
            }

            return newest;
        }

        public void Dispose()
        {
            try { captureSession.Dispose(); } catch { /* a dead session is already what we want */ }
            try { framePool.Dispose(); } catch { /* same */ }
            frameArrived.Dispose();
        }
    }

    /// <summary>Windows 11 only: no border around the captured window. On
    /// Windows 10 the property does not exist and the border stays - see the
    /// class remark.</summary>
    private static bool TryDisableBorder(GraphicsCaptureSession captureSession)
    {
        try
        {
            if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                return false;

            if (ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
            {
                AppCapabilityAccessStatus status = Wait(GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless));

                if (status != AppCapabilityAccessStatus.Allowed)
                    return false;
            }

            captureSession.IsBorderRequired = false;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Windows Graphics Capture: the capture border could not be turned off");
            return false;
        }
    }

    /// <summary>The mouse pointer is not part of the game and would only
    /// confuse the OCR. Windows 10 2004 and later.</summary>
    private static void TryDisableCursor(GraphicsCaptureSession captureSession)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                captureSession.IsCursorCaptureEnabled = false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Windows Graphics Capture: the cursor could not be excluded");
        }
    }

    // ------------------------------------------------------------ pixels

    /// <summary>GPU surface to PNG bytes through documented WinRT steps: a
    /// CPU copy of the surface, alpha dropped (the copy carries premultiplied
    /// alpha; the game window is opaque, every encoder takes Bgra8 with alpha
    /// ignored, and the OCR downstream expects the screen, not a
    /// transparency), then the PNG encoder into a memory stream. No Direct3D
    /// staging textures, no unsafe code.</summary>
    private static byte[] EncodePng(IDirect3DSurface surface)
    {
        using SoftwareBitmap copied = Wait(SoftwareBitmap.CreateCopyFromSurfaceAsync(surface));
        using SoftwareBitmap bitmap = SoftwareBitmap.Convert(copied, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using var stream = new InMemoryRandomAccessStream();

        BitmapEncoder encoder = Wait(BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream));
        encoder.SetSoftwareBitmap(bitmap);
        Wait(encoder.FlushAsync());

        stream.Seek(0);

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size = (uint)stream.Size;

        Wait(reader.LoadAsync(size));

        byte[] bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>The capture path is synchronous (IWindowCaptureService) and
    /// runs on worker threads; the WinRT operations complete on the thread
    /// pool, so blocking here cannot deadlock.</summary>
    private static T Wait<T>(IAsyncOperation<T> operation) =>
        operation.AsTask().GetAwaiter().GetResult();

    private static void Wait(IAsyncAction action) =>
        action.AsTask().GetAwaiter().GetResult();
#else
    public static bool IsSupported => false;

    public static string UnavailableReason => "this build was made without the Windows SDK projections";

    public static string BorderNote => string.Empty;

    public static byte[]? CapturePng(IntPtr handle, out string? failure)
    {
        failure = UnavailableReason;
        return null;
    }

    public static void Release()
    {
    }
#endif
}

#if WINDOWS
/// <summary>§257. The activation factory's interop interface for making a
/// capture item from a window handle - IGraphicsCaptureItemInterop in
/// windows.graphics.capture.interop.h. Declared here because the SDK
/// projections do not carry interop interfaces.</summary>
[ComImport]
[System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")] // qualified: Windows.Foundation.Metadata also defines GuidAttribute
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComVisible(true)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}
#endif
