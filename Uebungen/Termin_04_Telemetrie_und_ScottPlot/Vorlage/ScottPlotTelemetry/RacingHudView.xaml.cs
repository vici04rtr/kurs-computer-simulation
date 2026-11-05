using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlotTelemetry.Data;
using ScottPlotTelemetry.Statistics;
using Msagl = Microsoft.Msagl.Drawing;
using MsaglWpf = Microsoft.Msagl.WpfGraphControl;
using MsColor = Microsoft.Msagl.Drawing.Color;
using SpColor = ScottPlot.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;

namespace ScottPlotTelemetry;

/// <summary>
/// Track B: Retro Arcade Racing HUD &amp; G-Force-Telemetrie.
///   Panel 1: Speed- &amp; RPM-Streamer (2 Y-Achsen) mit Welford-Schalthüllkurve und Shift-Flash (RPM &gt; 7200)
///   Panel 2: Kamm'scher Kreis (a_y vs. a_x), Farbcodierung Grün / Orange / Rot
///   Panel 3: Live-Histogramm der Bremsverzögerung mit Gauß-Überlagerung (Welford)
///   Panel 4: Streckentopologie mit MSAGL, aktiver Abschnitt hervorgehoben
/// Das Control startet/stoppt seinen 50-Hz-Timer selbst, sobald es sichtbar/unsichtbar wird.
/// </summary>
public partial class RacingHudView : UserControl
{
    // ------------------------------------------------------------------ Konstanten
    private const int Capacity = 500;                  // 10 s @ 50 Hz
    private const double Dt = 0.02;                    // 20 ms
    private const int TrailLength = 150;               // G-G-Spur: 3 s
    private const double ShiftFlashRpm = 7200.0;
    private const int FlashHoldTicks = 15;             // 0,3 s Haltezeit (Entprellung)
    private const int GripHoldTicks = 25;              // 0,5 s Haltezeit (Entprellung)
    private const double GripWarnUtil = 0.90;          // ab hier Orange
    private const double GripLimitUtil = 1.00;         // ab hier Rot

    private const int HistBins = 20;
    private const double HistMin = 0.40;               // g
    private const double HistMax = 1.40;               // g
    private const double BinWidth = (HistMax - HistMin) / HistBins;
    private const double BrakeThresholdG = 0.40;       // ab dieser Verzögerung zählt ein Sample als "Anbremsen"
    private const int GaussPoints = 101;

    // ------------------------------------------------------------------ vorallokierte, eingefrorene Brushes
    private static readonly WpfBrush ShiftIdleBg = Frozen(42, 42, 42);
    private static readonly WpfBrush ShiftIdleFg = Frozen(170, 170, 170);
    private static readonly WpfBrush ShiftHotBgA = Frozen(200, 20, 20);
    private static readonly WpfBrush ShiftHotBgB = Frozen(110, 10, 10);
    private static readonly WpfBrush ShiftHotFg = Frozen(255, 240, 120);

    private static readonly WpfBrush[] GripBg = [Frozen(27, 77, 27), Frozen(150, 90, 10), Frozen(160, 30, 30)];
    private static readonly WpfBrush[] GripFg = [Frozen(152, 251, 152), Frozen(255, 210, 120), Frozen(255, 130, 130)];
    private static readonly string[] GripText = ["VOLLER GRIP", "UNTERSTEUERN / DRIFT", "TRAKTIONSABRISS"];

    private static WpfBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new WpfBrush(WpfColor.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ MSAGL-Farben
    private static readonly MsColor NodeInactiveFill = new(45, 45, 45);
    private static readonly MsColor NodeInactiveFont = new(200, 200, 200);
    private static readonly MsColor NodeInactiveLine = new(120, 120, 120);
    private static readonly MsColor NodeActiveFill = new(0, 210, 255);
    private static readonly MsColor NodeActiveFont = new(0, 0, 0);
    private static readonly MsColor NodeActiveLine = new(255, 255, 255);
    private static readonly MsColor GraphBackground = new(30, 30, 30);

    // ------------------------------------------------------------------ Simulation, Puffer, Statistik
    private readonly TrackSimulator _sim = new();
    private readonly DispatcherTimer _timer;

    private readonly WelfordStatistics _shiftStats = new();   // Ausdrehzahl beim Hochschalten
    private readonly WelfordStatistics _brakeStats = new();   // Bremsverzögerung [g]
    private readonly WelfordStatistics _lapStats = new();     // Rundenzeit [s]

    private readonly CircularBuffer<double> _axBuf = new(TrailLength);
    private readonly CircularBuffer<double> _ayBuf = new(TrailLength);
    private readonly double[] _axCopy = new double[TrailLength];
    private readonly double[] _ayCopy = new double[TrailLength];

    // Vorallokierte Koordinaten-Arrays je Farbklasse (Scatter liest direkt daraus)
    private readonly double[] _greenX = new double[TrailLength], _greenY = new double[TrailLength];
    private readonly double[] _orangeX = new double[TrailLength], _orangeY = new double[TrailLength];
    private readonly double[] _redX = new double[TrailLength], _redY = new double[TrailLength];

    private readonly int[] _hist = new int[HistBins];
    private readonly double[] _gaussX = new double[GaussPoints];
    private readonly double[] _gaussY = new double[GaussPoints];

    // ------------------------------------------------------------------ ScottPlot-Objekte
    private DataStreamer _speedStreamer = null!;
    private DataStreamer _rpmStreamer = null!;
    private IYAxis _rpmAxis = null!;
    private HorizontalLine _redline = null!;
    private HorizontalLine _shiftMean = null!;
    private HorizontalLine _shiftUpper = null!;
    private HorizontalLine _shiftLower = null!;

    private Scatter _ggGreen = null!;
    private Scatter _ggOrange = null!;
    private Scatter _ggRed = null!;
    private Marker _ggMarker = null!;

    private List<Bar> _bars = null!;
    private Scatter _gauss = null!;
    private VerticalLine _brakeMean = null!;

    // ------------------------------------------------------------------ MSAGL
    private MsaglWpf.GraphViewer? _viewer;
    private readonly Dictionary<string, Msagl.IViewerNode> _vnodes = new();
    private Msagl.IViewerNode? _activeNode;

    // ------------------------------------------------------------------ UI-Zustand
    private long _tick;
    private bool _userPaused;
    private int _flashTicks;
    private int _shiftBadgeState = -1;      // -1 = unbekannt, 0 = idle, 1 = Flash A, 2 = Flash B
    private int _flashCount;
    private int _gripShown = -1;
    private int _gripHold;
    private int _slipCount;
    private int _prevClass;
    private double _bestLap = double.MaxValue;

    public RacingHudView()
    {
        InitializeComponent();

        SetupSpeedRpmPlot();
        SetupGgPlot();
        SetupBrakePlot();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(20)    // 50 Hz
        };
        _timer.Tick += OnTick;
    }

    // =====================================================================================
    //  Öffentliche Steuerung (wird auch automatisch über IsVisibleChanged aufgerufen)
    // =====================================================================================
    public void Start()
    {
        if (_userPaused) return;
        _timer.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(EnsureGraph));   // MSAGL erst, wenn Layout steht
    }

    public void Stop() => _timer.Stop();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Start();
        else Stop();
    }

    // =====================================================================================
    //  Plot-Aufbau
    // =====================================================================================
    private static void StyleDark(Plot p)
    {
        p.FigureBackground.Color = SpColor.FromHex("#141414");
        p.DataBackground.Color = SpColor.FromHex("#1E1E1E");
        p.Axes.Color(SpColor.FromHex("#CCCCCC"));
        p.Grid.MajorLineColor = SpColor.FromHex("#2E2E2E");
    }

    private static void StyleLegend(Plot p, Alignment where)
    {
        Legend legend = p.ShowLegend(where);
        legend.BackgroundColor = SpColor.FromHex("#1E1E1E").WithAlpha(0.85);
        legend.OutlineColor = SpColor.FromHex("#444444");
        legend.FontColor = SpColor.FromHex("#DDDDDD");
    }

    // ---------------------------------------------------------------- Panel 1
    private void SetupSpeedRpmPlot()
    {
        Plot p = PlotSpeedRpm.Plot;
        StyleDark(p);

        SpColor speedColor = SpColor.FromHex("#00D2FF");
        SpColor rpmColor = SpColor.FromHex("#FFA500");

        _speedStreamer = p.Add.DataStreamer(Capacity, Dt);
        _speedStreamer.ViewScrollLeft();
        _speedStreamer.ManageAxisLimits = false;
        _speedStreamer.Color = speedColor;
        _speedStreamer.LineWidth = 2;
        _speedStreamer.LegendText = "Geschwindigkeit v(t)";

        // Zweite Y-Achse (rechts) für die Drehzahl
        _rpmAxis = p.Axes.AddRightAxis();
        _rpmStreamer = p.Add.DataStreamer(Capacity, Dt);
        _rpmStreamer.Axes.YAxis = _rpmAxis;
        _rpmStreamer.ViewScrollLeft();
        _rpmStreamer.ManageAxisLimits = false;
        _rpmStreamer.Color = rpmColor;
        _rpmStreamer.LineWidth = 2;
        _rpmStreamer.LegendText = "Drehzahl RPM(t)";

        // Drehzahlbegrenzer-Linie und Welford-Schalthüllkurve (alle auf der RPM-Achse)
        _redline = p.Add.HorizontalLine(ShiftFlashRpm, 1.5f, Colors.Red, LinePattern.Solid);
        _redline.Axes.YAxis = _rpmAxis;
        _redline.LegendText = "Shift-Grenze 7200";

        _shiftMean = p.Add.HorizontalLine(0, 1.5f, Colors.Yellow, LinePattern.Solid);
        _shiftMean.Axes.YAxis = _rpmAxis;
        _shiftMean.LegendText = "Ø Ausdrehzahl (Welford)";
        _shiftMean.IsVisible = false;

        _shiftUpper = p.Add.HorizontalLine(0, 1f, Colors.Yellow.WithAlpha(0.7), LinePattern.Dashed);
        _shiftUpper.Axes.YAxis = _rpmAxis;
        _shiftUpper.LegendText = "± 3σ Schaltband";
        _shiftUpper.IsVisible = false;

        _shiftLower = p.Add.HorizontalLine(0, 1f, Colors.Yellow.WithAlpha(0.7), LinePattern.Dashed);
        _shiftLower.Axes.YAxis = _rpmAxis;
        _shiftLower.IsVisible = false;

        p.Title("Panel 1 – Speed & Drehzahl (gleitendes 10-s-Fenster) mit Schalthüllkurve");
        p.Axes.Left.Label.Text = "Geschwindigkeit [km/h]";
        p.Axes.Bottom.Label.Text = "Zeit [s]";
        _rpmAxis.Label.Text = "Drehzahl [1/min]";

        p.Axes.SetLimits(0, Capacity * Dt, 0, 280);
        p.Axes.SetLimitsY(0, 8500, _rpmAxis);

        // Achsenfarben passend zu den Kurven (Title-Farbe danach wieder setzen)
        p.Axes.Color(p.Axes.Left, speedColor);
        p.Axes.Color(_rpmAxis, rpmColor);
        p.Axes.Title.Label.ForeColor = SpColor.FromHex("#CCCCCC");

        StyleLegend(p, Alignment.UpperLeft);
    }

    // ---------------------------------------------------------------- Panel 2
    private void SetupGgPlot()
    {
        Plot p = PlotGG.Plot;
        StyleDark(p);

        p.Add.VerticalLine(0, 1f, Colors.Gray.WithAlpha(0.6), LinePattern.Solid);
        p.Add.HorizontalLine(0, 1f, Colors.Gray.WithAlpha(0.6), LinePattern.Solid);

        // Kamm'scher Kreis: a_res = sqrt(a_x² + a_y²) <= μ·g
        var limit = p.Add.Circle(0, 0, TrackSimulator.Mu);
        limit.FillColor = Colors.Transparent;
        limit.LineColor = Colors.Red;
        limit.LineWidth = 2;

        var warn = p.Add.Circle(0, 0, TrackSimulator.Mu * GripWarnUtil);
        warn.FillColor = Colors.Transparent;
        warn.LineColor = Colors.Orange;
        warn.LineWidth = 1;
        warn.LinePattern = LinePattern.Dashed;

        // Je Farbklasse eine Scatter-Serie auf vorallokierten Arrays (MaxRenderIndex steuert Anzahl)
        _ggGreen = p.Add.ScatterPoints(_greenX, _greenY, SpColor.FromHex("#2ECC71"));
        _ggGreen.MarkerSize = 5;
        _ggGreen.LegendText = "Voller Grip";

        _ggOrange = p.Add.ScatterPoints(_orangeX, _orangeY, SpColor.FromHex("#FFA500"));
        _ggOrange.MarkerSize = 5;
        _ggOrange.LegendText = "Untersteuern / Drift";

        _ggRed = p.Add.ScatterPoints(_redX, _redY, SpColor.FromHex("#FF3B30"));
        _ggRed.MarkerSize = 5;
        _ggRed.LegendText = "Traktionsabriss";

        _ggMarker = p.Add.Marker(0, 0, MarkerShape.FilledCircle, 13, Colors.White);

        SetRange(_ggGreen, _greenX, _greenY, 0);
        SetRange(_ggOrange, _orangeX, _orangeY, 0);
        SetRange(_ggRed, _redX, _redY, 0);

        p.Title("Panel 2 – Kamm'scher Kreis (G-G-Diagramm, Spur 3 s)");
        p.Axes.Bottom.Label.Text = "Querbeschleunigung a_y [g]";
        p.Axes.Left.Label.Text = "Längsbeschleunigung a_x [g]  (+ Gas / − Bremse)";

        p.Axes.SetLimits(-1.8, 1.8, -1.8, 1.8);
        p.Axes.SquareUnits();

        StyleLegend(p, Alignment.UpperRight);
    }

    // ---------------------------------------------------------------- Panel 3
    private void SetupBrakePlot()
    {
        Plot p = PlotBrake.Plot;
        StyleDark(p);

        _bars = new List<Bar>(HistBins);
        for (int i = 0; i < HistBins; i++)
        {
            _bars.Add(new Bar
            {
                Position = HistMin + (i + 0.5) * BinWidth,
                Value = 0,
                Size = BinWidth * 0.9,
                FillColor = SpColor.FromHex("#00D2FF").WithAlpha(0.8),
            });
        }
        p.Add.Bars(_bars);

        for (int i = 0; i < GaussPoints; i++)
            _gaussX[i] = HistMin + (HistMax - HistMin) * i / (GaussPoints - 1);

        _gauss = p.Add.Scatter(_gaussX, _gaussY, SpColor.FromHex("#FFA500"));
        _gauss.LineWidth = 2.5f;
        _gauss.MarkerSize = 0;
        _gauss.LegendText = "Normalverteilung (Welford μ, σ)";
        _gauss.IsVisible = false;

        _brakeMean = p.Add.VerticalLine(0, 1.5f, Colors.Yellow, LinePattern.Dashed);
        _brakeMean.LegendText = "Ø Verzögerung";
        _brakeMean.IsVisible = false;

        p.Title("Panel 3 – Bremsverzögerung beim Anbremsen (20 Klassen)");
        p.Axes.Bottom.Label.Text = "Verzögerung |a_x| [g]";
        p.Axes.Left.Label.Text = "Anzahl Samples";
        p.Axes.SetLimits(HistMin - BinWidth, HistMax + BinWidth, 0, 10);

        StyleLegend(p, Alignment.UpperRight);
    }

    // =====================================================================================
    //  50-Hz-Tick
    // =====================================================================================
    private void OnTick(object? sender, EventArgs e)
    {
        _tick++;
        _sim.Step(Dt);

        double ax = _sim.AxG;
        double ay = _sim.AyG;

        // --- Panel 1: Streamer ----------------------------------------------------------------------
        _speedStreamer.Add(_sim.SpeedKmh);
        _rpmStreamer.Add(_sim.Rpm);

        if (_sim.Upshifted)
        {
            _shiftStats.Add(_sim.UpshiftRpm);
            UpdateShiftEnvelope();
        }
        UpdateShiftFlash(_sim.Rpm);

        // --- Panel 2: G-G-Puffer + Grip-Klassifikation -------------------------------------------
        _axBuf.Add(ax);
        _ayBuf.Add(ay);
        int cls = Classify(ax, ay);
        UpdateGripBadge(cls);

        // --- Panel 3: Bremsverzögerung ------------------------------------------------------------
        if (ax < -BrakeThresholdG)
            AddBrakeSample(-ax);

        // --- Runden --------------------------------------------------------------------------------------
        if (_sim.LapCompleted)
        {
            _lapStats.Add(_sim.LastLapTime);
            if (_sim.LastLapTime < _bestLap) _bestLap = _sim.LastLapTime;
        }

        // --- MSAGL: aktiven Knoten umfärben (nur bei Abschnittswechsel) --------------------------
        if (_sim.SegmentChanged)
            HighlightSegment(_sim.SegmentIndex);

        // --- Rendering (gestaffelte Raten) ---------------------------------------------------------
        PlotSpeedRpm.Refresh();                                   // 50 Hz

        if ((_tick & 1) == 0)                                     // 25 Hz
        {
            UpdateGgTrail(ax, ay, cls);
            PlotGG.Refresh();
        }

        if (_tick % 10 == 0)                                      // 5 Hz
        {
            UpdateHistogram();
            PlotBrake.Refresh();
            UpdateTexts();
        }
    }

    // =====================================================================================
    //  Panel 1 – Schalthüllkurve & Shift-Flash
    // =====================================================================================
    private void UpdateShiftEnvelope()
    {
        if (_shiftStats.Count < 3) return;

        var (lower, upper) = _shiftStats.GetToleranceBand(3.0);
        _shiftMean.Y = _shiftStats.Mean;
        _shiftUpper.Y = upper;
        _shiftLower.Y = lower;
        _shiftMean.IsVisible = _shiftUpper.IsVisible = _shiftLower.IsVisible = true;
    }

    private void UpdateShiftFlash(double rpm)
    {
        if (rpm > ShiftFlashRpm)
        {
            if (_flashTicks == 0) _flashCount++;      // steigende Flanke zählen
            _flashTicks = FlashHoldTicks;
        }
        else if (_flashTicks > 0)
        {
            _flashTicks--;
        }

        int state = _flashTicks == 0 ? 0 : (_flashTicks / 3) % 2 == 0 ? 1 : 2;   // Blinken mit ~8 Hz
        if (state == _shiftBadgeState) return;
        _shiftBadgeState = state;

        switch (state)
        {
            case 0:
                ShiftBadge.Background = ShiftIdleBg;
                TxtShiftBadge.Foreground = ShiftIdleFg;
                TxtShiftBadge.Text = "DREHZAHL OK";
                break;
            case 1:
                ShiftBadge.Background = ShiftHotBgA;
                TxtShiftBadge.Foreground = ShiftHotFg;
                TxtShiftBadge.Text = ">>> SHIFT! <<<";
                break;
            default:
                ShiftBadge.Background = ShiftHotBgB;
                TxtShiftBadge.Foreground = ShiftHotFg;
                TxtShiftBadge.Text = ">>> SHIFT! <<<";
                break;
        }
    }

    // =====================================================================================
    //  Panel 2 – Kamm'scher Kreis
    // =====================================================================================

    /// <summary>0 = voller Grip, 1 = Untersteuern/Drift, 2 = Traktionsabriss.</summary>
    private static int Classify(double ax, double ay)
    {
        double util = Math.Sqrt(ax * ax + ay * ay) / TrackSimulator.Mu;
        return util >= GripLimitUtil ? 2 : util >= GripWarnUtil ? 1 : 0;
    }

    private void UpdateGripBadge(int cls)
    {
        // Entprellung: schlechtere Klasse sofort anzeigen und 0,5 s halten
        if (cls > _gripShown)
        {
            _gripShown = cls;
            _gripHold = GripHoldTicks;
        }
        else if (_gripHold > 0)
        {
            _gripHold--;
            return;
        }
        else if (cls < _gripShown)
        {
            _gripShown = cls;
        }
        else
        {
            return;
        }

        GripBadge.Background = GripBg[_gripShown];
        TxtGripBadge.Foreground = GripFg[_gripShown];
        TxtGripBadge.Text = GripText[_gripShown];

        // Traktionsabrisse zählen (steigende Flanke auf Klasse 2)
        if (_gripShown == 2 && _prevClass != 2) _slipCount++;
        _prevClass = _gripShown;
    }

    private void UpdateGgTrail(double ax, double ay, int cls)
    {
        int n = _axBuf.Count;
        _axBuf.CopyTo(_axCopy);       // vorallokierte Ziel-Arrays → keine Allokation
        _ayBuf.CopyTo(_ayCopy);

        int g = 0, o = 0, r = 0;
        for (int i = 0; i < n; i++)
        {
            double x = _ayCopy[i];     // horizontale Achse = Querbeschleunigung
            double y = _axCopy[i];     // vertikale Achse   = Längsbeschleunigung
            switch (Classify(y, x))
            {
                case 0: _greenX[g] = x; _greenY[g] = y; g++; break;
                case 1: _orangeX[o] = x; _orangeY[o] = y; o++; break;
                default: _redX[r] = x; _redY[r] = y; r++; break;
            }
        }

        SetRange(_ggGreen, _greenX, _greenY, g);
        SetRange(_ggOrange, _orangeX, _orangeY, o);
        SetRange(_ggRed, _redX, _redY, r);

        _ggMarker.X = ay;
        _ggMarker.Y = ax;
        _ggMarker.MarkerColor = cls == 0 ? Colors.White : cls == 1 ? Colors.Orange : Colors.Red;
    }

    /// <summary>Begrenzt die gezeichneten Punkte einer Serie; bei 0 Punkten wird ein Dummy außerhalb des Sichtbereichs gesetzt.</summary>
    private static void SetRange(Scatter s, double[] xs, double[] ys, int count)
    {
        if (count == 0)
        {
            xs[0] = ys[0] = 99.0;
            count = 1;
        }
        s.MinRenderIndex = 0;
        s.MaxRenderIndex = count - 1;
    }

    // =====================================================================================
    //  Panel 3 – Histogramm der Bremsverzögerung
    // =====================================================================================
    private void AddBrakeSample(double decelG)
    {
        int idx = (int)((decelG - HistMin) / BinWidth);
        idx = Math.Clamp(idx, 0, HistBins - 1);
        _hist[idx]++;
        _brakeStats.Add(decelG);
    }

    private void UpdateHistogram()
    {
        int max = 0;
        for (int i = 0; i < HistBins; i++)
        {
            _bars[i].Value = _hist[i];
            if (_hist[i] > max) max = _hist[i];
        }
        PlotBrake.Plot.Axes.SetLimitsY(0, Math.Max(10, max * 1.3));

        // Gauß-Glocke aus Welford-Mittelwert und -Standardabweichung, skaliert auf Sample-Anzahl × Klassenbreite
        if (_brakeStats.Count >= 20 && _brakeStats.StdDev > 1e-6)
        {
            double mu = _brakeStats.Mean;
            double sd = _brakeStats.StdDev;
            double scale = _brakeStats.Count * BinWidth / (sd * Math.Sqrt(2.0 * Math.PI));

            for (int j = 0; j < GaussPoints; j++)
            {
                double z = (_gaussX[j] - mu) / sd;
                _gaussY[j] = scale * Math.Exp(-0.5 * z * z);
            }

            _brakeMean.X = mu;
            _gauss.IsVisible = _brakeMean.IsVisible = true;
        }
    }

    // =====================================================================================
    //  Panel 4 – MSAGL-Streckentopologie
    // =====================================================================================
    private void EnsureGraph()
    {
        if (_viewer is not null) return;

        _viewer = new MsaglWpf.GraphViewer();
        _viewer.BindToPanel(GraphHost);

        var graph = new Msagl.Graph("Rundkurs");
        graph.Attr.BackgroundColor = GraphBackground;

        // Fahrtrichtung: Sektor 1 → Hairpin → Sektor 2 → Chicane → Sektor 3 → (Boxengasse) → Sektor 1
        graph.AddEdge("Sektor 1", "Hairpin");
        graph.AddEdge("Hairpin", "Sektor 2");
        graph.AddEdge("Sektor 2", "Chicane");
        graph.AddEdge("Chicane", "Sektor 3");
        graph.AddEdge("Sektor 3", "Sektor 1");                                 // Start/Ziel-Linie
        graph.AddEdge("Sektor 3", "Boxengasse").Attr.AddStyle(Msagl.Style.Dashed);
        graph.AddEdge("Boxengasse", "Sektor 1").Attr.AddStyle(Msagl.Style.Dashed);

        foreach (Msagl.Edge edge in graph.Edges)
            edge.Attr.Color = new MsColor(150, 150, 150);

        foreach (Msagl.Node node in graph.Nodes)
        {
            node.Attr.Shape = Msagl.Shape.Box;
            ApplyNodeStyle(node, active: false);
        }

        _viewer.Graph = graph;

        _vnodes.Clear();
        foreach (Msagl.IViewerObject vo in _viewer.Entities)
        {
            if (vo is Msagl.IViewerNode vn)
                _vnodes[vn.Node.Id] = vn;
        }

        HighlightSegment(_sim.SegmentIndex);
    }

    private static void ApplyNodeStyle(Msagl.Node node, bool active)
    {
        node.Attr.FillColor = active ? NodeActiveFill : NodeInactiveFill;
        node.Attr.Color = active ? NodeActiveLine : NodeInactiveLine;
        node.Attr.LineWidth = active ? 3 : 1;
        node.Label.FontColor = active ? NodeActiveFont : NodeInactiveFont;
        node.Label.FontSize = 12;
    }

    private void HighlightSegment(int segmentIndex)
    {
        if (_viewer is null) return;

        if (_activeNode is not null)
        {
            ApplyNodeStyle(_activeNode.Node, active: false);
            _viewer.Invalidate(_activeNode);
            _activeNode = null;
        }

        if (_vnodes.TryGetValue(TrackSimulator.Segments[segmentIndex].Name, out Msagl.IViewerNode? vn))
        {
            ApplyNodeStyle(vn.Node, active: true);
            _viewer.Invalidate(vn);
            _activeNode = vn;
        }
    }

    // =====================================================================================
    //  Dashboard-Texte (5 Hz – String-Formatierung für WPF-Text erzeugt unvermeidbar kurzlebige Strings)
    // =====================================================================================
    private void UpdateTexts()
    {
        double util = _sim.ResultantG / TrackSimulator.Mu;

        TxtSpeed.Text = $"{_sim.SpeedKmh:F0}";
        TxtRpm.Text = $"{_sim.Rpm:F0}";
        TxtGear.Text = $"{_sim.Gear}";
        TxtAx.Text = $"{_sim.AxG:+0.00;-0.00}";
        TxtAy.Text = $"{_sim.AyG:+0.00;-0.00}";
        TxtUtil.Text = $"{util * 100:F0}%";
        TxtSegment.Text = TrackSimulator.Segments[_sim.SegmentIndex].Name;

        string last = _sim.LastLapTime > 0 ? $"{_sim.LastLapTime:F2} s" : "--.- s";
        string best = _bestLap < double.MaxValue ? $"{_bestLap:F2} s" : "--.- s";
        string lapAvg = _lapStats.Count >= 2
            ? $"{_lapStats.Mean:F2} ± {_lapStats.StdDev:F2} s"
            : "--.- s";
        string shift = _shiftStats.Count >= 2
            ? $"{_shiftStats.Mean:F0} ± {_shiftStats.StdDev:F0}"
            : "----";
        string brake = _brakeStats.Count >= 2
            ? $"{_brakeStats.Mean:F2} ± {_brakeStats.StdDev:F2} g"
            : "-.-- g";

        TxtFooter.Text =
            $"Runde {_sim.Lap}{(_sim.PitThisLap ? " (Boxenstopp)" : "")} {_sim.LapTime,5:F1} s | " +
            $"Letzte {last} | Beste {best} | Ø Rundenzeit {lapAvg} (n={_lapStats.Count})\n" +
            $"Ausdrehzahl Ø±σ {shift} 1/min (n={_shiftStats.Count}) | Shift-Flashes {_flashCount} | " +
            $"Traktionsabrisse {_slipCount} | Bremsen Ø±σ {brake} (n={_brakeStats.Count})";
    }

    // =====================================================================================
    //  Buttons
    // =====================================================================================
    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled)
        {
            _userPaused = true;
            _timer.Stop();
            BtnPause.Content = "Fortsetzen";
        }
        else
        {
            _userPaused = false;
            _timer.Start();
            BtnPause.Content = "Pause";
        }
    }

    private void BtnSlide_Click(object sender, RoutedEventArgs e) => _sim.InjectSlide();

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _sim.Reset();
        _shiftStats.Reset();
        _brakeStats.Reset();
        _lapStats.Reset();
        _axBuf.Clear();
        _ayBuf.Clear();
        _speedStreamer.Clear();
        _rpmStreamer.Clear();
        Array.Clear(_hist);

        _bestLap = double.MaxValue;
        _flashCount = 0;
        _flashTicks = 0;
        _slipCount = 0;
        _prevClass = 0;
        _gripHold = 0;
        _gripShown = -1;
        _shiftBadgeState = -1;
        _tick = 0;

        _shiftMean.IsVisible = _shiftUpper.IsVisible = _shiftLower.IsVisible = false;
        _gauss.IsVisible = _brakeMean.IsVisible = false;
        for (int i = 0; i < HistBins; i++) _bars[i].Value = 0;

        SetRange(_ggGreen, _greenX, _greenY, 0);
        SetRange(_ggOrange, _orangeX, _orangeY, 0);
        SetRange(_ggRed, _redX, _redY, 0);
        _ggMarker.X = _ggMarker.Y = 99.0;

        UpdateShiftFlash(0);
        UpdateGripBadge(0);
        HighlightSegment(_sim.SegmentIndex);
        UpdateTexts();

        PlotSpeedRpm.Refresh();
        PlotGG.Refresh();
        PlotBrake.Refresh();
    }
}