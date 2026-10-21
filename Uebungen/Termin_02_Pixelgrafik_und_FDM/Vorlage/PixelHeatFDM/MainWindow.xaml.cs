using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PixelHeatFDM.Rendering;
using PixelHeatFDM.Simulation;

namespace PixelHeatFDM;

public partial class MainWindow : Window
{
    private const int GridResolution = 128;
    private readonly HeatGrid _grid;
    private readonly BitmapHeatRenderer _renderer;
    private readonly DispatcherTimer _timer;

    private int _stepCount = 0;
    private bool _leftPainting;
    private bool _rightPainting;

    public MainWindow()
    {
        InitializeComponent();

        _grid = new HeatGrid(GridResolution, GridResolution);
        _renderer = new BitmapHeatRenderer(GridResolution, GridResolution);

        HeatImage.Source = _renderer.Bitmap;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ca. 30 FPS
        };
        _timer.Tick += OnSimulationTick;

        UpdateUiFromParameters();
        _renderer.Render(_grid);
    }

    private void OnSimulationTick(object? sender, EventArgs e)
    {
        // Physikalischer FDM-Schritt
        _grid.Step();
        _stepCount++;

        // Visualisierung aktualisieren
        _renderer.Render(_grid);

        TxtStatus.Text = $"Schritte: {_stepCount} | s = {_grid.StabilityFactor:F4} | Status: {(_grid.IsStable ? "OK" : "INSTABIL")}";
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_grid == null || TxtDtValue == null) return;

        _grid.Dt = (float)SliderDt.Value;
        _grid.Alpha = (float)SliderAlpha.Value;

        UpdateUiFromParameters();
    }

    private void UpdateUiFromParameters()
    {
        TxtDtValue.Text = $"{_grid.Dt:F4} s";
        TxtAlphaValue.Text = $"{_grid.Alpha:E2}";

        float s = _grid.StabilityFactor;
        TxtStabilityFactor.Text = $"s = {s:F4} (Grenze: 0.2500)";
        TxtCriticalDt.Text = $"dt_krit = {_grid.CriticalDt:F5} s";

        if (_grid.IsStable)
        {
            StabilityBadge.Background = new SolidColorBrush(Color.FromRgb(27, 77, 27));
            TxtStabilityFactor.Foreground = new SolidColorBrush(Color.FromRgb(152, 251, 152));
            TxtStabilityStatus.Text = "STABIL: Keine numerische Oszillation";
        }
        else
        {
            StabilityBadge.Background = new SolidColorBrush(Color.FromRgb(120, 20, 20));
            TxtStabilityFactor.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
            TxtStabilityStatus.Text = "WARNUNG: s > 0.25! Gitter explodiert!";
        }
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
        _timer.Stop();
        BtnStartStop.Content = "Start";
        _stepCount = 0;
        _grid.Reset();
        _renderer.Render(_grid);
        TxtStatus.Text = "Simulation zurückgesetzt.";
    }

    private void HeatImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _leftPainting = e.ChangedButton == MouseButton.Left;
        _rightPainting = e.ChangedButton == MouseButton.Right;

        HeatImage.CaptureMouse();
        PaintAtMousePosition(e.GetPosition(HeatImage));
    }

    private void HeatImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (_leftPainting || _rightPainting)
        {
            PaintAtMousePosition(e.GetPosition(HeatImage));
        }
    }

    private void HeatImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            _leftPainting = false;

        if (e.ChangedButton == MouseButton.Right)
            _rightPainting = false;

        if (!_leftPainting && !_rightPainting)
            HeatImage.ReleaseMouseCapture();
    }

    private void RbBoundary_Checked(object sender, RoutedEventArgs e)
    {
        if (_grid == null) return;

        if (RbNeumann?.IsChecked == true)
        {
            _grid.Boundary = BoundaryMode.NeumannAdiabatic;
        }
        else
        {
            _grid.Boundary = BoundaryMode.DirichletFixed;
        }
    }

    private void PaintAtMousePosition(Point pos)
    {
        if (HeatImage.ActualWidth <= 0 || HeatImage.ActualHeight <= 0)
            return;

        int gx = (int)(pos.X / HeatImage.ActualWidth * _grid.Width);
        int gy = (int)(pos.Y / HeatImage.ActualHeight * _grid.Height);

        gx = Math.Clamp(gx, 0, _grid.Width - 1);
        gy = Math.Clamp(gy, 0, _grid.Height - 1);

        if (_leftPainting)
            _grid.PaintHotspot(gx, gy, 2);

        if (_rightPainting)
            _grid.PaintInsulator(gx, gy, 2);
    }
}
