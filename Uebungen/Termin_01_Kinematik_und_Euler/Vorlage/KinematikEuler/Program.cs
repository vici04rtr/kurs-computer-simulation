using KinematikEuler.Models;
using KinematikEuler.Solvers;
using KinematikEuler.Output;

namespace KinematikEuler;

internal class Program
{
    private const double G = 9.81; // Erdbeschleunigung [m/s^2]

    public static void Main(string[] args)
    {
        Console.WriteLine("===============================================================");
        Console.WriteLine("  Termin 01: Kinematik, Zustandsraum & Expliziter Euler-Integrator");
        Console.WriteLine("  Kurs: Systemsimulation / Digitaler Zwilling (FH OÖ Campus Wels)");
        Console.WriteLine("===============================================================\n");

        Console.WriteLine("Wählen Sie einen Modus:");
        Console.WriteLine("  1: Stufe A (In-Class Sprint: Schiefer Wurf im Vakuum)");
        Console.WriteLine("  2: Stufe B (Track A: Hydraulikzylinder-Endlagendämpfung)");
        Console.WriteLine("  3: Stufe B (Track B: Artillery Duel mit Wind & Drag)");
        Console.Write("Auswahl [Standard: 1]: ");

        string? input = Console.ReadLine();
        char choice = string.IsNullOrWhiteSpace(input) ? '1' : input.Trim()[0];

        switch (choice)
        {
            case '2':
                RunTrackA_HydraulicCylinder();
                break;
            case '3':
                RunTrackB_ArtilleryGame();
                break;
            default:
                RunSprint_BallisticTrajectory();
                break;
        }
    }

    /// <summary>
    /// Stufe A: In-Class Sprint - Schiefer Wurf im Vakuum.
    /// Vergleich des expliziten Euler-Verfahrens mit der geschlossenen analytischen Lösung.
    /// </summary>
    public static void RunSprint_BallisticTrajectory()
    {
        Console.WriteLine("\n--- [Stufe A Sprint] Schiefer Wurf im Vakuum ---");

        // Physikalische Randbedingungen:
        double v0 = 150.0;             // Abgangsgeschwindigkeit [m/s]
        double alphaDeg = 45.0;        // Abschusswinkel [Grad]
        double alphaRad = alphaDeg * Math.PI / 180.0;

        double vx0 = v0 * Math.Cos(alphaRad);
        double vy0 = v0 * Math.Sin(alphaRad);

        // Analytische Vakuumlösung:
        double tAna = 2.0 * vy0 / G;
        double xAna = (v0 * v0 * Math.Sin(2.0 * alphaRad)) / G;
        double yMaxAna = (vy0 * vy0) / (2.0 * G);

        Console.WriteLine($"Parameter: v0 = {v0:F1} m/s, alpha = {alphaDeg:F1}°");
        Console.WriteLine($"Analytisch: t_Flug = {tAna:F3} s | x_Impact = {xAna:F2} m | y_Max = {yMaxAna:F2} m\n");

        double[] stepSizes = [0.1, 0.01];

        foreach (double dt in stepSizes)
        {
            var state = new KinematicState(Time: 0.0, X: 0.0, Y: 0.0, Vx: vx0, Vy: vy0);
            var previous = state;
            double yMaxNum = 0.0;

            while (state.Y >= 0.0)
            {
                if (state.Y > yMaxNum)
                    yMaxNum = state.Y;

                previous = state;
                state = EulerIntegrator.StepEuler(state, dt, s => (0.0, -G));
            }

            // Exakte Schnittpunkt-Interpolation bei Bodenberührung
            var impactState = EulerIntegrator.InterpolateGroundImpact(previous, state);
            double diffX = impactState.X - xAna;
            double diffT = impactState.Time - tAna;

            Console.WriteLine($"Euler (dt = {dt:F2}s):");
            Console.WriteLine($"  t_Impact = {impactState.Time:F3} s (Fehler: {diffT:+0.0000;-0.0000} s)");
            Console.WriteLine($"  x_Impact = {impactState.X:F2} m (Fehler: {diffX:+0.000;-0.000} m)");
            Console.WriteLine($"  y_Max    = {yMaxNum:F2} m (Fehler: {yMaxNum - yMaxAna:+0.000;-0.000} m)\n");
        }

        Console.WriteLine("Hinweis: In X ist der Fehler numerisch exakt 0, da ax = 0 konstant ist!");
    }

    /// <summary>
    /// Stufe B: Track A - Hydraulikzylinder-Endlagendämpfung (Gerüst).
    /// </summary>
    private static void RunTrackA_HydraulicCylinder()
    {
        Console.WriteLine("\n--- [Stufe B Track A] Hydraulikzylinder-Endlagendämpfung ---");
        Console.WriteLine("// TODO: [Stufe B Track A] Implementieren Sie:");
        Console.WriteLine("   1. HydraulicCylinderModel mit Kraft F_vor = 60 kN und Blendenreibung.");
        Console.WriteLine("   2. Vergleich Euler vs. Heun (RK2) bei dt in {10µs, 100µs, 500µs, 2ms}.");
        Console.WriteLine("   3. CSV-Export nach simulation_cylinder.csv.");
    }

    /// <summary>Stufe B: Track B - Retro Artillery Duel mit Newton-Drag & Wind.</summary>
    private static void RunTrackB_ArtilleryGame()
    {
        Console.WriteLine("\n--- [Stufe B Track B] Retro Artillery Duel ---");

        var engine = new BallisticEngine();
        Console.WriteLine($"Ziel: x = {engine.TargetX:F0} m, y_terrain = {engine.TargetY:F2} m, v0 = {engine.MuzzleVelocity:F0} m/s\n");

        PrintSolverComparison(engine);
        PrintWindSignTest(engine);
        PlayRandomRounds(engine, rounds: 5);
    }

    private static void PrintSolverComparison(BallisticEngine engine)
    {
        double angleRad = 30.0 * Math.PI / 180.0;
        var reference = ShotSimulator.Simulate(engine, angleRad, 0.0, SolverKind.Heun, 0.001,
                                               recordTrajectory: false).Impact;

        Console.WriteLine("=== EULER vs. HEUN (alpha = 30 Grad, kein Wind, Referenz: Heun dt = 0.001 s) ===");
        Console.WriteLine($"{"Solver",-8}{"dt [s]",8}{"t_impact [s]",15}{"x_impact [m]",15}{"Fehler x [m]",15}");

        foreach (var solver in new[] { SolverKind.Euler, SolverKind.Heun })
        {
            foreach (double dt in new[] { 0.1, 0.01 })
            {
                var r = ShotSimulator.Simulate(engine, angleRad, 0.0, solver, dt, recordTrajectory: false).Impact;
                Console.WriteLine($"{solver,-8}{dt,8:F3}{r.Time,15:F3}{r.X,15:F2}{r.X - reference.X,15:+0.00;-0.00}");
            }
        }
        Console.WriteLine();
    }

    private static void PrintWindSignTest(BallisticEngine engine)
    {
        Console.WriteLine("=== WINDVORZEICHEN-TEST (flache Bahn, Heun dt = 0.01 s) ===");

        foreach (double wx in new[] { -10.0, 0.0, 10.0 })
        {
            string label = wx < 0 ? "Gegenwind" : wx > 0 ? "Rueckenwind" : "kein Wind";
            var angles = AngleFinder.FindAnglesDeg(engine, wx);

            if (angles.Count == 0)
            {
                Console.WriteLine($"wx = {wx,6:F1} m/s ({label}): kein Treffwinkel gefunden");
                continue;
            }

            double a = angles[0];
            var r = ShotSimulator.Simulate(engine, a * Math.PI / 180.0, wx, SolverKind.Heun, 0.01,
                                           recordTrajectory: false).Impact;
            Console.WriteLine($"wx = {wx,6:F1} m/s ({label,-11}): alpha = {a,7:F3} Grad | t = {r.Time:F3} s | x = {r.X:F2} m");
        }
        Console.WriteLine("Erwartung: Gegenwind -> groesserer Winkel, Rueckenwind -> kleinerer Winkel als ohne Wind.\n");
    }

    private static void PlayRandomRounds(BallisticEngine engine, int rounds)
    {
        var rng = new Random(42); // feste Seed = reproduzierbar; fuer echten Zufall: new Random()

        for (int i = 1; i <= rounds; i++)
        {
            double wx = rng.NextDouble() * 30.0 - 15.0; // U(-15, +15) m/s
            Console.WriteLine($"--- Schuss {i}: Wind wx = {wx:+0.00;-0.00} m/s ---");

            var angles = AngleFinder.FindAnglesDeg(engine, wx);
            if (angles.Count == 0)
            {
                Console.WriteLine("Kein Winkel im Bereich [10, 80] Grad trifft das Ziel.\n");
                continue;
            }

            double alphaDeg = angles[0]; // flache Bahn
            var shot = ShotSimulator.Simulate(engine, alphaDeg * Math.PI / 180.0, wx, SolverKind.Heun, 0.01);

            Console.WriteLine($"Zielwinkel = {alphaDeg:F3} Grad | Flugdauer = {shot.Impact.Time:F3} s | " +
                              $"x_impact = {shot.Impact.X:F2} m (Ziel {engine.TargetX:F0} m)");
            Console.WriteLine(ShotOutput.RenderAscii(shot.Trajectory, engine));

            string path = $"shot_{i}.csv";
            ShotOutput.WriteCsv(path, shot.Trajectory);
            Console.WriteLine($"CSV: {Path.GetFullPath(path)}\n");
        }
    }
}
