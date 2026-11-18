using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SceneGraph3D.Camera;
using SceneGraph3D.Scene;
using SharpGL;
using SharpGL.WPF;

namespace SceneGraph3D;

/// <summary>Umschaltbare Aufgabenstufen im Hauptfenster (ComboBox).</summary>
public enum AppStage
{
    Stage1Scara,
    Stage2ClawCrane
}

public partial class MainWindow : Window
{
    // ------------------------------------------------------------------ Modus & Kameras
    private AppStage _stage = AppStage.Stage1Scara;
    private bool _uiReady;

    // Jede Stufe behält ihre eigene Kameraeinstellung
    private readonly OrbitCamera _cameraScara = CreateScaraCamera();
    private readonly OrbitCamera _cameraCrane = CreateCraneCamera();
    private OrbitCamera ActiveCamera => _stage == AppStage.Stage1Scara ? _cameraScara : _cameraCrane;

    private bool _isOrbiting = false;   // linke Maustaste
    private bool _isPanning = false;    // rechte Maustaste
    private Point _lastMousePos;

    // ------------------------------------------------------------------ Stufe 1: SCARA
    private const float HBase = 0.5f;
    private const float L1 = 1.0f;
    private const float L2 = 0.8f;

    private const float WorkpieceEdge = 0.12f;      // Kantenlänge [m]
    private const float GrabDistance = 0.12f;       // Greifdistanz [m]

    private float _theta1Deg = 30.0f;
    private float _theta2Deg = -45.0f;
    private float _d3 = 0.15f;
    private bool _workpieceInRange = false;

    private readonly SceneNode _rootNode = new("Root");
    private readonly SceneNode _baseNode = new("Base");
    private readonly SceneNode _arm1Node = new("UpperArm");
    private readonly SceneNode _arm2Node = new("ForeArm");
    private readonly SceneNode _pinoleNode = new("Pinole");
    private readonly SceneNode _workpieceNode = new("Workpiece");

    // ------------------------------------------------------------------ Stufe 2: Claw Crane
    private readonly ClawCraneScene _crane = new();
    private readonly HashSet<Key> _pressedKeys = new();
    private bool _suppressCraneUi = false; // gegen Rückkopplung

    private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTickSeconds;

    public MainWindow()
    {
        InitializeComponent();
        BuildSceneGraph();

        _gameTimer.Tick += GameTimer_Tick;
        _lastTickSeconds = _clock.Elapsed.TotalSeconds;
        _gameTimer.Start();
        Deactivated += (_, _) => _pressedKeys.Clear();

        _uiReady = true;
        UpdateTcpCalculations();
        ApplyStage();
    }

    private static OrbitCamera CreateScaraCamera() => new()
    {
        AzimuthDeg = 45.0, ElevationDeg = 30.0, Distance = 8.0,
        LookAtX = 0.0, LookAtY = 0.5, LookAtZ = 0.0
    };

    private static OrbitCamera CreateCraneCamera() => new()
    {
        AzimuthDeg = 22.0, ElevationDeg = 20.0, Distance = 9.5,
        LookAtX = 0.0, LookAtY = 1.2, LookAtZ = 0.0
    };

    // =====================================================================================
    // Stufe 1: Szenengraph
    // =====================================================================================
    private void BuildSceneGraph()
    {
        // Hierarchie: Root -> Base -> Arm1 -> Arm2 -> Pinole ; Root -> Workpiece
        _rootNode.AddChild(_baseNode);
        _baseNode.AddChild(_arm1Node);
        _arm1Node.AddChild(_arm2Node);
        _arm2Node.AddChild(_pinoleNode);
        _rootNode.AddChild(_workpieceNode);

        // Aufgabe 1.1: jeder Knoten zeichnet seine Geometrie im LOKALEN Koordinatensystem.
        // Die Skalierung (gl.Scale) bleibt bewusst innerhalb von Push/Pop in der Zeichenlogik,
        // damit Kindknoten nicht verzerrt werden (nicht _arm1Node.Sx = L1 setzen!).

        // Basis (statischer Zylinder)
        _baseNode.RenderCustomGeometry = gl =>
        {
            gl.Color(0.4f, 0.45f, 0.5f);
            GlPrimitives.Cylinder(gl, 0.22f, HBase, 24);
        };

        // Gelenk 1 (Schultergehäuse) & Arm 1
        _arm1Node.RenderCustomGeometry = gl =>
        {
            gl.Color(0.2f, 0.6f, 0.9f);
            GlPrimitives.Sphere(gl, 0.18f);

            gl.PushMatrix();
            gl.Translate(L1 / 2.0f, 0.0f, 0.0f);
            gl.Scale(L1, 0.14f, 0.18f);
            GlPrimitives.Cube(gl);
            gl.PopMatrix();
        };

        // Gelenk 2 (Ellenbogen) & Arm 2
        _arm2Node.RenderCustomGeometry = gl =>
        {
            gl.Color(0.2f, 0.8f, 0.4f);
            GlPrimitives.Sphere(gl, 0.15f);

            gl.PushMatrix();
            gl.Translate(L2 / 2.0f, 0.0f, 0.0f);
            gl.Scale(L2, 0.12f, 0.15f);
            GlPrimitives.Cube(gl);
            gl.PopMatrix();
        };

        // Pinole: vertikaler Schaft + Greiferkopf (TCP im Ursprung des Knotens)
        _pinoleNode.RenderCustomGeometry = gl =>
        {
            gl.Color(0.85f, 0.85f, 0.85f);
            gl.PushMatrix();
            gl.Translate(0.0f, 0.15f, 0.0f);
            GlPrimitives.Cylinder(gl, 0.035f, 0.35f, 16);
            gl.PopMatrix();

            gl.Color(0.9f, 0.3f, 0.2f);
            GlPrimitives.Sphere(gl, 0.07f);
        };

        // Aufgabe 1.3: Werkstück (Würfel 0,12 m) auf dem Hallenboden, grün bei Greifdistanz
        _workpieceNode.Tx = 1.10f;
        _workpieceNode.Ty = 0.06f;
        _workpieceNode.Tz = -0.40f;
        _workpieceNode.RenderCustomGeometry = gl =>
        {
            if (_workpieceInRange) gl.Color(0.2f, 0.85f, 0.25f);   // grün
            else gl.Color(1.0f, 0.55f, 0.05f);                     // orange

            gl.PushMatrix();
            gl.Scale(WorkpieceEdge, WorkpieceEdge, WorkpieceEdge);
            GlPrimitives.Cube(gl);
            gl.PopMatrix();
        };

        UpdateJointTransforms();
    }

    private void UpdateJointTransforms() // Vorwärtskinematik
    {
        // Basis: steht auf dem Boden
        _baseNode.Tx = 0.0f;
        _baseNode.Ty = 0.0f;
        _baseNode.Tz = 0.0f;

        // Arm 1: sitzt oben auf der Basis, rotiert um Y (Schulter theta1)
        _arm1Node.Tx = 0.0f;
        _arm1Node.Ty = HBase;
        _arm1Node.Tz = 0.0f;
        _arm1Node.Ry = _theta1Deg;

        // Arm 2: am Ende von Arm 1 (L1 in X), rotiert um Y (Ellenbogen theta2)
        _arm2Node.Tx = L1;
        _arm2Node.Ty = 0.0f;
        _arm2Node.Tz = 0.0f;
        _arm2Node.Ry = _theta2Deg;

        // Pinole: am Ende von Arm 2 (L2 in X), Vertikalhub -d3
        _pinoleNode.Tx = L2;
        _pinoleNode.Ty = -_d3;
        _pinoleNode.Tz = 0.0f;
    }

    // =====================================================================================
    // OpenGL
    // =====================================================================================
    private void OpenGlView_OpenGLInitialized(object sender, OpenGLRoutedEventArgs args)
    {
        OpenGL gl = args.OpenGL;

        gl.Enable(OpenGL.GL_DEPTH_TEST);

        // Phong-Beleuchtungsmodell
        gl.Enable(OpenGL.GL_LIGHTING);
        gl.Enable(OpenGL.GL_LIGHT0);

        // Normalen nach Skalierung wieder auf Länge 1 bringen (sonst falsche Helligkeit an skalierten Quadern)
        gl.Enable(OpenGL.GL_NORMALIZE);

        gl.Enable(OpenGL.GL_COLOR_MATERIAL);
        gl.ColorMaterial(OpenGL.GL_FRONT_AND_BACK, OpenGL.GL_AMBIENT_AND_DIFFUSE);

        // Materialglanz (spekularer Anteil des Phong-Modells)
        gl.Material(OpenGL.GL_FRONT_AND_BACK, OpenGL.GL_SPECULAR, new float[] { 0.6f, 0.6f, 0.6f, 1.0f });
        gl.Material(OpenGL.GL_FRONT_AND_BACK, OpenGL.GL_SHININESS, new float[] { 48.0f });

        gl.ShadeModel(OpenGL.GL_SMOOTH);
        gl.ClearColor(0.08f, 0.08f, 0.08f, 1.0f);
    }

    private void OpenGlView_Resized(object sender, OpenGLRoutedEventArgs args)
    {
        OpenGL gl = args.OpenGL;
        gl.MatrixMode(OpenGL.GL_PROJECTION);
        gl.LoadIdentity();

        double aspect = OpenGlView.ActualWidth / Math.Max(OpenGlView.ActualHeight, 1.0);
        gl.Perspective(45.0, aspect, 0.1, 100.0);

        gl.MatrixMode(OpenGL.GL_MODELVIEW);
    }

    private void OpenGlView_OpenGLDraw(object sender, OpenGLRoutedEventArgs args)
    {
        OpenGL gl = args.OpenGL;

        if (_stage == AppStage.Stage2ClawCrane)
            gl.ClearColor(0.05f, 0.06f, 0.10f, 1.0f);
        else
            gl.ClearColor(0.08f, 0.08f, 0.08f, 1.0f);

        gl.Clear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT);
        gl.LoadIdentity();

        // Hauptlicht GL_LIGHT0 (an die Kamera gebunden, da Modelview hier noch Identität ist)
        bool crane = _stage == AppStage.Stage2ClawCrane;
        float amb = crane ? 0.18f : 0.25f;
        float dif = crane ? 0.45f : 0.85f;
        gl.Light(OpenGL.GL_LIGHT0, OpenGL.GL_POSITION, new float[] { 10.0f, 20.0f, 15.0f, 1.0f });
        gl.Light(OpenGL.GL_LIGHT0, OpenGL.GL_AMBIENT, new float[] { amb, amb, amb, 1.0f });
        gl.Light(OpenGL.GL_LIGHT0, OpenGL.GL_DIFFUSE, new float[] { dif, dif, dif, 1.0f });
        gl.Light(OpenGL.GL_LIGHT0, OpenGL.GL_SPECULAR, new float[] { 1.0f, 1.0f, 1.0f, 1.0f });

        // 1. Orbit-Kamera anwenden
        ActiveCamera.Apply(gl);

        if (_stage == AppStage.Stage1Scara)
        {
            gl.Disable(OpenGL.GL_LIGHT1);

            GlPrimitives.Axes(gl, 2.0f);
            GlPrimitives.FloorGrid(gl, 4.0f, 0.5f);

            RenderRobot(gl);       // Roboter + Werkstück über den Szenengraphen
            DrawTcpSphere(gl);     // analytischer TCP (gelb)
        }
        else
        {
            // Spotlight-Position gilt in Weltkoordinaten -> nach der Kamera setzen
            _crane.ApplySpotlight(gl);

            GlPrimitives.FloorGrid(gl, 6.0f, 0.5f, -0.7f);
            var (ex, ey, ez) = _cameraCrane.GetEyePosition();
            _crane.Render(gl, ex, ey, ez);
        }
    }

    /// <summary>Aufgabe 1.1: Der gesamte Roboter (und das Werkstück) wird über den Szenengraphen gezeichnet.</summary>
    private void RenderRobot(OpenGL gl)
    {
        _rootNode.Render(gl);
    }

    private void DrawTcpSphere(OpenGL gl)
    {
        var (xAna, yAna, zAna) = CalculateAnalyticTcp();

        gl.Disable(OpenGL.GL_LIGHTING);
        gl.Color(1.0f, 1.0f, 0.0f);

        gl.PushMatrix();
        gl.Translate(xAna, yAna, zAna);
        GlPrimitives.Sphere(gl, 0.04f);
        gl.PopMatrix();

        gl.Enable(OpenGL.GL_LIGHTING);
    }

    private (float X, float Y, float Z) CalculateAnalyticTcp()
    {
        float t1 = _theta1Deg * MathF.PI / 180.0f;
        float t12 = (_theta1Deg + _theta2Deg) * MathF.PI / 180.0f;

        float x = L1 * MathF.Cos(t1) + L2 * MathF.Cos(t12);
        float z = -(L1 * MathF.Sin(t1) + L2 * MathF.Sin(t12));
        float y = HBase - _d3;

        return (x, y, z);
    }

    private void UpdateTcpCalculations()
    {
        var (xAna, yAna, zAna) = CalculateAnalyticTcp();

        UpdateJointTransforms();
        var pMatrix = _pinoleNode.GetWorldPosition();

        if (TxtTcpAnalytic != null)
            TxtTcpAnalytic.Text = $"X = {xAna:F3} m | Y = {yAna:F3} m | Z = {zAna:F3} m";

        if (TxtTcpMatrix != null)
            TxtTcpMatrix.Text = $"X = {pMatrix.X:F3} m | Y = {pMatrix.Y:F3} m | Z = {pMatrix.Z:F3} m";

        float diff = MathF.Sqrt(
            MathF.Pow(xAna - pMatrix.X, 2) +
            MathF.Pow(yAna - pMatrix.Y, 2) +
            MathF.Pow(zAna - pMatrix.Z, 2)) * 1000.0f; // mm

        if (TxtTcpDiff != null)
            TxtTcpDiff.Text = $"Deckungsfehler Delta = {diff:F3} mm";

        // Aufgabe 1.3: euklidischer 3D-Abstand TCP <-> Werkstück
        Vector3 pWork = _workpieceNode.GetWorldPosition();
        float d = Vector3.Distance(pMatrix, pWork);
        _workpieceInRange = d < GrabDistance;

        if (TxtStatus != null && _stage == AppStage.Stage1Scara)
        {
            string state = _workpieceInRange
                ? "GREIFDISTANZ erreicht (Werkstück grün)"
                : $"außerhalb Greifdistanz (d ≥ {GrabDistance:F2} m)";
            TxtStatus.Text = $"Stufe 1 | Abstand TCP–Werkstück d = {d:F3} m | {state}";
        }
    }

    // =====================================================================================
    // Modus-Umschaltung (ComboBox)
    // =====================================================================================
    private void ModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ApplyStage();
    }

    private void ApplyStage()
    {
        _stage = ModeCombo.SelectedIndex == 1 ? AppStage.Stage2ClawCrane : AppStage.Stage1Scara;
        _pressedKeys.Clear();

        bool crane = _stage == AppStage.Stage2ClawCrane;
        PanelStage1.Visibility = crane ? Visibility.Collapsed : Visibility.Visible;
        PanelStage2.Visibility = crane ? Visibility.Visible : Visibility.Collapsed;

        TxtTitle.Text = crane
            ? "Stufe 2 – Track B: 3D Arcade Claw Crane (Szenengraph, Greifmechanik, Spotlight)"
            : "3D-Visualisierung, Orbit-Kamera & Vorwärtskinematik-Szenengraph";
        BtnResetJoints.Content = crane ? "Spiel zurücksetzen" : "Gelenke nullen";

        if (crane) UpdateCraneUi();
        else UpdateTcpCalculations();
    }

    // =====================================================================================
    // Stufe 2: Spielschleife, Tastatur, UI
    // =====================================================================================
    private void GameTimer_Tick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)(now - _lastTickSeconds);
        _lastTickSeconds = now;

        if (_stage != AppStage.Stage2ClawCrane) return;

        // Pfeiltasten: links/rechts = X-Brücke, hoch/runter = Laufkatze in Z (hoch = von der Kamera weg)
        float dirX = (_pressedKeys.Contains(Key.Right) ? 1.0f : 0.0f) - (_pressedKeys.Contains(Key.Left) ? 1.0f : 0.0f);
        float dirZ = (_pressedKeys.Contains(Key.Down) ? 1.0f : 0.0f) - (_pressedKeys.Contains(Key.Up) ? 1.0f : 0.0f);
        _crane.MoveManual(dirX, dirZ, dt);

        _crane.Update(dt);
        UpdateCraneUi();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_stage != AppStage.Stage2ClawCrane || ModeCombo.IsDropDownOpen) return;

        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                _pressedKeys.Add(e.Key);
                e.Handled = true;      // verhindert, dass ComboBox/Slider die Pfeiltasten verbrauchen
                break;

            case Key.Space:
                if (!e.IsRepeat) _crane.StartCycle();
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_stage != AppStage.Stage2ClawCrane) return;

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            _pressedKeys.Remove(e.Key);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            e.Handled = true;
        }
    }

    private void CraneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _suppressCraneUi) return;
        if (_crane.Phase != ClawPhase.Idle) return;     // während des Zyklus steuert der Automat

        _crane.BridgeX = (float)SliderCraneX.Value;
        _crane.TrolleyZ = (float)SliderCraneZ.Value;
        _crane.ClawY = (float)SliderCraneY.Value;
        _crane.GammaDeg = (float)SliderCraneGamma.Value;
    }

    private void BtnStartCycle_Click(object sender, RoutedEventArgs e)
    {
        _crane.StartCycle();
    }

    private void ChkSpotlight_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _crane.SpotlightEnabled = ChkSpotlight.IsChecked == true;
    }

    private static string PhaseText(ClawPhase phase) => phase switch
    {
        ClawPhase.Idle => "Bereit (Pfeiltasten / Leertaste)",
        ClawPhase.Lowering => "1/4 Klaue senkt sich ab",
        ClawPhase.Closing => "2/4 Finger schließen sich",
        ClawPhase.Raising => "3/4 Klaue fährt nach oben",
        ClawPhase.MovingToChute => "4/4 Fahrt zum Auswurfschacht",
        ClawPhase.Opening => "4/4 Klaue öffnet sich",
        _ => ""
    };

    private void UpdateCraneUi()
    {
        // Slider spiegeln den Modellzustand (Pfeiltasten / Automat), ohne Rückkopplung
        _suppressCraneUi = true;
        SliderCraneX.Value = _crane.BridgeX;
        SliderCraneZ.Value = _crane.TrolleyZ;
        SliderCraneY.Value = _crane.ClawY;
        SliderCraneGamma.Value = _crane.GammaDeg;
        _suppressCraneUi = false;

        bool manual = _crane.Phase == ClawPhase.Idle;
        SliderCraneX.IsEnabled = manual;
        SliderCraneZ.IsEnabled = manual;
        SliderCraneY.IsEnabled = manual;
        SliderCraneGamma.IsEnabled = manual;
        BtnStartCycle.IsEnabled = manual;

        TxtCraneX.Text = $"{_crane.BridgeX:F2} m";
        TxtCraneZ.Text = $"{_crane.TrolleyZ:F2} m";
        TxtCraneY.Text = $"{_crane.ClawY:F2} m";
        TxtCraneGamma.Text = $"{_crane.GammaDeg:F0}°";

        Vector3 c = _crane.ClawCenterWorld();
        var (prize, d) = _crane.NearestPrize();
        int total = _crane.Prizes.Count;

        TxtCranePhase.Text = "Phase: " + PhaseText(_crane.Phase);
        TxtCraneCenter.Text = $"Greifer-Zentrum:\nX = {c.X:F2} | Y = {c.Y:F2} | Z = {c.Z:F2} m";

        string nearest;
        if (_crane.HeldPrize != null) nearest = "Preis gegriffen";
        else if (prize == null) nearest = "kein Preis mehr am Boden";
        else nearest = $"d = {d:F3} m" + (d < ClawCraneScene.GrabRadius ? "  (in Greifreichweite)" : "");
        TxtCraneNearest.Text = "Nächster Preis: " + nearest;
        TxtCraneScore.Text = $"Im Schacht: {_crane.Score} / {total}";

        TxtStatus.Text = $"Stufe 2 | Claw Crane | {PhaseText(_crane.Phase)} | Greifer ({c.X:F2}, {c.Y:F2}, {c.Z:F2}) m | " +
                         $"Treffer: {_crane.Score}/{total}";
    }

    // =====================================================================================
    // Kamera-Maus-Events (links: Orbit, rechts: Panning, Rad: Zoom)
    // =====================================================================================
    private void OpenGlView_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) _isOrbiting = true;
        else if (e.ChangedButton == MouseButton.Right) _isPanning = true;
        else return;

        _lastMousePos = e.GetPosition(OpenGlView);
        OpenGlView.CaptureMouse();
    }

    private void OpenGlView_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isOrbiting && !_isPanning) return;

        Point currentPos = e.GetPosition(OpenGlView);
        double dx = currentPos.X - _lastMousePos.X;
        double dy = currentPos.Y - _lastMousePos.Y;

        if (_isOrbiting)
            ActiveCamera.Rotate(dx * 0.4, -dy * 0.4);   // 0.4 Grad/Pixel, Elevation geklemmt auf [-85°, +85°]

        if (_isPanning)
            ActiveCamera.Pan(dx * 0.01, dy * 0.01);     // Aufgabe 1.2

        _lastMousePos = currentPos;
    }

    private void OpenGlView_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) _isOrbiting = false;
        else if (e.ChangedButton == MouseButton.Right) _isPanning = false;

        if (!_isOrbiting && !_isPanning)
            OpenGlView.ReleaseMouseCapture();
    }

    private void OpenGlView_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double deltaZoom = e.Delta > 0 ? -0.5 : 0.5;
        ActiveCamera.Zoom(deltaZoom);
    }

    // =====================================================================================
    // Stufe 1: Slider & Buttons
    // =====================================================================================
    private void SliderJoint_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        if (SliderTheta1 == null || SliderTheta2 == null || SliderD3 == null) return;
        if (TxtTheta1 == null || TxtTheta2 == null || TxtD3 == null) return;

        _theta1Deg = (float)SliderTheta1.Value;
        _theta2Deg = (float)SliderTheta2.Value;
        _d3 = (float)SliderD3.Value;

        TxtTheta1.Text = $"{_theta1Deg:F0}°";
        TxtTheta2.Text = $"{_theta2Deg:F0}°";
        TxtD3.Text = $"{_d3:F2} m";

        UpdateTcpCalculations();
    }

    private void BtnResetView_Click(object sender, RoutedEventArgs e)
    {
        OrbitCamera preset = _stage == AppStage.Stage1Scara ? CreateScaraCamera() : CreateCraneCamera();
        OrbitCamera cam = ActiveCamera;
        cam.AzimuthDeg = preset.AzimuthDeg;
        cam.ElevationDeg = preset.ElevationDeg;
        cam.Distance = preset.Distance;
        cam.LookAtX = preset.LookAtX;
        cam.LookAtY = preset.LookAtY;
        cam.LookAtZ = preset.LookAtZ;
    }

    private void BtnResetJoints_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == AppStage.Stage2ClawCrane)
        {
            _crane.Reset();
            UpdateCraneUi();
            return;
        }

        SliderTheta1.Value = 0.0;
        SliderTheta2.Value = 0.0;
        SliderD3.Value = 0.0;
    }
}
