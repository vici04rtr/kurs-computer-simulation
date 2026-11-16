using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SandFireFDM.Rendering;
using SandFireFDM.Simulation;

namespace SandFireFDM;

public partial class TrackBWindow : Window
{
    /// <summary>Fester Physik-Takt: 60 Hz, unabhängig vom Timer-Jitter.</summary>
    private const double FrameStep = 1.0 / 60.0;

    private static readonly SolidColorBrush BadgeOk = Frozen(27, 77, 27);
    private static readonly SolidColorBrush BadgeBad = Frozen(120, 20, 20);

    private readonly SandWorld _world = new();
    private readonly WorldRenderer _renderer = new();
    private readonly DispatcherTimer _timer;          // single-threaded, kein Parallel.For
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastTime;
    private double _accumulator;
    private bool _running = true;
    private bool _initialized;
    private bool _dirty = true;
    private int _substeps = 1;

    // Maus
    private int _prevCx = -1, _prevCy = -1;

    // Profiling (Auswertung 4x pro Sekunde -> keine String-Allokation pro Frame)
    private double _reportStart;
    private int _frames;
    private long _physicsTicks, _renderTicks;
    private WorldStats _stats;

    // Instabilitäts-Dokumentation
    private long _instStep = -1;
    private float _instS;
    private int _shotCountdown = -1;
    private string _lastShot = "";

    public TrackBWindow()
    {
        InitializeComponent();

        WorldImage.Source = _renderer.Bitmap;

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _timer.Tick += OnTick;

        _initialized = true;
        ApplyParameters();
        _world.LoadDemoScene();
        _renderer.Render(_world, CurrentView);

        _lastTime = _clock.Elapsed.TotalSeconds;
        _reportStart = _lastTime;
        _timer.Start();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private ViewMode CurrentView => RbViewHeat.IsChecked == true ? ViewMode.HeatMap : ViewMode.Material;

    // =====================================================================
    //  Haupt-Schleife
    // =====================================================================
    private void OnTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = Math.Min(now - _lastTime, 0.1);
        _lastTime = now;

        long t0 = Stopwatch.GetTimestamp();
        bool stepped = false;

        if (_running)
        {
            _accumulator += elapsed;
            int n = 0;
            while (_accumulator >= FrameStep && n < 3)
            {
                HandleMouse();
                _world.StepParticles((float)FrameStep);          // Layer 2: Automat
                for (int k = 0; k < _substeps; k++)
                    _world.StepDiffusion();                      // Layer 1: FDM
                _accumulator -= FrameStep;
                n++;
                stepped = true;
            }
            if (n == 3) _accumulator = 0;
        }
        else if (HandleMouse())
        {
            _dirty = true;
        }

        long t1 = Stopwatch.GetTimestamp();

        if (stepped || _dirty)
        {
            _stats = _world.ComputeStats();
            _renderer.Render(_world, CurrentView);
            _dirty = false;

            long t2 = Stopwatch.GetTimestamp();
            _physicsTicks += t1 - t0;
            _renderTicks += t2 - t1;
            _frames++;

            CheckInstability();
        }

        if (now - _reportStart >= 0.25) UpdateStatus(now);
    }

    // =====================================================================
    //  Maus-Werkzeuge
    // =====================================================================
    private bool HandleMouse()
    {
        bool left = Mouse.LeftButton == MouseButtonState.Pressed;
        bool right = Mouse.RightButton == MouseButtonState.Pressed;
        bool heat = Mouse.MiddleButton == MouseButtonState.Pressed || Keyboard.IsKeyDown(Key.H);

        if (!WorldImage.IsMouseOver || !(left || right || heat))
        {
            _prevCx = -1;
            return false;
        }

        Point p = Mouse.GetPosition(WorldImage);
        int cx = Math.Clamp((int)(p.X / WorldImage.ActualWidth * SandWorld.Width), 0, SandWorld.Width - 1);
        int cy = Math.Clamp((int)(p.Y / WorldImage.ActualHeight * SandWorld.Height), 0, SandWorld.Height - 1);
        int r = (int)SliderBrush.Value;

        int x0 = _prevCx < 0 ? cx : _prevCx;
        int y0 = _prevCy < 0 ? cy : _prevCy;
        int dist = Math.Max(Math.Abs(cx - x0), Math.Abs(cy - y0));
        int n = Math.Max(1, dist / Math.Max(1, r / 2));

        for (int k = 1; k <= n; k++)
        {
            int ix = x0 + (cx - x0) * k / n;
            int iy = y0 + (cy - y0) * k / n;

            if (right) _world.Paint(ix, iy, r, Simulation.Material.Stone);
            if (left)
            {
                if (RbSand.IsChecked == true) _world.Paint(ix, iy, r, Simulation.Material.Sand, 0.35f);
                else if (RbLava.IsChecked == true) _world.Paint(ix, iy, r, Simulation.Material.Lava, 0.35f);
                else if (RbWood.IsChecked == true) _world.Paint(ix, iy, r, Simulation.Material.Wood);
                else _world.Paint(ix, iy, r, Simulation.Material.Air);
            }
            if (heat) _world.Heat(ix, iy, r);
        }

        _prevCx = cx;
        _prevCy = cy;
        return true;
    }

    // =====================================================================
    //  Parameter & Stabilitätsanzeige
    // =====================================================================
    private void Param_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        ApplyParameters();
    }

    private void ApplyParameters()
    {
        _world.Dt = (float)SliderDt.Value;
        _world.Alpha = (float)SliderAlpha.Value;
        _world.Gamma = (float)SliderGamma.Value;
        _substeps = (int)SliderSub.Value;

        TxtBrush.Text = ((int)SliderBrush.Value).ToString(CultureInfo.CurrentCulture);
        TxtDt.Text = $"{_world.Dt:F4}";
        TxtAlpha.Text = $"{_world.Alpha:E2}";
        TxtGamma.Text = $"{_world.Gamma:F2}";
        TxtSub.Text = _substeps.ToString(CultureInfo.CurrentCulture);

        float s = _world.StabilityFactor;
        TxtStability.Text = $"s = a·Δt/Δx² = {s:F4}";
        TxtCritical.Text = $"Grenze s ≤ 0,2500  →  Δt_krit = {_world.CriticalDt:F4} s  (Δx = {_world.Dx * 1000f:F0} mm)";

        if (_world.IsStable)
        {
            StabilityBadge.Background = BadgeOk;
            TxtStabilityStatus.Text = "STABIL: Maximumprinzip gilt, keine Oszillation";
        }
        else
        {
            StabilityBadge.Background = BadgeBad;
            TxtStabilityStatus.Text = "WARNUNG: s > 0,25 → Schachbrett-Instabilität, Gitter explodiert!";
        }
    }

    private void BtnPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b ||
            !double.TryParse((string)b.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
            return;

        // a = s · Δx² / Δt  (Δt bleibt, nur der Diffusionskoeffizient wird verändert)
        double alpha = s * _world.Dx * _world.Dx / _world.Dt;
        SliderAlpha.Value = alpha;
        ApplyParameters();
    }

    private void Boundary_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _world.Boundary = RbNeumann.IsChecked == true ? BoundaryMode.NeumannAdiabatic : BoundaryMode.DirichletFixed;
    }

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _dirty = true;
    }

    // =====================================================================
    //  Instabilitäts-Erkennung / Dokumentation
    // =====================================================================
    private void CheckInstability()
    {
        if (_world.FirstInstabilityStep >= 0 && _instStep < 0)
        {
            _instStep = _world.FirstInstabilityStep;
            _instS = _world.StabilityFactor;
            if (ChkAutoShot.IsChecked == true) _shotCountdown = 45;
        }

        if (_shotCountdown > 0 && --_shotCountdown == 0)
        {
            SaveScreenshot();
            _shotCountdown = -1;
        }
    }

    // =====================================================================
    //  Status
    // =====================================================================
    private void UpdateStatus(double now)
    {
        double dt = now - _reportStart;
        double fps = _frames / dt;
        double tickMs = 1000.0 / Stopwatch.Frequency;
        double physMs = _frames > 0 ? _physicsTicks * tickMs / _frames : 0;
        double rendMs = _frames > 0 ? _renderTicks * tickMs / _frames : 0;

        string inst = _instStep >= 0
            ? $"\nINSTABILITÄT erkannt bei FDM-Schritt {_instStep} (s = {_instS:F4})"
            : "\nKeine Instabilität (Min-Temperatur ≥ 20 °C)";

        TxtStatus.Text =
            $"FPS: {fps:F0}   (Ziel ≥ 45)\n" +
            $"Physik: {physMs:F2} ms · Render: {rendMs:F2} ms / Frame\n" +
            $"FDM-Schritte: {_world.StepCount}\n" +
            $"T_max: {_stats.MaxT:F0} °C · T_min: {_stats.MinT:F1} °C\n" +
            $"Brennende Holz-Pixel: {_world.BurningCount}" +
            inst +
            (_lastShot.Length > 0 ? $"\nScreenshot: {_lastShot}" : "");

        _reportStart = now;
        _frames = 0;
        _physicsTicks = 0;
        _renderTicks = 0;
    }

    // =====================================================================
    //  Buttons / Tastatur
    // =====================================================================
    private void TogglePause()
    {
        _running = !_running;
        BtnPause.Content = _running ? "Pause (Leertaste)" : "Fortsetzen (Leertaste)";
        _accumulator = 0;
        _lastTime = _clock.Elapsed.TotalSeconds;
    }

    private void ResetWorld(bool demo)
    {
        if (demo) _world.LoadDemoScene(); else _world.Reset();
        _instStep = -1;
        _shotCountdown = -1;
        _dirty = true;
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void BtnReset_Click(object sender, RoutedEventArgs e) => ResetWorld(false);
    private void BtnDemo_Click(object sender, RoutedEventArgs e) => ResetWorld(true);

    private void BtnHotspot_Click(object sender, RoutedEventArgs e)
    {
        _world.Heat(SandWorld.Width / 2, SandWorld.Height / 2, 8);
        _dirty = true;
    }

    private void BtnShot_Click(object sender, RoutedEventArgs e) => SaveScreenshot();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.D1: RbSand.IsChecked = true; break;
            case Key.D2: RbLava.IsChecked = true; break;
            case Key.D3: RbWood.IsChecked = true; break;
            case Key.D4: RbEraser.IsChecked = true; break;
            case Key.Space: TogglePause(); break;
            case Key.R: ResetWorld(false); break;
            case Key.V:
                if (RbViewHeat.IsChecked == true) RbViewMaterial.IsChecked = true; else RbViewHeat.IsChecked = true;
                break;
            case Key.F12: SaveScreenshot(); break;
            default: return;
        }
        e.Handled = true;
    }

    // =====================================================================
    //  Screenshot (Nachweis für die Stabilitätsstudie)
    // =====================================================================
    private void SaveScreenshot()
    {
        try
        {
            int w = (int)WorldImage.ActualWidth, h = (int)WorldImage.ActualHeight;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(WorldImage);

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SandFireFDM_Screenshots");
            Directory.CreateDirectory(dir);
            string name = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyyMMdd_HHmmss}_s{_world.StabilityFactor:F3}_step{_world.StepCount}_{CurrentView}.png");
            string path = Path.Combine(dir, name);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
            _lastShot = path;
        }
        catch (Exception ex)
        {
            _lastShot = "Fehler: " + ex.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}