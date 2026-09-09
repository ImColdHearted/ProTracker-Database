using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Foot_Tracker.Tracking;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §141. Plays an animated GIF as the app background.
    ///
    /// Avalonia's ImageBrush shows one still image, so a GIF chosen in the
    /// Appearance window would otherwise sit on its first frame forever. This
    /// decodes the GIF's frames with SkiaSharp (SKCodec, frame by frame, each
    /// composited onto the previous one the way the format requires), turns
    /// each into an Avalonia bitmap on a background thread, and then swaps the
    /// brush's Source on a UI-thread timer at the GIF's own frame timings.
    /// ThemeManager hands over the very ImageBrush it put into
    /// Application.Resources as ThemeBackgroundBrush, and every window binds
    /// that one instance, so they all animate together and no window has to
    /// know that its background moves.
    ///
    /// Two budgets keep a careless GIF from eating memory: at most MaxFrames
    /// frames (a longer GIF keeps every n-th frame, each holding the screen for
    /// the frames it replaced), and at most PixelBudget pixels across the kept
    /// frames (a GIF over that is scaled down, since a background behind the
    /// UI does not need full resolution). The first frame is on screen at once
    /// through ThemeManager's ordinary still-image path; the animation starts
    /// when decoding finishes, a second or two later for a large file.
    /// </summary>
    public static class AnimatedBackgroundService
    {
        public const int MaxFrames = 200;

        /// <summary>Kept-frame pixels in total - 40 million is about 160 MB
        /// of BGRA, the size of forty 1000x1000 frames or two hundred
        /// 500x400 ones.</summary>
        public const long PixelBudget = 40_000_000;

        /// <summary>GIF frame delays are in centiseconds and a delay of 0 or 1
        /// means "as fast as you can", which every browser shows at 100 ms.
        /// Same rule here, and nothing faster than 20 ms either way.</summary>
        public const int DefaultFrameMs = 100;
        public const int MinFrameMs = 20;

        /// <summary>How long a retired frame set is kept alive after Stop:
        /// a window can paint the old brush once more before its
        /// DynamicResource re-resolves, and a disposed bitmap must not be what
        /// it finds.</summary>
        public static readonly TimeSpan RetireDelay = TimeSpan.FromSeconds(5);

        internal sealed class Frame
        {
            public Bitmap Image { get; init; } = null!;
            public int DurationMs { get; init; }
        }

        private static int generation;
        private static DispatcherTimer? timer;
        private static ImageBrush? brush;
        private static List<Frame> frames = new();
        private static int index;

        public static bool IsGif(string? path) =>
            !string.IsNullOrWhiteSpace(path) &&
            path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

        public static bool IsPlaying => timer is { IsEnabled: true };

        /// <summary>Stops whatever is playing. Safe to call when nothing is;
        /// ThemeManager calls it on every Apply so the previous appearance's
        /// GIF never outlives the brush it belonged to. Must run on the UI
        /// thread, like everything else that touches the brush.</summary>
        public static void Stop()
        {
            generation++;

            timer?.Stop();
            timer = null;
            brush = null;

            List<Frame> retired = frames;
            frames = new List<Frame>();
            index = 0;

            if (retired.Count > 0)
                DispatcherTimer.RunOnce(() => DisposeFrames(retired), RetireDelay);
        }

        /// <summary>Starts playing <paramref name="gifPath"/> into
        /// <paramref name="target"/>, whose Source already shows the first
        /// frame. Decoding runs off the UI thread; if the appearance changes
        /// again before it finishes, the late result is discarded unused.</summary>
        public static void Start(ImageBrush target, string gifPath)
        {
            Stop();

            int myGeneration = generation;
            brush = target;

            _ = Task.Run(() =>
            {
                List<Frame>? decoded = null;

                try
                {
                    decoded = DecodeFrames(gifPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "GIF background: could not decode {Path} - showing its first frame only", gifPath);
                }

                Dispatcher.UIThread.Post(() => Begin(myGeneration, target, decoded, gifPath));
            });
        }

        private static void Begin(int myGeneration, ImageBrush target, List<Frame>? decoded, string gifPath)
        {
            bool stale = myGeneration != generation || !ReferenceEquals(target, brush);

            if (decoded is null || decoded.Count < 2 || stale)
            {
                if (decoded is not null)
                    DisposeFrames(decoded);

                if (!stale && decoded is not null)
                    Log.Information("GIF background: {Path} has a single frame - shown as a still image", gifPath);

                return;
            }

            frames = decoded;
            index = 0;

            timer = new DispatcherTimer { Interval = Interval(frames[0].DurationMs) };
            timer.Tick += (_, _) => Advance();
            timer.Start();

            Log.Information(
                "GIF background playing: {Frames} frames at {Width}x{Height} from {Path}",
                frames.Count, frames[0].Image.PixelSize.Width, frames[0].Image.PixelSize.Height, gifPath);
        }

        private static void Advance()
        {
            if (brush is null || frames.Count == 0 || timer is null)
                return;

            index = (index + 1) % frames.Count;
            brush.Source = frames[index].Image;
            timer.Interval = Interval(frames[index].DurationMs);
        }

        internal static TimeSpan Interval(int durationMs) =>
            TimeSpan.FromMilliseconds(durationMs <= 10 ? DefaultFrameMs : Math.Max(MinFrameMs, durationMs));

        private static void DisposeFrames(List<Frame> set)
        {
            foreach (Frame frame in set)
            {
                try
                {
                    frame.Image.Dispose();
                }
                catch
                {
                    // A bitmap that will not dispose is not worth a crash.
                }
            }
        }

        /// <summary>Which of <paramref name="frameCount"/> frames are kept
        /// (every <c>step</c>-th) and how long each kept frame stays on
        /// screen: its own delay plus the delays of the frames it replaces,
        /// so a thinned GIF still runs at the same overall speed.</summary>
        internal static (int Step, int[] KeptDurations) PlanFrames(int[] durations)
        {
            int frameCount = durations.Length;
            int step = Math.Max(1, (int)Math.Ceiling(frameCount / (double)MaxFrames));
            int kept = (frameCount + step - 1) / step;
            var keptDurations = new int[kept];

            for (int i = 0; i < frameCount; i++)
                keptDurations[i / step] += Math.Max(0, durations[i]);

            return (step, keptDurations);
        }

        /// <summary>The scale that fits <paramref name="keptFrames"/> frames of
        /// <paramref name="width"/> x <paramref name="height"/> into
        /// PixelBudget - 1.0 when they already fit.</summary>
        internal static double BudgetScale(int width, int height, int keptFrames) =>
            Math.Min(1.0, Math.Sqrt(PixelBudget / ((double)width * height * Math.Max(1, keptFrames))));

        /// <summary>Decodes the GIF into ready-to-show frames. Runs on a
        /// background thread. Returns fewer than two frames for anything that
        /// is not an animation; a frame that will not decode ends the list
        /// there (the frames before it still play) rather than throwing.</summary>
        internal static List<Frame> DecodeFrames(string gifPath)
        {
            var result = new List<Frame>();

            using FileStream stream = File.OpenRead(gifPath);
            using SKCodec? codec = SKCodec.Create(stream);

            if (codec is null)
                return result;

            int frameCount = codec.FrameCount;

            if (frameCount < 2)
                return result;

            SKCodecFrameInfo[] frameInfos = codec.FrameInfo;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            if (info.Width <= 0 || info.Height <= 0)
                return result;

            var durations = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
                durations[i] = frameInfos[i].Duration;

            (int step, int[] keptDurations) = PlanFrames(durations);
            double scale = BudgetScale(info.Width, info.Height, keptDurations.Length);
            int outWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
            int outHeight = Math.Max(1, (int)Math.Round(info.Height * scale));

            // One canvas the whole way through: frame N is decoded on top of
            // frame N-1 exactly as the GIF intends, which is also what lets
            // Skia decode each frame once instead of rebuilding the chain.
            using var canvas = new SKBitmap(info);
            IntPtr pixels = canvas.GetPixels();

            for (int i = 0; i < frameCount; i++)
            {
                // The canvas holds the previous frame, so say so - unless that
                // frame's disposal is "restore previous", which Skia rejects as
                // a starting point; it then rebuilds from the frame it needs.
                int priorFrame = i - 1;

                if (priorFrame >= 0 &&
                    frameInfos[priorFrame].DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious)
                {
                    priorFrame = -1;
                }

                var options = new SKCodecOptions(i, priorFrame);
                SKCodecResult status = codec.GetPixels(info, pixels, options);

                if (status != SKCodecResult.Success && status != SKCodecResult.IncompleteInput)
                {
                    Log.Warning(
                        "GIF background: frame {Frame} of {Path} did not decode ({Status}) - playing the {Kept} frames before it",
                        i, gifPath, status, result.Count);
                    break;
                }

                if (i % step != 0)
                    continue;

                using SKBitmap frame = scale < 1.0
                    ? ImageOps.ResizeBilinear(canvas, outWidth, outHeight)
                    : canvas.Copy();

                using var png = new MemoryStream(ImageOps.EncodePng(frame));

                result.Add(new Frame
                {
                    Image = new Bitmap(png),
                    DurationMs = keptDurations[i / step]
                });
            }

            return result;
        }
    }
}
