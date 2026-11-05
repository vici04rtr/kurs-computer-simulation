namespace ScottPlotTelemetry.Data;

/// <summary>
/// Ein Streckenabschnitt des Rundkurses (entspricht genau einem Knoten im MSAGL-Streckengraphen).
/// </summary>
/// <param name="Name">Knotenname im Graphen (z. B. "Hairpin").</param>
/// <param name="LengthM">Länge des Abschnitts in Metern.</param>
/// <param name="Curvature">Krümmung κ = 1/R in 1/m (0 = Gerade).</param>
/// <param name="TargetKmh">Zielgeschwindigkeit des Fahrers in diesem Abschnitt.</param>
/// <param name="Alternating">true = Krümmungsvorzeichen wechselt in der Mitte (Schikane).</param>
public readonly record struct TrackSegment(
    string Name, double LengthM, double Curvature, double TargetKmh, bool Alternating = false);

/// <summary>
/// Sehr einfache Fahrdynamik-Simulation (Punktmasse, Reibkreis, 6-Gang-Getriebe) für das Retro-Racing-HUD.
/// Der gesamte Zustand liegt in Feldern; <see cref="Step"/> erzeugt keinerlei Heap-Allokationen.
/// Physik:
///   a_y = v² · κ                      (Querbeschleunigung aus Kurvenkrümmung)
///   a_x = Fahrer-Gas/Bremse           (begrenzt durch Reibkreis  sqrt(a_x² + a_y²) ≤ μ·g)
///   RPM = v / v_Gang,max · RPM_max    (starre Kopplung über Gangübersetzung)
/// </summary>
public sealed class TrackSimulator
{
    public const double G = 9.81;
    /// <summary>Reibwert Reifen/Asphalt (Radius des Kamm'schen Kreises in g).</summary>
    public const double Mu = 1.3;
    public const double RpmLimiter = 7500.0;

    // ---- Streckenlayout (Reihenfolge = Fahrtrichtung) ----------------------------------------
    public const int PitIndex = 5;

    public static readonly TrackSegment[] Segments =
    [
        new("Sektor 1",   600.0, 0.000, 220.0),
        new("Hairpin",    120.0, 0.040,  58.0),
        new("Sektor 2",   450.0, 0.000, 200.0),
        new("Chicane",    150.0, 0.012, 100.0, Alternating: true),
        new("Sektor 3",   350.0, 0.003, 190.0),
        new("Boxengasse", 200.0, 0.000,  80.0),
    ];

    // ---- Antriebsstrang -----------------------------------------------------------------------
    private const double RpmAtGearTop = 7600.0;
    private static readonly double[] GearTopKmh = [55.0, 95.0, 135.0, 175.0, 215.0, 260.0];
    private const double AccelMax = 9.5;     // m/s² bei v = 0
    private const double VMax = 75.0;        // m/s (~270 km/h) Luftwiderstandsgrenze
    private const double BrakeNominal = 11.0; // m/s² (~1,12 g)

    private readonly Random _rng;

    // ---- Interner Zustand ------------------------------------------------------------------------
    private double _v;               // m/s
    private double _segDist;         // zurückgelegt im aktuellen Abschnitt [m]
    private double _aLong;           // geglättete Längsbeschleunigung [m/s²]
    private double _driverFactor = 1.0;
    private double _brakeFactor = 0.9;
    private double _shiftPoint = 6950.0;
    private int _slideTicks;
    private double _slideSign = 1.0;

    // ---- Öffentliche Ausgangsgrößen (Lese-Zugriff aus der UI) ------------------------------------
    public double SpeedKmh { get; private set; }
    public double Rpm { get; private set; }
    /// <summary>Gang 1…6.</summary>
    public int Gear { get; private set; }
    /// <summary>Längsbeschleunigung in g (positiv = Beschleunigen, negativ = Bremsen).</summary>
    public double AxG { get; private set; }
    /// <summary>Querbeschleunigung in g (vorzeichenbehaftet).</summary>
    public double AyG { get; private set; }
    public double ResultantG => Math.Sqrt(AxG * AxG + AyG * AyG);

    public int SegmentIndex { get; private set; }
    public int Lap { get; private set; } = 1;
    public double LapTime { get; private set; }
    public double LastLapTime { get; private set; }
    public bool PitThisLap => Lap % 3 == 0;

    // ---- Ereignisse (gelten nur für den zuletzt ausgeführten Tick) -----------------------------
    public bool Upshifted { get; private set; }
    public double UpshiftRpm { get; private set; }
    public bool SegmentChanged { get; private set; }
    public bool LapCompleted { get; private set; }

    public TrackSimulator(int seed = 42)
    {
        _rng = new Random(seed);
        Reset();
    }

    public void Reset()
    {
        _v = 60.0 / 3.6;
        _segDist = 0.0;
        _aLong = 0.0;
        _slideTicks = 0;
        SegmentIndex = 0;
        Lap = 1;
        LapTime = 0.0;
        LastLapTime = 0.0;
        NewLapDriverMood();
        _shiftPoint = 6950.0;

        // Passenden Startgang wählen
        Gear = 1;
        double kmh = _v * 3.6;
        while (Gear < 6 && kmh / GearTopKmh[Gear - 1] * RpmAtGearTop > _shiftPoint)
            Gear++;

        SpeedKmh = kmh;
        Rpm = kmh / GearTopKmh[Gear - 1] * RpmAtGearTop;
        AxG = AyG = 0.0;
        Upshifted = SegmentChanged = LapCompleted = false;
    }

    /// <summary>Simuliert einen Fahrfehler (kurzer Traktionsabriss) für ca. 0,3 s.</summary>
    public void InjectSlide()
    {
        _slideTicks = 15;
        _slideSign = _rng.Next(2) == 0 ? -1.0 : 1.0;
    }

    public void Step(double dt)
    {
        Upshifted = false;
        SegmentChanged = false;
        LapCompleted = false;

        TrackSegment seg = Segments[SegmentIndex];
        TrackSegment next = Segments[NextIndex()];

        double vTarget = TargetMs(seg);
        double vNext = TargetMs(next);

        // ---- Querbeschleunigung aus Krümmung --------------------------------------------------------
        double kappa = seg.Curvature;
        if (seg.Alternating && _segDist > seg.LengthM * 0.5)
            kappa = -kappa;
        double ay = _v * _v * kappa;

        // ---- Längsdynamik: Gas / Bremse mit Bremspunkt --------------------------------------------
        double remaining = seg.LengthM - _segDist;
        double brakeDecel = BrakeNominal * _brakeFactor;
        // Bremspunkt wird konservativ mit Nominalverzögerung geplant
        double brakeDist = _v > vNext ? (_v * _v - vNext * vNext) / (2.0 * BrakeNominal * 0.9) : 0.0;

        double accelCap = AccelMax * (1.0 - (_v / VMax) * (_v / VMax));
        // Reibkreis: verbleibende Längsbeschleunigung bei gegebener Querbeschleunigung
        double ayG = Math.Abs(ay) / G;
        double frictionLeft = Math.Sqrt(Math.Max(0.0, Mu * Mu - ayG * ayG)) * G * 0.9;
        accelCap = Math.Min(accelCap, frictionLeft);

        double aTarget;
        if (_v > vNext && remaining <= brakeDist)
            aTarget = -brakeDecel;
        else
            aTarget = Math.Clamp((vTarget - _v) * 3.0, -brakeDecel, Math.Max(0.0, accelCap));

        // Pedal-Dynamik (PT1-Glied): erzeugt realistische Ein-/Ausblend-Rampen beim Bremsen
        _aLong += (aTarget - _aLong) * 0.25;

        double aNoise = (_rng.NextDouble() - 0.5) * 0.06 * G;
        double yNoise = (_rng.NextDouble() - 0.5) * 0.04 * G;

        double axOut = _aLong + aNoise;
        double ayOut = ay + yNoise;

        // ---- Traktionsabriss (manuell injiziert) -----------------------------------------------------
        if (_slideTicks > 0)
        {
            _slideTicks--;
            ayOut += _slideSign * 0.55 * G;
            axOut -= 0.25 * G;
        }

        // ---- Integration ---------------------------------------------------------------------------------
        _v = Math.Max(5.0, _v + _aLong * dt);
        _segDist += _v * dt;
        LapTime += dt;

        AxG = axOut / G;
        AyG = ayOut / G;
        SpeedKmh = _v * 3.6;

        UpdateGearbox();

        // ---- Abschnittswechsel / Rundenwechsel -----------------------------------------------------
        if (_segDist >= seg.LengthM)
        {
            _segDist -= seg.LengthM;
            AdvanceSegment();
        }
    }

    // =============================================================================================
    private double TargetMs(in TrackSegment s)
    {
        double factor = s.Curvature != 0.0 ? _driverFactor : 1.0;
        return s.TargetKmh / 3.6 * factor;
    }

    private int NextIndex()
    {
        int i = SegmentIndex;
        return i switch
        {
            4 => PitThisLap ? PitIndex : 0,   // nach Sektor 3: entweder Boxengasse oder Start/Ziel
            PitIndex => 0,
            _ => i + 1
        };
    }

    private void AdvanceSegment()
    {
        int nextIdx = NextIndex();
        SegmentChanged = true;

        if (nextIdx == 0)
        {
            LapCompleted = true;
            LastLapTime = LapTime;
            LapTime = 0.0;
            Lap++;
            NewLapDriverMood();
        }

        SegmentIndex = nextIdx;
    }

    /// <summary>Pro Runde variiert die "Tagesform" des Fahrers: Kurventempo und Bremsdruck.</summary>
    private void NewLapDriverMood()
    {
        _driverFactor = 0.95 + _rng.NextDouble() * 0.13;   // 0,95 … 1,08 → Grip / Untersteuern / Abriss
        _brakeFactor = 0.78 + _rng.NextDouble() * 0.24;    // 0,78 … 1,02
    }

    private void UpdateGearbox()
    {
        double kmh = SpeedKmh;
        double rpm = kmh / GearTopKmh[Gear - 1] * RpmAtGearTop;

        // Hochschalten bei fahrerspezifischem Schaltpunkt (streut → Welford hat etwas zu tun)
        if (Gear < 6 && rpm >= _shiftPoint)
        {
            Upshifted = true;
            UpshiftRpm = rpm;
            Gear++;
            _shiftPoint = 6950.0 + (_rng.NextDouble() - 0.5) * 900.0;   // 6500 … 7400
            rpm = kmh / GearTopKmh[Gear - 1] * RpmAtGearTop;
        }

        // Runterschalten beim Bremsen
        while (Gear > 1 && rpm < 3800.0)
        {
            Gear--;
            rpm = kmh / GearTopKmh[Gear - 1] * RpmAtGearTop;
        }

        rpm += (_rng.NextDouble() - 0.5) * 30.0;
        Rpm = Math.Min(rpm, RpmLimiter);
    }
}