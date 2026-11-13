using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopSnowSim
{
    public struct SnowParticle
    {
        public bool Active;
        public float X;
        public float Y;
        public float VX;
        public float VY;
        public float Radius;
        public float Weight;
        public float Phase;
        public byte SpriteIndex;
    }

    public class WindowSnowShelf
    {
        public IntPtr Handle;
        public Rectangle Bounds;
        public int ZOrder;
        public float[] Heights;
        public bool[] ExposedBins;
        public bool SeenThisFrame;

        public WindowSnowShelf(IntPtr handle, Rectangle bounds, int zOrder, int binSize)
        {
            Handle = handle;
            Bounds = bounds;
            ZOrder = zOrder;
            int bins = Math.Max(1, bounds.Width / binSize);
            Heights = new float[bins];
            ExposedBins = new bool[bins];
            SeenThisFrame = true;
        }
    }

    public class SnowOverlayForm : Form
    {
        private const int HOTKEY_ID_CLEAR = 9001;
        private const int MAX_PARTICLES = 6000;
        private const int BIN_SIZE = 2;
        private const float PIXELS_PER_FLAKE_VOLUME = 1.6f;
        private const float MAX_SLOPE_DIFF = 1.15f;
        private const double TARGET_FRAME_TIME = 1.0 / 60.0;
        private const string AUTORUN_KEY_PATH = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AUTORUN_VALUE_NAME = "DesktopSnowSimulator";

        // Мост для связи с приложением котенка
        private UdpClient _catUdp;
        private readonly IPEndPoint _catEndpoint = new IPEndPoint(IPAddress.Loopback, 50506);

        private readonly SnowParticle[] _particles = new SnowParticle[MAX_PARTICLES];
        private int _searchCursor = 0;

        private readonly Dictionary<IntPtr, WindowSnowShelf> _windowShelves = new Dictionary<IntPtr, WindowSnowShelf>();
        private List<Win32Native.TrackedWindowInfo> _allWindowsZOrder = new List<Win32Native.TrackedWindowInfo>();

        private float[] _groundHeights = new float[0];
        private int _groundY;

        private Rectangle _virtualBounds;

        private IntPtr _screenDc;
        private IntPtr _memDc;
        private IntPtr _dibBitmap;
        private IntPtr _oldBitmap;
        private IntPtr _dibBits;
        private IntPtr _dibByteSize;
        private Graphics _dibGraphics;

        private readonly Bitmap[] _flakeSprites = new Bitmap[6];
        private readonly int[] _spriteOffsets = new int[6];

        private readonly Random _rng = new Random();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private double _lastFrameTime;
        private int _frameCount;

        private Point _prevMousePos;
        private float _smoothedMouseVX;
        private float _smoothedMouseVY;
        private float _vortexSwirlStrength;
        private float _vortexDirection = 1f;

        private NotifyIcon _trayIcon;
        private Icon _customSnowIcon;
        private ToolStripMenuItem _autorunMenuItem;

        private readonly SolidBrush _pileBrush = new SolidBrush(Color.FromArgb(248, 255, 255, 255));
        private readonly Pen _crestPen = new Pen(Color.FromArgb(255, 255, 255, 255), 1.4f);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public SnowOverlayForm()
        {
            Win32Native.timeBeginPeriod(1);

            _virtualBounds = SystemInformation.VirtualScreen;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = _virtualBounds;
            TopMost = true;
            ShowInTaskbar = false;

            InitializeDirectDibBuffer(_virtualBounds.Width, _virtualBounds.Height);
            InitializeFlakeSprites();
            InitializeGround();
            InitializeTrayIcon();
            InitializeCatBridge();

            _prevMousePos = Cursor.Position;
            _lastFrameTime = _clock.Elapsed.TotalSeconds;

            for (int i = 0; i < 160; i++)
            {
                float x = _virtualBounds.Left + (float)_rng.NextDouble() * _virtualBounds.Width;
                float y = _virtualBounds.Top + (float)_rng.NextDouble() * (_virtualBounds.Height * 0.7f);
                SpawnRandomFlakeAt(x, y);
            }

            Application.Idle += OnApplicationIdle;
        }

        private void InitializeCatBridge()
        {
            try
            {
                _catUdp = new UdpClient(50505);
                _catUdp.Client.Blocking = false;
            }
            catch
            {
                _catUdp = null;
            }
        }

        private void ProcessCatBridge()
        {
            if (_catUdp == null) return;

            // 1. Читаем сообщения от котенка (прыжок или бег по сугробам)
            try
            {
                while (_catUdp.Available > 0)
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _catUdp.Receive(ref remote);
                    string msg = Encoding.UTF8.GetString(data);
                    string[] parts = msg.Split(':');
                    if (parts.Length >= 3 && parts[0] == "JUMP")
                    {
                        float catX = float.Parse(parts[1], CultureInfo.InvariantCulture);
                        float dir = float.Parse(parts[2], CultureInfo.InvariantCulture);
                        ApplyCatJumpInteraction(catX, dir);
                    }
                }
            }
            catch { }

            // 2. Раз в ~0.3 сек отправляем котенку координаты самого высокого сугроба
            if (_frameCount % 20 == 0 && _groundHeights.Length > 0)
            {
                float maxH = 0f;
                int maxBin = 0;
                for (int i = 15; i < _groundHeights.Length - 15; i++)
                {
                    if (_groundHeights[i] > maxH)
                    {
                        maxH = _groundHeights[i];
                        maxBin = i;
                    }
                }

                float worldX = _virtualBounds.Left + maxBin * BIN_SIZE;
                string payload = string.Format(CultureInfo.InvariantCulture, "DRIFT:{0:F1}:{1:F1}", worldX, maxH);
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                try { _catUdp.Send(bytes, bytes.Length, _catEndpoint); } catch { }
            }
        }

        private void ApplyCatJumpInteraction(float catX, float dir)
        {
            int centerBin = (int)((catX - _virtualBounds.Left) / BIN_SIZE);
            int radiusBins = 26; // Ширина разлета сугроба (~52 пикселя)

            for (int b = centerBin - radiusBins; b <= centerBin + radiusBins; b++)
            {
                if (b < 0 || b >= _groundHeights.Length) continue;
                float h = _groundHeights[b];
                if (h < 1.0f) continue;

                float distNorm = Math.Abs(b - centerBin) / (float)radiusBins;
                float factor = 1.0f - distNorm * 0.65f;
                float kick = h * Clamp(factor, 0.25f, 0.95f);
                _groundHeights[b] -= kick;

                float sideDir = (b == centerBin) ? dir : Math.Sign(b - centerBin);
                float splashVX = sideDir * (45f + (float)_rng.NextDouble() * 85f) + dir * 25f;
                float splashVY = -65f - (float)_rng.NextDouble() * 95f;

                EmitParticlesFromColumn(
                    _virtualBounds.Left + b * BIN_SIZE,
                    _groundY - h * 0.5f,
                    kick * 1.4f,
                    splashVX,
                    splashVY
                );
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Win32Native.WS_EX_LAYERED | Win32Native.WS_EX_TRANSPARENT | Win32Native.WS_EX_TOOLWINDOW | Win32Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        private void InitializeDirectDibBuffer(int width, int height)
        {
            _screenDc = Win32Native.GetDC(IntPtr.Zero);
            _memDc = Win32Native.CreateCompatibleDC(_screenDc);

            Win32Native.BITMAPINFO bmi = new Win32Native.BITMAPINFO();
            bmi.biSize = Marshal.SizeOf(typeof(Win32Native.BITMAPINFO));
            bmi.biWidth = width;
            bmi.biHeight = -height;
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            bmi.biCompression = 0;

            _dibBitmap = Win32Native.CreateDIBSection(_memDc, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
            _oldBitmap = Win32Native.SelectObject(_memDc, _dibBitmap);
            _dibByteSize = new IntPtr(width * height * 4);

            _dibGraphics = Graphics.FromHdc(_memDc);
            _dibGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            _dibGraphics.CompositingMode = CompositingMode.SourceOver;
        }

        private void InitializeFlakeSprites()
        {
            float[] radii = { 0.9f, 1.15f, 1.4f, 1.65f, 1.9f, 2.15f };
            int[] alphas = { 200, 215, 230, 240, 250, 255 };

            for (int i = 0; i < _flakeSprites.Length; i++)
            {
                int size = 8;
                Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float r = radii[i];
                    float center = size * 0.5f;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alphas[i], 255, 255, 255)))
                    {
                        g.FillEllipse(b, center - r, center - r, r * 2f, r * 2f);
                    }
                }
                _flakeSprites[i] = bmp;
                _spriteOffsets[i] = size / 2;
            }
        }

        private Icon CreateSnowflakeTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (SolidBrush bg = new SolidBrush(Color.FromArgb(230, 30, 110, 210)))
                {
                    g.FillEllipse(bg, 1, 1, 30, 30);
                }

                using (Pen pen = new Pen(Color.White, 2.2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    for (int a = 0; a < 3; a++)
                    {
                        double angle = a * Math.PI / 3.0;
                        float dx = (float)Math.Cos(angle) * 10f;
                        float dy = (float)Math.Sin(angle) * 10f;
                        g.DrawLine(pen, 16f - dx, 16f - dy, 16f + dx, 16f + dy);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
                DestroyIcon(hIcon);
                return icon;
            }
        }

        private static float Clamp(float val, float min, float max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        private void InitializeGround()
        {
            int totalBins = Math.Max(1, _virtualBounds.Width / BIN_SIZE);
            _groundHeights = new float[totalBins];

            Screen primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            _groundY = primary.WorkingArea.Bottom;
            if (_groundY <= primary.Bounds.Top + 100)
                _groundY = primary.Bounds.Bottom;
        }

        private void InitializeTrayIcon()
        {
            _customSnowIcon = CreateSnowflakeTrayIcon();
            Icon = _customSnowIcon;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Очистить снег (Ctrl+Alt+S)", null, (s, e) => ClearAllSnow());

            _autorunMenuItem = new ToolStripMenuItem("Запускать вместе с Windows")
            {
                Checked = IsAutorunEnabled()
            };
            _autorunMenuItem.Click += (s, e) => ToggleAutorun();
            menu.Items.Add(_autorunMenuItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход (Exit)", null, (s, e) => Close());

            _trayIcon = new NotifyIcon
            {
                Icon = _customSnowIcon,
                Text = "Desktop Snow (Ctrl+Alt+S - очистить)",
                ContextMenuStrip = menu,
                Visible = true
            };
        }

        private bool IsAutorunEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AUTORUN_KEY_PATH, false))
                {
                    if (key == null) return false;
                    string val = key.GetValue(AUTORUN_VALUE_NAME) as string;
                    return !string.IsNullOrEmpty(val);
                }
            }
            catch
            {
                return false;
            }
        }

        private void ToggleAutorun()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AUTORUN_KEY_PATH, true))
                {
                    if (key == null) return;
                    if (IsAutorunEnabled())
                    {
                        key.DeleteValue(AUTORUN_VALUE_NAME, false);
                        _autorunMenuItem.Checked = false;
                    }
                    else
                    {
                        string exePath = "\"" + Application.ExecutablePath + "\"";
                        key.SetValue(AUTORUN_VALUE_NAME, exePath);
                        _autorunMenuItem.Checked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось изменить настройки автозапуска: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32Native.MakeOverlayClickThrough(Handle);
            Win32Native.RegisterHotKey(Handle, HOTKEY_ID_CLEAR, Win32Native.MOD_CONTROL | Win32Native.MOD_ALT, Win32Native.VK_S);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32Native.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID_CLEAR)
            {
                ClearAllSnow();
            }
            base.WndProc(ref m);
        }

        private void ClearAllSnow()
        {
            Array.Clear(_groundHeights, 0, _groundHeights.Length);
            foreach (var shelf in _windowShelves.Values)
            {
                Array.Clear(shelf.Heights, 0, shelf.Heights.Length);
            }
            for (int i = 0; i < MAX_PARTICLES; i++)
            {
                _particles[i].Active = false;
            }
            RenderLayeredFrame();
        }

        private static bool IsApplicationIdle()
        {
            Win32Native.NativeMessage result;
            return !Win32Native.PeekMessage(out result, IntPtr.Zero, 0, 0, 0);
        }

        private void OnApplicationIdle(object sender, EventArgs e)
        {
            while (IsApplicationIdle())
            {
                double now = _clock.Elapsed.TotalSeconds;
                double elapsed = now - _lastFrameTime;

                if (elapsed >= TARGET_FRAME_TIME)
                {
                    float dt = Clamp((float)elapsed, 0.004f, 0.035f);
                    _lastFrameTime = now;
                    StepSimulationAndRender(dt, (float)now);
                }
                else
                {
                    double remainingMs = (TARGET_FRAME_TIME - elapsed) * 1000.0;
                    if (remainingMs > 1.5)
                        Thread.Sleep(1);
                    else
                        Thread.SpinWait(60);
                }
            }
        }

        private void StepSimulationAndRender(float dt, float now)
        {
            _frameCount++;

            if (_frameCount % 4 == 0)
            {
                UpdateTrackedWindows();
            }

            ProcessCatBridge();

            Point mousePos = Cursor.Position;
            float rawVX = (mousePos.X - _prevMousePos.X) / dt;
            float rawVY = (mousePos.Y - _prevMousePos.Y) / dt;
            _prevMousePos = mousePos;

            _smoothedMouseVX += (rawVX - _smoothedMouseVX) * Clamp(dt * 14f, 0f, 1f);
            _smoothedMouseVY += (rawVY - _smoothedMouseVY) * Clamp(dt * 14f, 0f, 1f);

            float mouseSpeed = (float)Math.Sqrt(_smoothedMouseVX * _smoothedMouseVX + _smoothedMouseVY * _smoothedMouseVY);

            if (Math.Abs(_smoothedMouseVX) > 25f)
            {
                _vortexDirection = _smoothedMouseVX > 0 ? 1f : -1f;
            }

            float targetSwirl = Clamp(mouseSpeed * 0.85f, 0f, 1600f);
            if (targetSwirl > _vortexSwirlStrength)
                _vortexSwirlStrength += (targetSwirl - _vortexSwirlStrength) * Clamp(dt * 10f, 0f, 1f);
            else
                _vortexSwirlStrength += (targetSwirl - _vortexSwirlStrength) * Clamp(dt * 3.2f, 0f, 1f);

            SpawnNewSnowflakes(dt);
            ApplyMouseInteractionWithPiles(mousePos, _smoothedMouseVX, _smoothedMouseVY, mouseSpeed);
            UpdateParticles(dt, now, mousePos);
            RelaxHeightfields();

            RenderLayeredFrame();
        }

        private void SpawnRandomFlakeAt(float x, float y)
        {
            byte spriteIdx = (byte)_rng.Next(0, _flakeSprites.Length);
            float radius = 0.8f + spriteIdx * 0.25f;
            float weight = 0.5f + (radius / 2.1f) * 0.5f;
            float vy = 18f + weight * 20f;
            float vx = ((float)_rng.NextDouble() - 0.5f) * 10f;

            SpawnParticle(x, y, vx, vy, radius, weight, spriteIdx);
        }

        private void SpawnNewSnowflakes(float dt)
        {
            float rate = 85f;
            int countToSpawn = (int)(rate * dt) + (_rng.NextDouble() < (rate * dt) % 1.0f ? 1 : 0);
            for (int i = 0; i < countToSpawn; i++)
            {
                float x = _virtualBounds.Left + (float)_rng.NextDouble() * _virtualBounds.Width;
                float y = _virtualBounds.Top - _rng.Next(2, 15);
                SpawnRandomFlakeAt(x, y);
            }
        }

        private void SpawnParticle(float x, float y, float vx, float vy, float radius, float weight, byte spriteIdx)
        {
            for (int i = 0; i < MAX_PARTICLES; i++)
            {
                int idx = (_searchCursor + i) % MAX_PARTICLES;
                if (!_particles[idx].Active)
                {
                    _particles[idx] = new SnowParticle
                    {
                        Active = true,
                        X = x,
                        Y = y,
                        VX = vx,
                        VY = vy,
                        Radius = radius,
                        Weight = weight,
                        Phase = (float)(_rng.NextDouble() * Math.PI * 2.0),
                        SpriteIndex = spriteIdx
                    };
                    _searchCursor = (idx + 1) % MAX_PARTICLES;
                    return;
                }
            }
        }

        private bool IsPointOccludedByHigherWindow(int worldX, int worldY, int targetWindowZOrder)
        {
            for (int i = 0; i < _allWindowsZOrder.Count; i++)
            {
                var win = _allWindowsZOrder[i];
                if (win.ZOrder >= targetWindowZOrder)
                    break;

                if (worldX >= win.Bounds.Left && worldX < win.Bounds.Right &&
                    worldY >= win.Bounds.Top && worldY < win.Bounds.Bottom)
                {
                    return true;
                }
            }
            return false;
        }

        private void UpdateTrackedWindows()
        {
            _allWindowsZOrder = Win32Native.GetVisibleWindowsInZOrder(Handle, _virtualBounds);

            foreach (var shelf in _windowShelves.Values)
                shelf.SeenThisFrame = false;

            foreach (var win in _allWindowsZOrder)
            {
                if (!win.CanHoldSnow)
                    continue;

                WindowSnowShelf shelf;
                if (_windowShelves.TryGetValue(win.Handle, out shelf))
                {
                    shelf.SeenThisFrame = true;
                    shelf.ZOrder = win.ZOrder;
                    HandleWindowMovementOrResize(shelf, win.Bounds);
                }
                else
                {
                    shelf = new WindowSnowShelf(win.Handle, win.Bounds, win.ZOrder, BIN_SIZE);
                    _windowShelves[win.Handle] = shelf;
                }

                int bins = shelf.Heights.Length;
                if (shelf.ExposedBins.Length != bins)
                    shelf.ExposedBins = new bool[bins];

                int checkY = shelf.Bounds.Top;
                for (int b = 0; b < bins; b++)
                {
                    int worldX = shelf.Bounds.Left + b * BIN_SIZE;
                    bool occluded = IsPointOccludedByHigherWindow(worldX, checkY, shelf.ZOrder);
                    shelf.ExposedBins[b] = !occluded;

                    if (occluded && shelf.Heights[b] > 0.1f)
                    {
                        EmitParticlesFromColumn(worldX, checkY, shelf.Heights[b], 0f, 10f);
                        shelf.Heights[b] = 0f;
                    }
                }
            }

            var closedHandles = new List<IntPtr>();
            foreach (var kvp in _windowShelves)
            {
                if (!kvp.Value.SeenThisFrame)
                {
                    ConvertShelfToFallingSnow(kvp.Value, 0f, 10f);
                    closedHandles.Add(kvp.Key);
                }
            }

            foreach (var handle in closedHandles)
                _windowShelves.Remove(handle);
        }

        private void HandleWindowMovementOrResize(WindowSnowShelf shelf, Rectangle newBounds)
        {
            int dx = newBounds.X - shelf.Bounds.X;
            int dy = newBounds.Y - shelf.Bounds.Y;
            int dw = newBounds.Width - shelf.Bounds.Width;

            if (dx == 0 && dy == 0 && dw == 0)
                return;

            float dist = (float)Math.Sqrt(dx * dx + dy * dy);

            if (dist > 60f || Math.Abs(dy) > 30)
            {
                ConvertShelfToFallingSnow(shelf, Clamp(dx * 3.5f, -150f, 150f), Clamp(dy * 2f, -90f, 90f));
                shelf.Bounds = newBounds;
                int newBins = Math.Max(1, newBounds.Width / BIN_SIZE);
                if (shelf.Heights.Length != newBins)
                {
                    shelf.Heights = new float[newBins];
                    shelf.ExposedBins = new bool[newBins];
                }
                return;
            }

            int oldBins = shelf.Heights.Length;
            int targetBins = Math.Max(1, newBounds.Width / BIN_SIZE);
            float[] updatedHeights = new float[targetBins];
            float shedFraction = Clamp(dist / 85f, 0.08f, 0.5f);

            for (int i = 0; i < oldBins; i++)
            {
                float h = shelf.Heights[i];
                if (h <= 0.1f) continue;

                float shedAmount = h * shedFraction;
                float remaining = h - shedAmount;

                if (i < targetBins)
                    updatedHeights[i] = remaining;
                else
                    shedAmount += remaining;

                EmitParticlesFromColumn(
                    shelf.Bounds.Left + i * BIN_SIZE,
                    shelf.Bounds.Top - h,
                    shedAmount,
                    dx * 2.5f + ((float)_rng.NextDouble() - 0.5f) * 35f,
                    dy * 1.5f - (float)_rng.NextDouble() * 25f
                );
            }

            shelf.Heights = updatedHeights;
            if (shelf.ExposedBins.Length != targetBins)
                shelf.ExposedBins = new bool[targetBins];
            shelf.Bounds = newBounds;
        }

        private void ConvertShelfToFallingSnow(WindowSnowShelf shelf, float impulseVX, float impulseVY)
        {
            for (int i = 0; i < shelf.Heights.Length; i++)
            {
                float h = shelf.Heights[i];
                if (h <= 0.2f) continue;

                float worldX = shelf.Bounds.Left + i * BIN_SIZE;
                float worldY = shelf.Bounds.Top - h * 0.5f;
                EmitParticlesFromColumn(
                    worldX,
                    worldY,
                    h,
                    impulseVX + ((float)_rng.NextDouble() - 0.5f) * 35f,
                    impulseVY + ((float)_rng.NextDouble() - 0.3f) * 20f
                );
                shelf.Heights[i] = 0f;
            }
        }

        private void EmitParticlesFromColumn(float x, float y, float heightPixels, float baseVX, float baseVY)
        {
            int count = (int)Math.Ceiling(heightPixels / PIXELS_PER_FLAKE_VOLUME);
            count = Math.Min(count, 10);
            for (int c = 0; c < count; c++)
            {
                byte spriteIdx = (byte)_rng.Next(0, 4);
                float radius = 0.8f + spriteIdx * 0.25f;
                float py = y + ((float)_rng.NextDouble() - 0.5f) * Math.Min(heightPixels, 14f);
                SpawnParticle(
                    x + (float)_rng.NextDouble() * BIN_SIZE,
                    py,
                    baseVX + ((float)_rng.NextDouble() - 0.5f) * 25f,
                    baseVY + ((float)_rng.NextDouble() - 0.5f) * 20f,
                    radius,
                    0.75f,
                    spriteIdx
                );
            }
        }

        private void ApplyMouseInteractionWithPiles(Point mousePos, float mouseVX, float mouseVY, float speed)
        {
            if (speed < 45f) return;

            int radiusBins = 10;

            int groundBin = (mousePos.X - _virtualBounds.Left) / BIN_SIZE;
            for (int b = groundBin - radiusBins; b <= groundBin + radiusBins; b++)
            {
                if (b < 0 || b >= _groundHeights.Length) continue;
                float h = _groundHeights[b];
                if (h < 1.2f) continue;

                float surfaceY = _groundY - h;
                if (Math.Abs(mousePos.Y - surfaceY) < 28f)
                {
                    float kick = Math.Min(h, Clamp(speed * 0.01f, 1.5f, 14f));
                    _groundHeights[b] -= kick;
                    EmitParticlesFromColumn(
                        _virtualBounds.Left + b * BIN_SIZE,
                        surfaceY - 2f,
                        kick,
                        mouseVX * 0.22f,
                        -Math.Abs(mouseVY * 0.15f) - Clamp(speed * 0.12f, 25f, 95f)
                    );
                }
            }

            foreach (var shelf in _windowShelves.Values)
            {
                if (mousePos.X < shelf.Bounds.Left - 24 || mousePos.X > shelf.Bounds.Right + 24)
                    continue;

                int wBin = (mousePos.X - shelf.Bounds.Left) / BIN_SIZE;
                for (int b = wBin - radiusBins; b <= wBin + radiusBins; b++)
                {
                    if (b < 0 || b >= shelf.Heights.Length || !shelf.ExposedBins[b]) continue;
                    float h = shelf.Heights[b];
                    if (h < 1.2f) continue;

                    float surfaceY = shelf.Bounds.Top - h;
                    if (Math.Abs(mousePos.Y - surfaceY) < 24f)
                    {
                        float kick = Math.Min(h, Clamp(speed * 0.01f, 1.5f, 12f));
                        shelf.Heights[b] -= kick;
                        EmitParticlesFromColumn(
                            shelf.Bounds.Left + b * BIN_SIZE,
                            surfaceY - 2f,
                            kick,
                            mouseVX * 0.22f,
                            -Math.Abs(mouseVY * 0.15f) - Clamp(speed * 0.1f, 20f, 80f)
                        );
                    }
                }
            }
        }

        private void UpdateParticles(float dt, float time, Point mousePos)
        {
            float globalWind = (float)Math.Sin(time * 0.3f) * 14f + (float)Math.Cos(time * 0.7f) * 8f;

            bool vortexActive = _vortexSwirlStrength > 15f;
            float vortexRadius = Clamp(75f + _vortexSwirlStrength * 0.11f, 80f, 210f);
            float vortexRadiusSq = vortexRadius * vortexRadius;

            for (int i = 0; i < MAX_PARTICLES; i++)
            {
                if (!_particles[i].Active) continue;

                SnowParticle p = _particles[i];

                float localFlutter = (float)Math.Sin(time * 2.0f + p.Phase) * 10f;
                float targetVX = (globalWind + localFlutter) / (p.Weight + 0.3f);
                p.VX += (targetVX - p.VX) * 2.0f * dt;

                float terminalVY = 20f + p.Weight * 22f;
                p.VY += (terminalVY - p.VY) * 2.5f * dt;

                if (vortexActive)
                {
                    float dx = p.X - mousePos.X;
                    float dy = p.Y - mousePos.Y;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < vortexRadiusSq && distSq > 4f)
                    {
                        float dist = (float)Math.Sqrt(distSq);
                        float normDist = dist / vortexRadius;
                        float influence = (1f - normDist);
                        influence *= influence;

                        float nx = dx / dist;
                        float ny = dy / dist;

                        float tx = -ny * _vortexDirection;
                        float ty = nx * _vortexDirection;

                        float swirlAccel = _vortexSwirlStrength * 2.6f * influence / p.Weight;
                        p.VX += tx * swirlAccel * dt;
                        p.VY += ty * swirlAccel * dt;

                        float radialPull = (dist > 28f) ? -_vortexSwirlStrength * 0.75f : _vortexSwirlStrength * 0.5f;
                        p.VX += nx * radialPull * influence * dt;
                        p.VY += ny * radialPull * influence * dt;

                        p.VX += _smoothedMouseVX * 0.45f * influence * dt;
                        p.VY += (_smoothedMouseVY * 0.45f - _vortexSwirlStrength * 0.35f) * influence * dt;
                    }
                }

                float prevY = p.Y;
                p.X += p.VX * dt;
                p.Y += p.VY * dt;

                if (p.X < _virtualBounds.Left) p.X += _virtualBounds.Width;
                else if (p.X >= _virtualBounds.Right) p.X -= _virtualBounds.Width;

                if (p.VY > 0f)
                {
                    bool landed = false;

                    foreach (var shelf in _windowShelves.Values)
                    {
                        if (p.X >= shelf.Bounds.Left && p.X < shelf.Bounds.Right)
                        {
                            int bin = (int)((p.X - shelf.Bounds.Left) / BIN_SIZE);
                            if (bin >= 0 && bin < shelf.Heights.Length && shelf.ExposedBins[bin])
                            {
                                float surfaceY = shelf.Bounds.Top - shelf.Heights[bin];
                                if (prevY <= surfaceY + 5f && p.Y >= surfaceY)
                                {
                                    DepositSnow(shelf.Heights, shelf.ExposedBins, bin, p.Radius * 0.55f);
                                    p.Active = false;
                                    landed = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (landed)
                    {
                        _particles[i] = p;
                        continue;
                    }

                    int gBin = (int)((p.X - _virtualBounds.Left) / BIN_SIZE);
                    if (gBin >= 0 && gBin < _groundHeights.Length)
                    {
                        float groundSurfaceY = _groundY - _groundHeights[gBin];
                        if (p.Y >= groundSurfaceY)
                        {
                            DepositSnowGround(_groundHeights, gBin, p.Radius * 0.6f);
                            p.Active = false;
                            _particles[i] = p;
                            continue;
                        }
                    }
                }

                if (p.Y > _virtualBounds.Bottom + 30)
                {
                    p.Active = false;
                }

                _particles[i] = p;
            }
        }

        private static void DepositSnow(float[] heights, bool[] exposed, int centerBin, float amount)
        {
            if (exposed[centerBin])
                heights[centerBin] += amount * 0.6f;
            if (centerBin > 0 && exposed[centerBin - 1])
                heights[centerBin - 1] += amount * 0.2f;
            if (centerBin < heights.Length - 1 && exposed[centerBin + 1])
                heights[centerBin + 1] += amount * 0.2f;
        }

        private static void DepositSnowGround(float[] heights, int centerBin, float amount)
        {
            heights[centerBin] += amount * 0.6f;
            if (centerBin > 0) heights[centerBin - 1] += amount * 0.2f;
            if (centerBin < heights.Length - 1) heights[centerBin + 1] += amount * 0.2f;
        }

        private void RelaxHeightfields()
        {
            int maxScreenHeight = _virtualBounds.Height;
            for (int i = 0; i < _groundHeights.Length - 1; i++)
            {
                float diff = _groundHeights[i] - _groundHeights[i + 1];
                if (Math.Abs(diff) > MAX_SLOPE_DIFF)
                {
                    float transfer = (Math.Abs(diff) - MAX_SLOPE_DIFF) * 0.35f;
                    if (diff > 0)
                    {
                        _groundHeights[i] -= transfer;
                        _groundHeights[i + 1] += transfer;
                    }
                    else
                    {
                        _groundHeights[i] += transfer;
                        _groundHeights[i + 1] -= transfer;
                    }
                }
                if (_groundHeights[i] > maxScreenHeight)
                    _groundHeights[i] = maxScreenHeight;
            }

            foreach (var shelf in _windowShelves.Values)
            {
                float[] h = shelf.Heights;
                int len = h.Length;
                if (len == 0) continue;

                if (h[0] > 3f)
                {
                    float spill = h[0] * 0.35f;
                    h[0] -= spill;
                    EmitParticlesFromColumn(shelf.Bounds.Left - 2, shelf.Bounds.Top, spill, -12f, 10f);
                }
                if (h[len - 1] > 3f)
                {
                    float spill = h[len - 1] * 0.35f;
                    h[len - 1] -= spill;
                    EmitParticlesFromColumn(shelf.Bounds.Right + 2, shelf.Bounds.Top, spill, 12f, 10f);
                }

                for (int i = 0; i < len - 1; i++)
                {
                    float diff = h[i] - h[i + 1];
                    if (Math.Abs(diff) > MAX_SLOPE_DIFF)
                    {
                        float transfer = (Math.Abs(diff) - MAX_SLOPE_DIFF) * 0.35f;
                        if (diff > 0)
                        {
                            h[i] -= transfer;
                            h[i + 1] += transfer;
                        }
                        else
                        {
                            h[i] += transfer;
                            h[i + 1] -= transfer;
                        }
                    }
                }
            }
        }

        private void RenderLayeredFrame()
        {
            if (!IsHandleCreated || IsDisposed || _dibBits == IntPtr.Zero)
                return;

            Win32Native.ZeroMemory(_dibBits, _dibByteSize);

            int offsetX = _virtualBounds.Left;
            int offsetY = _virtualBounds.Top;

            for (int i = 0; i < MAX_PARTICLES; i++)
            {
                if (!_particles[i].Active) continue;
                SnowParticle p = _particles[i];
                int sIdx = p.SpriteIndex;
                int half = _spriteOffsets[sIdx];
                _dibGraphics.DrawImageUnscaled(_flakeSprites[sIdx], (int)p.X - offsetX - half, (int)p.Y - offsetY - half);
            }

            _dibGraphics.TranslateTransform(-offsetX, -offsetY);

            foreach (var shelf in _windowShelves.Values)
            {
                RenderSnowStrip(_dibGraphics, shelf.Heights, shelf.Bounds.Left, shelf.Bounds.Top);
            }

            RenderSnowStrip(_dibGraphics, _groundHeights, _virtualBounds.Left, _groundY);

            _dibGraphics.ResetTransform();

            Win32Native.SIZE size = new Win32Native.SIZE(_virtualBounds.Width, _virtualBounds.Height);
            Win32Native.POINT pointSource = new Win32Native.POINT(0, 0);
            Win32Native.POINT topPos = new Win32Native.POINT(_virtualBounds.Left, _virtualBounds.Top);

            Win32Native.BLENDFUNCTION blend = new Win32Native.BLENDFUNCTION
            {
                BlendOp = Win32Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32Native.AC_SRC_ALPHA
            };

            Win32Native.UpdateLayeredWindow(Handle, _screenDc, ref topPos, ref size, _memDc, ref pointSource, 0, ref blend, Win32Native.ULW_ALPHA);
        }

        private void RenderSnowStrip(Graphics g, float[] heights, int startX, int baseLineY)
        {
            int len = heights.Length;
            int segStart = -1;

            for (int i = 0; i <= len; i++)
            {
                bool hasSnow = i < len && heights[i] >= 0.8f;
                if (hasSnow && segStart == -1)
                {
                    segStart = i;
                }
                else if (!hasSnow && segStart != -1)
                {
                    DrawSnowPolygonSegment(g, heights, segStart, i - 1, startX, baseLineY);
                    segStart = -1;
                }
            }
        }

        private void DrawSnowPolygonSegment(Graphics g, float[] heights, int firstBin, int lastBin, int startX, int baseLineY)
        {
            int count = lastBin - firstBin + 1;
            PointF[] poly = new PointF[count + 2];

            poly[0] = new PointF(startX + firstBin * BIN_SIZE, baseLineY + 1);
            for (int i = 0; i < count; i++)
            {
                int b = firstBin + i;
                poly[i + 1] = new PointF(startX + b * BIN_SIZE + (BIN_SIZE * 0.5f), baseLineY - heights[b]);
            }
            poly[count + 1] = new PointF(startX + (lastBin + 1) * BIN_SIZE, baseLineY + 1);

            g.FillPolygon(_pileBrush, poly);

            if (count >= 2)
            {
                PointF[] crest = new PointF[count];
                Array.Copy(poly, 1, crest, 0, count);
                g.DrawLines(_crestPen, crest);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Application.Idle -= OnApplicationIdle;
            Win32Native.timeEndPeriod(1);
            Win32Native.UnregisterHotKey(Handle, HOTKEY_ID_CLEAR);

            if (_catUdp != null)
            {
                try { _catUdp.Close(); } catch { }
            }

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            if (_customSnowIcon != null)
                _customSnowIcon.Dispose();

            foreach (var bmp in _flakeSprites)
            {
                if (bmp != null) bmp.Dispose();
            }

            _pileBrush.Dispose();
            _crestPen.Dispose();

            if (_dibGraphics != null) _dibGraphics.Dispose();
            if (_dibBitmap != IntPtr.Zero)
            {
                Win32Native.SelectObject(_memDc, _oldBitmap);
                Win32Native.DeleteObject(_dibBitmap);
            }
            if (_memDc != IntPtr.Zero) Win32Native.DeleteDC(_memDc);
            if (_screenDc != IntPtr.Zero) Win32Native.ReleaseDC(IntPtr.Zero, _screenDc);

            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static Mutex _singleInstanceMutex;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "Local\\DesktopSnowSimulator_UniqueMutex", out createdNew);
            if (!createdNew)
            {
                return;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SnowOverlayForm());

            GC.KeepAlive(_singleInstanceMutex);
        }
    }
}