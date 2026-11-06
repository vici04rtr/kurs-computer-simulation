using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlotTelemetry.Data;
using ScottPlotTelemetry.Statistics;

namespace ScottPlotTelemetry;

public partial class MainWindow : Window
{
    private const int BufferCapacity = 500;
    private readonly CircularBuffer<double> _buffer = new(BufferCapacity);
    private readonly WelfordStatistics _welford = new();
    private readonly DispatcherTimer _timer;
    private readonly Random _random = new(42);

    private readonly DataStreamer _streamer;
    private readonly DataStreamer _referenceStreamer; // 2. Kanal für Sollwert
    private readonly HorizontalLine _meanLine;
    private readonly HorizontalLine _upperLine;
    private readonly HorizontalLine _lowerLine;

    private double _simTime = 0.0;
    private bool _injectAnomalyFlag = false;

    // Merkt sich, ob Stufe A lief, als auf Track B umgeschaltet wurde
    private bool _stageAWasRunning = true;

    // Vorallokiertes Array für das gleitende Fenster (0 Byte GC-Allokation im Loop)
    private readonly double[] _windowCopy = new double[BufferCapacity];

    // Entprellung & Anomalie-Zähler
    private int _anomalyCount = 0;
    private int _anomalyHoldTicks = 0;

    // Statische, eingefrorene Brushes zur Vermeidung von GC-Leaks im 50-Hz-Loop
    private static readonly SolidColorBrush NormalBgBrush = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 77, 27)));
    private static readonly SolidColorBrush NormalFgBrush = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(152, 251, 152)));
    private static readonly SolidColorBrush AnomalyBgBrush = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 30, 30)));
    private static readonly SolidColorBrush AnomalyFgBrush = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 120, 120)));

    private static SolidColorBrush FreezeBrush(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public MainWindow()
    {
        InitializeComponent();

        // ScottPlot 5 Konfiguration
        Plot1.Plot.Title("Echtzeit-Signaltelemetrie & Dynamisches 3-Sigma-Toleranzband");
        Plot1.Plot.Axes.Left.Label.Text = "Amplitude [Einheit]";
        Plot1.Plot.Axes.Bottom.Label.Text = "Abtastpunkte";

        // Dunkles Farbschema passend zum UI
        Plot1.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#141414");
        Plot1.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        Plot1.Plot.Axes.Color(ScottPlot.Color.FromHex("#CCCCCC"));
        Plot1.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2E2E2E");

        // High-Performance DataStreamer (Kanal 1: Messsignal)
        _streamer = Plot1.Plot.Add.DataStreamer(BufferCapacity);
        _streamer.ViewScrollLeft();
        _streamer.Color = ScottPlot.Color.FromHex("#00D2FF");

        // High-Performance DataStreamer (Kanal 2: Ideales Referenzsignal in Grün/Orange)
        _referenceStreamer = Plot1.Plot.Add.DataStreamer(BufferCapacity);
        _referenceStreamer.ViewScrollLeft();
        _referenceStreamer.Color = ScottPlot.Color.FromHex("#00FF88");

        // Horizontale Welford-Statistiklinien
        _meanLine = Plot1.Plot.Add.HorizontalLine(100.0);
        _meanLine.Color = ScottPlot.Colors.Yellow;
        _meanLine.LineWidth = 1.5f;

        _upperLine = Plot1.Plot.Add.HorizontalLine(115.0);
        _upperLine.Color = ScottPlot.Colors.Red;
        _upperLine.LineWidth = 1.5f;
        _upperLine.LinePattern = LinePattern.Dashed;

        _lowerLine = Plot1.Plot.Add.HorizontalLine(85.0);
        _lowerLine.Color = ScottPlot.Colors.Red;
        _lowerLine.LineWidth = 1.5f;
        _lowerLine.LinePattern = LinePattern.Dashed;

        Plot1.Plot.Axes.SetLimitsY(50, 150);

        // Streaming-Timer (50 Hz = 20 ms)
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _timer.Tick += OnTelemetryTick;
        _timer.Start();
    }

    private void OnTelemetryTick(object? sender, EventArgs e)
    {
        _simTime += 0.02;

        // Signalmodell: Harmonische Schwingung (0.5 Hz) mit normalverteiltem Rauschen
        double noise = (_random.NextDouble() - 0.5) * 6.0;
        double signal = 100.0 + 15.0 * Math.Sin(2.0 * Math.PI * 0.5 * _simTime) + noise;

        // Ideales Referenzsignal (ohne Rauschen)
        double referenceSignal = 100.0 + 15.0 * Math.Sin(2.0 * Math.PI * 0.5 * _simTime);

        if (_injectAnomalyFlag)
        {
            signal += 45.0; // Künstlicher Stoß / Peak
            _injectAnomalyFlag = false;
        }

        // Puffer & Welford aktualisieren (0 B GC Heap-Allokationen)
        _buffer.Add(signal);
        _welford.Add(signal);

        // ScottPlot Streamer befüllen
        _streamer.Add(signal);
        _referenceStreamer.Add(referenceSignal);

        // Gleitende Fenster-Auswertung (letzte 10 Sekunden / 500 Samples)
        int currentCount = _buffer.Count;
        if (currentCount > 0)
        {
            _buffer.CopyTo(_windowCopy);
            double windowSum = 0;
            double windowMin = double.MaxValue;
            double windowMax = double.MinValue;

            for (int i = 0; i < currentCount; i++)
            {
                double val = _windowCopy[i];
                windowSum += val;
                if (val < windowMin) windowMin = val;
                if (val > windowMax) windowMax = val;
            }
            double windowMean = windowSum / currentCount;

            TxtFooter.Text = $"10s-Fenster (N={currentCount}): Mittel = {windowMean:F2} | Min = {windowMin:F2} | Max = {windowMax:F2} | Anomalien: {_anomalyCount}";
        }

        // Welford-Grenzlinien anpassen
        if (_welford.Count > 10)
        {
            _meanLine.Y = _welford.Mean;
            var (lower, upper) = _welford.GetToleranceBand(3.0);
            _upperLine.Y = upper;
            _lowerLine.Y = lower;


            // Anomalie-Prüfung & Entprellung (Haltezeit 25 Ticks = 0.5s)

            bool isAnomaly = _welford.IsAnomaly(signal, 3.0);
            if (isAnomaly)
            {
                _anomalyHoldTicks = 25;
                _anomalyCount++;
            }

            if (_anomalyHoldTicks > 0)
            {
                _anomalyHoldTicks--;
                StatusBadge.Background = AnomalyBgBrush;
                TxtStatusBadge.Text = "! ANOMALIE ERKANNT (> 3 SIGMA) !";
                TxtStatusBadge.Foreground = AnomalyFgBrush;
            }
            else
            {
                StatusBadge.Background = NormalBgBrush;
                TxtStatusBadge.Text = "SIGNAL IM NORMALBEREICH";
                TxtStatusBadge.Foreground = NormalFgBrush;
            }

            TxtToleranceBand.Text = $"[{lower:F1} ... {upper:F1}]";
        }

        // Dashboard-Zahlen aktualisieren
        TxtCount.Text = $"{_welford.Count:N0}";
        TxtCurrent.Text = $"{signal:F2}";
        TxtMean.Text = $"{_welford.Mean:F2}";
        TxtStdDev.Text = $"{_welford.StdDev:F2}";
        TxtVariance.Text = $"{_welford.Variance:F2}";

        Plot1.Refresh();
    }

    private void BtnStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            BtnStartStop.Content = "Fortsetzen";
        }
        else
        {
            _timer.Start();
            BtnStartStop.Content = "Pause";
        }
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _buffer.Clear();
        _welford.Reset();
        _streamer.Clear();
        _referenceStreamer.Clear();
        _simTime = 0.0;
        _anomalyCount = 0;
        _anomalyHoldTicks = 0;
        Plot1.Refresh();

        TxtCount.Text = "0";
        TxtCurrent.Text = "--.-";
        TxtMean.Text = "--.-";
        TxtStdDev.Text = "--.-";
        TxtVariance.Text = "--.-";
        TxtToleranceBand.Text = "[--.- ... --.-]";
        StatusBadge.Background = NormalBgBrush;
        TxtStatusBadge.Text = "SIGNAL IM NORMALBEREICH";
        TxtFooter.Text = "Telemetrie zurückgesetzt. Abtastrate: 50 Hz.";
    }

    private void BtnInjectAnomaly_Click(object sender, RoutedEventArgs e)
    {
        _injectAnomalyFlag = true;
    }

    private void CmbView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Das Event feuert schon während InitializeComponent() → dann nichts tun
        if (!IsLoaded || StageAHost is null || RacingView is null) return;

        bool trackB = CmbView.SelectedIndex == 1;

        StageAHost.Visibility = trackB ? Visibility.Collapsed : Visibility.Visible;
        RacingView.Visibility = trackB ? Visibility.Visible : Visibility.Collapsed;

        if (trackB)
        {
            // Stufe-A-Timer anhalten; Track B startet sich selbst (IsVisibleChanged)
            _stageAWasRunning = _timer.IsEnabled;
            _timer.Stop();
        }
        else if (_stageAWasRunning)
        {
            // Nur fortsetzen, wenn Stufe A vorher nicht manuell pausiert war
            _timer.Start();
        }
    }
}