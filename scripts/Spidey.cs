// Spidey overlay: a spider-hero that visits when Claude Code needs you and
// you're looking at some other window.
//   wait   hangs from the top on a web line: Claude needs your OK
//   alert  same, plus spider-sense tingle, shake and a red bubble: risky command
//   done   jumps up from the taskbar and crouches: a long task finished
// Compiled at runtime by spidey.ps1 (Windows PowerShell 5.1 => C# 5 syntax only).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeSpidey
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        public static int WindowPid(IntPtr hWnd)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return (int)pid;
        }

        public static string WindowTitle(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    // Everything the launcher decided, filled in by spidey.ps1 from launch.js's JSON.
    public class Options
    {
        public int[] Chain = new int[0];  // PIDs of the hook process and its ancestors
        public string Mode = "wait";       // wait | alert | done
        public string Title = "", Message = "", Detail = "", Project = "";
        public string SpriteDir = "", Suit = "random", SoundDir = "";
        public int Zoom;                   // 0 = auto from DPI
        public double Fps = 12;
        public bool Sound = true;
        public double Volume = 0.6;
        public bool Always;                // show even if Claude's window has focus (tests)
        public double Timeout = 180;
    }

    // Decides whether a window is "where Claude lives", i.e. the user is already looking at it.
    class Home
    {
        // One process owns every window of these apps, so the PID alone can't tell
        // two windows apart; their titles include the workspace folder name instead.
        static readonly string[] MultiWindowApps = { "code", "code - insiders", "cursor", "windsurf", "vscodium" };

        readonly HashSet<int> pids = new HashSet<int>();
        readonly HashSet<int> multiWindowPids = new HashSet<int>();
        readonly string project;

        public Home(int[] chain, string project)
        {
            this.project = project ?? "";
            foreach (int pid in chain)
            {
                pids.Add(pid);
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        if (Array.IndexOf(MultiWindowApps, p.ProcessName.ToLowerInvariant()) >= 0)
                            multiWindowPids.Add(pid);
                    }
                }
                catch { }
            }
        }

        public bool Contains(IntPtr hWnd)
        {
            int pid = Native.WindowPid(hWnd);
            if (!pids.Contains(pid)) return false;
            if (!multiWindowPids.Contains(pid) || project.Length == 0) return true;
            return TitleMatches(hWnd);
        }

        bool TitleMatches(IntPtr hWnd)
        {
            return Native.WindowTitle(hWnd).IndexOf(project, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The window to bring forward when the user clicks the spider.
        public IntPtr FindWindow()
        {
            IntPtr best = IntPtr.Zero, fallback = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr l)
            {
                if (!Native.IsWindowVisible(hWnd) || !pids.Contains(Native.WindowPid(hWnd))) return true;
                if (Native.WindowTitle(hWnd).Length == 0) return true;
                if (fallback == IntPtr.Zero) fallback = hWnd;
                if (project.Length > 0 && TitleMatches(hWnd)) { best = hWnd; return false; }
                return true;
            }, IntPtr.Zero);
            return best != IntPtr.Zero ? best : fallback;
        }
    }

    public static class Program
    {
        public static void Run(Options o)
        {
            Native.SetProcessDPIAware();
            Home home = new Home(o.Chain, o.Project);
            bool focused = home.Contains(Native.GetForegroundWindow());
            // Spider-sense shows even if you're looking at Claude: it's a warning, not a nudge.
            if (focused && !o.Always && o.Mode != "alert") return;

            // One visitor at a time; a newer visit asks the current one to leave.
            using (Mutex slot = new Mutex(false, @"Local\ClaudeSpidey"))
            using (EventWaitHandle leave = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeSpideyLeave"))
            {
                if (!Acquire(slot, 0))
                {
                    leave.Set();
                    if (!Acquire(slot, 4000)) return;
                }
                try
                {
                    leave.Reset();
                    if (!string.IsNullOrEmpty(o.Project)) o.Message = "[" + o.Project + "]  " + o.Message;
                    if (focused) o.Timeout = Math.Min(o.Timeout, 20);

                    Application.EnableVisualStyles();
                    // Already looking at Claude: that focus must not count as "came back".
                    Application.Run(new SpideyForm(o, focused ? new Home(new int[0], "") : home,
                                                   home.FindWindow(), leave));
                }
                finally
                {
                    slot.ReleaseMutex();
                }
            }
        }

        static bool Acquire(Mutex m, int ms)
        {
            try { return m.WaitOne(ms); }
            catch (AbandonedMutexException) { return true; }
        }
    }

    class Anim
    {
        public readonly Bitmap[] Frames;
        readonly double fps;

        public Anim(Bitmap[] frames, double fps)
        {
            Frames = frames;
            this.fps = fps;
        }

        public bool Empty { get { return Frames.Length == 0; } }
        public double Duration { get { return Frames.Length / fps; } }
        public Bitmap First { get { return Frames[0]; } }
        public Bitmap Once(double t) { return Frames[Math.Max(0, Math.Min((int)(t * fps), Frames.Length - 1))]; }
        public Bitmap Loop(double t) { return Frames[Math.Max(0, (int)(t * fps)) % Frames.Length]; }
    }

    // Sprite animations. SpriteDir holds the frames directly or one subfolder per
    // suit. File names follow the Marvel Cosmic Invasion rip:
    //   wait   Selectscreenstart_*.png (once) then Selectscreen_*.png (loop), head down
    //   alert  Spidersense_*.png  effect drawn around the head
    //   done   Jump, Jumpapex_*, Jumpfall, Land_*, Idle_* (loop)
    // Missing sets fall back to whatever exists; no sprites at all => built-in pixel art.
    class Pack : IDisposable
    {
        public Anim WaitIntro, WaitLoop, Alert, Jump, Apex, Fall, Land, Idle;
        public Point WaitHead, DoneHead;  // head position inside a frame
        public string Dir, Root;           // for per-pack sounds
        readonly Dictionary<Bitmap, int> webX = new Dictionary<Bitmap, int>();

        public Size WaitSize { get { return WaitLoop.First.Size; } }
        public Size DoneSize { get { return Idle.First.Size; } }

        public static Pack Load(string root, string suit, int zoom, double fps)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
                string dir = root;
                if (Directory.GetFiles(root, "Selectscreen_*.png").Length == 0)
                {
                    string[] suits = Array.FindAll(Directory.GetDirectories(root),
                        delegate(string d) { return Directory.GetFiles(d, "Selectscreen_*.png").Length > 0; });
                    if (suits.Length == 0) return null;
                    string chosen = Array.Find(suits, delegate(string d)
                    {
                        return string.Equals(Path.GetFileName(d), suit, StringComparison.OrdinalIgnoreCase);
                    });
                    dir = chosen ?? suits[new Random().Next(suits.Length)];
                }

                Bitmap[][] wait = CropSet(new[] { Read(dir, "Selectscreenstart_*.png"), Read(dir, "Selectscreen_*.png") }, zoom, false, false);
                if (wait == null || wait[1].Length == 0) return null;
                Bitmap[][] alert = CropSet(new[] { Read(dir, "Spidersense_*.png") }, zoom, false, true);
                // Standing frames face right; mirror them so he faces the screen from the right edge.
                Bitmap[][] done = CropSet(new[] {
                    Read(dir, "Jump.png"), Read(dir, "Jumpapex_*.png"), Read(dir, "Jumpfall.png"),
                    Read(dir, "Land_*.png"), Read(dir, "Idle_*.png") }, zoom, true, false);

                Pack p = new Pack();
                p.Dir = dir;
                p.Root = root;
                p.WaitIntro = new Anim(wait[0], fps);
                p.WaitLoop = new Anim(wait[1], fps);
                p.Alert = new Anim(alert != null ? alert[0] : new Bitmap[0], 10);
                if (done != null && done[4].Length > 0)
                {
                    p.Idle = new Anim(done[4], fps);
                    p.Jump = Or(done[0], done[4], fps);
                    p.Apex = Or(done[1], done[4], fps);
                    p.Fall = Or(done[2], done[4], fps);
                    p.Land = new Anim(done[3], fps);
                }
                else
                {
                    // No standing frames: flip the hanging loop upright.
                    Bitmap[] upright = Array.ConvertAll(wait[1], delegate(Bitmap b)
                    {
                        Bitmap c = (Bitmap)b.Clone();
                        c.RotateFlip(RotateFlipType.RotateNoneFlipY);
                        return c;
                    });
                    p.Idle = p.Jump = p.Apex = p.Fall = new Anim(upright, fps);
                    p.Land = new Anim(new Bitmap[0], fps);
                }
                p.Finish();
                return p;
            }
            catch
            {
                return null;
            }
        }

        public static Pack Pixel(Bitmap hanging, Bitmap upright, double fps)
        {
            Pack p = new Pack();
            p.WaitIntro = new Anim(new Bitmap[0], fps);
            p.WaitLoop = new Anim(new[] { hanging }, fps);
            p.Alert = new Anim(new Bitmap[0], 10);
            p.Idle = p.Jump = p.Apex = p.Fall = new Anim(new[] { upright }, fps);
            p.Land = new Anim(new Bitmap[0], fps);
            p.Finish();
            return p;
        }

        static Anim Or(Bitmap[] frames, Bitmap[] fallback, double fps)
        {
            return new Anim(frames.Length > 0 ? frames : fallback, fps);
        }

        void Finish()
        {
            WaitHead = Centroid(WaitLoop.First, 0.82f, 1f);
            DoneHead = Centroid(Idle.First, 0f, 0.25f);
        }

        // Where the web leaves the top of a hanging frame (centre of its topmost opaque pixels).
        public int WebX(Bitmap frame)
        {
            int x;
            if (webX.TryGetValue(frame, out x)) return x;
            x = Centroid(frame, 0f, 0.02f).X;
            webX[frame] = x;
            return x;
        }

        // Centre of the opaque pixels in a horizontal band of the frame (fractions of its height).
        static Point Centroid(Bitmap b, float from, float to)
        {
            int y0 = (int)(b.Height * from), y1 = Math.Max(y0 + 1, (int)(b.Height * to));
            long sx = 0, sy = 0, n = 0;
            for (int y = y0; y < y1 && y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A > 0) { sx += x; sy += y; n++; }
            return n == 0 ? new Point(b.Width / 2, (y0 + y1) / 2) : new Point((int)(sx / n), (int)(sy / n));
        }

        static Bitmap[] Read(string dir, string pattern)
        {
            string[] files = Directory.GetFiles(dir, pattern);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return Array.ConvertAll(files, delegate(string f)
            {
                using (Image img = Image.FromFile(f)) return new Bitmap(img);
            });
        }

        // Crop every frame of every group to their shared opaque bounds (keeps frames
        // aligned), zoom, and optionally mirror. Returns null when nothing is opaque.
        static Bitmap[][] CropSet(Bitmap[][] groups, int zoom, bool flipX, bool flipY)
        {
            Rectangle box = Rectangle.Empty;
            foreach (Bitmap[] group in groups)
                foreach (Bitmap b in group)
                {
                    Rectangle r = OpaqueBounds(b);
                    if (!r.IsEmpty) box = box.IsEmpty ? r : Rectangle.Union(box, r);
                }

            Bitmap[][] result = null;
            if (!box.IsEmpty)
            {
                result = Array.ConvertAll(groups, delegate(Bitmap[] group)
                {
                    return Array.ConvertAll(group, delegate(Bitmap b)
                    {
                        Bitmap c = CropZoom(b, box, zoom);
                        if (flipX || flipY)
                            c.RotateFlip(flipX && flipY ? RotateFlipType.RotateNoneFlipXY
                                         : flipX ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipY);
                        return c;
                    });
                });
            }
            foreach (Bitmap[] group in groups)
                foreach (Bitmap b in group) b.Dispose();
            return result;
        }

        static int[] Pixels(Bitmap b)
        {
            Rectangle all = new Rectangle(0, 0, b.Width, b.Height);
            BitmapData data = b.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int[] px = new int[b.Width * b.Height];
            for (int row = 0; row < b.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, px, row * b.Width, b.Width);
            b.UnlockBits(data);
            return px;
        }

        static Rectangle OpaqueBounds(Bitmap b)
        {
            int[] px = Pixels(b);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int i = 0; i < px.Length; i++)
            {
                if (((px[i] >> 24) & 0xFF) < 128) continue;
                int x = i % b.Width, y = i / b.Width;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        // Nearest-neighbour zoom with hard alpha, so edges never blend with the
        // window's colour-key background.
        static Bitmap CropZoom(Bitmap src, Rectangle box, int zoom)
        {
            int[] px = Pixels(src);
            int w = box.Width * zoom, h = box.Height * zoom;
            int[] outPx = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = px[(box.Y + y / zoom) * src.Width + box.X + x / zoom];
                    outPx[y * w + x] = ((p >> 24) & 0xFF) < 128 ? 0 : (p | unchecked((int)0xFF000000));
                }
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData data = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int row = 0; row < h; row++)
                Marshal.Copy(outPx, row * w, data.Scan0 + row * data.Stride, w);
            dst.UnlockBits(data);
            return dst;
        }

        public void Dispose()
        {
            foreach (Anim a in new[] { WaitIntro, WaitLoop, Alert, Jump, Apex, Fall, Land, Idle })
                if (a != null) foreach (Bitmap b in a.Frames) b.Dispose();
        }
    }

    // Plays thwip.wav / tingle.wav / done.wav, looked up in the sprite pack first
    // (so packs can bring their own), then the plugin's assets.
    static class Sfx
    {
        static SoundPlayer player; // keep a reference while it plays

        public static void Play(Options o, Pack pack, string name)
        {
            if (!o.Sound || o.Volume <= 0) return;
            foreach (string dir in new[] { pack.Dir, pack.Root, o.SoundDir })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    byte[] wav = File.ReadAllBytes(path);
                    ScalePcm16(wav, Math.Min(1, o.Volume));
                    player = new SoundPlayer(new MemoryStream(wav));
                    player.Play();
                }
                catch { }
                return;
            }
        }

        // Volume control for 16-bit PCM WAVs; other formats play at full volume.
        static void ScalePcm16(byte[] wav, double volume)
        {
            if (volume >= 0.999 || wav.Length < 12 || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return;
            bool pcm16 = false;
            for (int pos = 12; pos + 8 <= wav.Length; )
            {
                string id = Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (size < 0) return;
                if (id == "fmt ")
                    pcm16 = BitConverter.ToInt16(wav, pos + 8) == 1 && BitConverter.ToInt16(wav, pos + 22) == 16;
                if (id == "data" && pcm16)
                {
                    int end = Math.Min(wav.Length - 1, pos + 8 + size);
                    for (int i = pos + 8; i + 1 < end; i += 2)
                    {
                        short s = (short)(BitConverter.ToInt16(wav, i) * volume);
                        wav[i] = (byte)s;
                        wav[i + 1] = (byte)(s >> 8);
                    }
                    return;
                }
                pos += 8 + size + (size & 1);
            }
        }
    }

    class SpideyForm : Form
    {
        // Built-in pixel spider, drawn upright.
        static readonly string[] Art = {
            "....KKKKKKKK....",
            "...KRRRRRRRRK...",
            "..KRRDRRRRDRRK..",
            "..KRDKKRRKKDRK..",
            "..KRKWWKKWWKRK..",
            "..KKWWWKKWWWKK..",
            "..KKWWWKKWWWKK..",
            "..KRKKKRRKKKRK..",
            "..KRRDRRRRDRRK..",
            "...KRRDRRDRRK...",
            "....KKRRRRKK....",
            "......KKKK......",
            "...KKKRRRRKKK...",
            "..KBBKRKKRKBBK..",
            ".KBBKRRKKRRKBBK.",
            ".KRKKRKRRKRKKRK.",
            ".KRK.KBBBBK.KRK.",
            "..K..KRRRRK..K..",
            ".....KBBBBK.....",
            "....KBBKKBBK....",
            "....KBBKKBBK....",
            "....KRRKKRRK....",
            "....KKKKKKKK....",
        };

        enum Phase { Enter, Stay, Leave }

        const float Gravity = 0.9f;    // px per 60fps-frame², times DPI scale (jump)
        const float ApexSpeed = 8f;    // below this vertical speed he's "at the top" of the jump

        readonly Options o;
        readonly Home home;
        readonly IntPtr homeWindow;
        readonly EventWaitHandle leaveSignal;
        readonly bool hanging, alert;
        readonly float scale;
        readonly Pack pack;
        readonly int anchorX;   // web anchor (hanging) / body centre (standing)
        readonly float rest;    // resting position: sprite top (hanging) or feet (standing)
        readonly Font titleFont, bodyFont, codeFont;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Stopwatch clock = Stopwatch.StartNew();

        Phase phase = Phase.Enter;
        float y, v;
        double lastStep, stayStart, apexAt = -1;

        public SpideyForm(Options o, Home home, IntPtr homeWindow, EventWaitHandle leaveSignal)
        {
            this.o = o;
            this.home = home;
            this.homeWindow = homeWindow;
            this.leaveSignal = leaveSignal;
            hanging = o.Mode != "done";
            alert = o.Mode == "alert";

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;

            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            // Whole-number zoom keeps every sprite pixel a crisp square.
            int zoom = o.Zoom > 0 ? o.Zoom : Math.Max(1, (int)Math.Round(1.6 * scale));
            int px = Math.Max(4, (int)Math.Round(5 * scale));
            pack = Pack.Load(o.SpriteDir, o.Suit, zoom, o.Fps)
                   ?? Pack.Pixel(BuildSprite(px, true), BuildSprite(px, false), o.Fps);
            titleFont = new Font("Segoe UI", 11f, FontStyle.Bold);
            bodyFont = new Font("Segoe UI", 10f);
            codeFont = new Font("Consolas", 9.5f);

            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = (int)(460 * scale);
            int left = wa.Right - width - (int)(24 * scale);
            if (hanging)
            {
                Size s = pack.WaitSize;
                rest = (int)(wa.Height * 0.28f);
                Bounds = new Rectangle(left, wa.Top, width, (int)(rest + s.Height + 60 * scale));
                anchorX = width - s.Width / 2 - (int)(20 * scale);
                y = -s.Height;
            }
            else
            {
                // Window sits on the taskbar; he leaps up from below its bottom edge.
                Size s = pack.DoneSize;
                int height = s.Height * 2 + (int)(200 * scale);
                Bounds = new Rectangle(left, wa.Bottom - height, width, height);
                anchorX = width - s.Width / 2 - (int)(28 * scale);
                rest = height - 1;
                y = height + s.Height;
                float rise = (y - rest) + 150 * scale;
                v = -(float)Math.Sqrt(2 * Gravity * scale * rise);
            }

            Sfx.Play(o, pack, alert ? "tingle.wav" : "thwip.wav");

            Native.timeBeginPeriod(1); // default ~15.6ms timer granularity makes motion stutter
            timer.Interval = 10;
            timer.Tick += delegate { Step(); };
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x8; // WS_EX_TOOLWINDOW (no alt-tab) | WS_EX_TOPMOST
                return cp;
            }
        }

        static Bitmap BuildSprite(int px, bool headDown)
        {
            int rows = Art.Length, cols = Art[0].Length;
            Bitmap bmp = new Bitmap(cols * px, rows * px);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                for (int r = 0; r < rows; r++)
                {
                    string row = Art[headDown ? rows - 1 - r : r];
                    for (int c = 0; c < cols && c < row.Length; c++)
                    {
                        Color col;
                        switch (row[c])
                        {
                            case 'K': col = Color.FromArgb(17, 17, 17); break;
                            case 'R': col = Color.FromArgb(214, 40, 40); break;
                            case 'D': col = Color.FromArgb(142, 20, 20); break;
                            case 'B': col = Color.FromArgb(31, 79, 184); break;
                            case 'W': col = Color.White; break;
                            default: continue;
                        }
                        using (SolidBrush b = new SolidBrush(col)) g.FillRectangle(b, c * px, r * px, px, px);
                    }
                }
            }
            return bmp;
        }

        double Elapsed { get { return clock.Elapsed.TotalSeconds; } }

        void Step()
        {
            double now = Elapsed;
            if (phase != Phase.Leave &&
                (leaveSignal.WaitOne(0) || home.Contains(Native.GetForegroundWindow()) || now > o.Timeout))
                StartLeave();

            // Physics is tuned in 60fps "frame" units; integrate real elapsed time in
            // small sub-steps so motion stays smooth whatever the timer actually delivers.
            float frames60 = (float)Math.Min((now - lastStep) * 60, 6);
            lastStep = now;
            while (frames60 > 0)
            {
                float f = Math.Min(frames60, 0.25f);
                frames60 -= f;
                if (hanging) StepHang(f, now);
                else StepJump(f, now);
            }

            Size s = hanging ? pack.WaitSize : pack.DoneSize;
            bool gone = hanging ? y + s.Height < 0 : y - s.Height > ClientSize.Height;
            if (phase == Phase.Leave && gone) { timer.Stop(); Close(); return; }
            Invalidate();
        }

        void StepHang(float f, double now)
        {
            if (phase == Phase.Leave)
            {
                v -= 1.6f * scale * f;
                y += v * f;
                return;
            }
            // Springy drop, then a gentle bob while hanging.
            float goal = rest + (phase == Phase.Stay ? (float)Math.Sin(now * 1.8) * 4 * scale : 0);
            v += ((goal - y) * 0.03f - v * 0.10f) * f;
            y += v * f;
            if (phase == Phase.Enter && v < 0) Arrive(now); // first bounce
        }

        void StepJump(float f, double now)
        {
            if (phase == Phase.Stay) return;
            v += Gravity * scale * f;
            y += v * f;
            if (apexAt < 0 && v > -ApexSpeed * scale) apexAt = now;
            if (phase == Phase.Enter && v > 0 && y >= rest)
            {
                y = rest;
                v = 0;
                Arrive(now);
                Sfx.Play(o, pack, "done.wav");
            }
        }

        void Arrive(double now)
        {
            phase = Phase.Stay;
            stayStart = now;
        }

        void StartLeave()
        {
            if (phase == Phase.Leave) return;
            phase = Phase.Leave;
            v = hanging ? -2 * scale : -9 * scale; // zip up / small hop then drop away
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (homeWindow != IntPtr.Zero)
            {
                if (Native.IsIconic(homeWindow)) Native.ShowWindow(homeWindow, 9); // SW_RESTORE
                Native.SetForegroundWindow(homeWindow);
            }
            StartLeave();
        }

        Bitmap CurrentFrame(double now)
        {
            if (hanging)
            {
                if (now < pack.WaitIntro.Duration) return pack.WaitIntro.Once(now);
                return pack.WaitLoop.Loop(now - pack.WaitIntro.Duration);
            }
            if (phase == Phase.Stay)
            {
                double t = now - stayStart;
                if (t < pack.Land.Duration) return pack.Land.Once(t);
                return pack.Idle.Loop(t - pack.Land.Duration);
            }
            if (v < -ApexSpeed * scale) return pack.Jump.First;
            if (v < ApexSpeed * scale && phase == Phase.Enter) return pack.Apex.Once(now - apexAt);
            return pack.Fall.First;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            double now = Elapsed;
            Bitmap frame = CurrentFrame(now);
            int cx = anchorX;
            if (hanging && phase == Phase.Stay) cx += (int)(Math.Sin(now * 2.2) * 6 * scale);
            if (alert && phase != Phase.Leave && now % 1.6 < 0.45) cx += (int)(Math.Sin(now * 90) * 3 * scale);
            int left = cx - frame.Width / 2;
            int top = hanging ? (int)y : (int)y - frame.Height;

            if (hanging && top + 4 > 0)
            {
                int webX = left + pack.WebX(frame);
                using (Pen dark = new Pen(Color.FromArgb(60, 60, 60), Math.Max(3, 3 * scale)))
                using (Pen web = new Pen(Color.FromArgb(235, 235, 235), Math.Max(1, scale)))
                {
                    g.DrawLine(dark, anchorX, 0, webX, top + 4);
                    g.DrawLine(web, anchorX, 0, webX, top + 4);
                }
            }

            g.DrawImageUnscaled(frame, left, top);

            Point head = hanging ? pack.WaitHead : pack.DoneHead;
            head.Offset(left, top);
            if (alert && phase != Phase.Leave) DrawTingle(g, head, now);
            if (phase == Phase.Stay) DrawBubble(g, left, head.Y);
        }

        // Spider-sense: the pack's effect frames around his head, or simple flashing lines.
        void DrawTingle(Graphics g, Point head, double now)
        {
            if (!pack.Alert.Empty)
            {
                Bitmap fx = pack.Alert.Loop(now);
                // Effect frames are flipped for the head-down pose; the lines fan out below his head.
                g.DrawImageUnscaled(fx, head.X - fx.Width / 2, head.Y - fx.Height / 3);
                return;
            }
            if ((int)(now * 8) % 2 == 1) return;
            using (Pen pen = new Pen(Color.FromArgb(255, 140, 0), Math.Max(2, 2 * scale)))
            {
                float r1 = 16 * scale, r2 = 28 * scale;
                for (int deg = 20; deg <= 160; deg += 35)
                {
                    double a = deg * Math.PI / 180;
                    g.DrawLine(pen, head.X + (float)Math.Cos(a) * r1, head.Y + (float)Math.Sin(a) * r1,
                                    head.X + (float)Math.Cos(a) * r2, head.Y + (float)Math.Sin(a) * r2);
                }
            }
        }

        void DrawBubble(Graphics g, int spriteLeft, int headY)
        {
            Color accent, fill;
            if (alert) { accent = Color.FromArgb(200, 30, 30); fill = Color.FromArgb(255, 244, 244); }
            else if (!hanging) { accent = Color.FromArgb(30, 123, 52); fill = Color.FromArgb(243, 255, 245); }
            else { accent = Color.FromArgb(190, 25, 25); fill = Color.White; }
            Color border = hanging && !alert ? Color.FromArgb(17, 17, 17) : accent;

            int pad = (int)(10 * scale);
            int gap = (int)(16 * scale);
            int right = spriteLeft - gap;
            int maxText = right - pad * 2 - (int)(8 * scale);
            if (maxText < 60) return;

            SizeF ts = g.MeasureString(o.Title, titleFont, maxText);
            SizeF ms = g.MeasureString(o.Message, bodyFont, maxText);
            SizeF ds = o.Detail.Length > 0 ? g.MeasureString(o.Detail, codeFont, maxText) : SizeF.Empty;
            float detailGap = ds.IsEmpty ? 0 : 4 * scale;
            int w = (int)Math.Ceiling(Math.Max(ts.Width, Math.Max(ms.Width, ds.Width))) + pad * 2;
            int h = (int)Math.Ceiling(ts.Height + ms.Height + detailGap + ds.Height) + pad * 2;

            int bx = right - w;
            int by = Math.Max(2, Math.Min(headY - h / 2, ClientSize.Height - h - 2));
            Rectangle box = new Rectangle(bx, by, w, h);
            int tailY = Math.Max(by + (int)(12 * scale), Math.Min(headY, by + h - (int)(12 * scale)));

            int thickness = Math.Max(2, (int)(3 * scale));
            Point[] tail = {
                new Point(right - 1, tailY - (int)(8 * scale)),
                new Point(right + gap - (int)(4 * scale), tailY),
                new Point(right - 1, tailY + (int)(8 * scale)),
            };

            using (SolidBrush bg = new SolidBrush(fill))
            using (Pen outline = new Pen(border, thickness))
            {
                g.FillRectangle(bg, box);
                g.DrawRectangle(outline, box);
                g.FillPolygon(bg, tail);
                g.DrawLine(outline, tail[0], tail[1]);
                g.DrawLine(outline, tail[1], tail[2]);
            }

            float tx = bx + pad, ty = by + pad;
            using (SolidBrush titleBrush = new SolidBrush(accent))
                g.DrawString(o.Title, titleFont, titleBrush, new RectangleF(tx, ty, maxText, ts.Height));
            ty += ts.Height;
            g.DrawString(o.Message, bodyFont, Brushes.Black, new RectangleF(tx, ty, maxText, ms.Height));
            ty += ms.Height + detailGap;
            if (!ds.IsEmpty)
                using (SolidBrush codeBrush = new SolidBrush(Color.FromArgb(70, 70, 70)))
                    g.DrawString(o.Detail, codeFont, codeBrush, new RectangleF(tx, ty, maxText, ds.Height));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                Native.timeEndPeriod(1);
                pack.Dispose();
                titleFont.Dispose();
                bodyFont.Dispose();
                codeFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
