using Microsoft.VisualStudio.TestTools.UnitTesting;
using KinematikEuler.Models;
using KinematikEuler.Solvers;

namespace KinematikTests;

[TestClass]
public class KinematikEulerTests
{
    private const double G = 9.81;

    [TestMethod]
    public void Test_ExplicitEuler_FreeFall_AnalyticComparison()
    {
        // Freier Fall aus y0 = 100 m ohne Anfangsgeschwindigkeit (v0 = 0)
        // Analytisch: y(t) = y0 - 0.5 * g * t^2
        // v(t) = -g * t
        double y0 = 100.0;
        double dt = 0.001; // Feine Diskretisierung
        double totalTime = 2.0;

        var state = new KinematicState(Time: 0.0, X: 0.0, Y: y0, Vx: 0.0, Vy: 0.0);

        int steps = (int)(totalTime / dt);
        for (int i = 0; i < steps; i++)
        {
            state = EulerIntegrator.StepEuler(state, dt, _ => (0.0, -G));
        }

        double yAnalytic = y0 - 0.5 * G * totalTime * totalTime;
        double vAnalytic = -G * totalTime;

        // Toleranzprüfung: Beim expliziten Euler ist der Geschwindigkeitsfehler 0 bei konstanter Beschleunigung
        Assert.AreEqual(vAnalytic, state.Vy, 1e-4, "Geschwindigkeit muss exakt übereinstimmen bei konstanter Beschleunigung.");
        // Lokaler Ortsfehler ist O(dt):
        Assert.AreEqual(yAnalytic, state.Y, 0.05, "Ortskoordinate muss nahe der analytischen Lösung liegen.");
    }

    [TestMethod]
    public void Test_GroundImpact_Interpolation_ProducesExactZeroY()
    {
        var prev = new KinematicState(Time: 1.0, X: 10.0, Y: 2.0, Vx: 10.0, Vy: -10.0);
        var curr = new KinematicState(Time: 1.1, X: 11.0, Y: -1.0, Vx: 10.0, Vy: -10.0);

        var impact = EulerIntegrator.InterpolateGroundImpact(prev, curr);

        // y muss exakt 0 sein
        Assert.AreEqual(0.0, impact.Y, 1e-9, "Bodenkontakt Y-Koordinate muss 0 sein.");
        // Zeit muss zwischen prev.Time und curr.Time liegen:
        // tau = (0 - 2) / (-1 - 2) = 2/3 ≈ 0.6667
        // t* = 1.0 + 2/3 * 0.1 ≈ 1.06667
        double expectedTime = 1.0 + (2.0 / 3.0) * 0.1;
        Assert.AreEqual(expectedTime, impact.Time, 1e-4, "Auftreffzeitpunkt muss korrekt interpoliert sein.");
        // x* = 10.0 + 2/3 * 1.0 ≈ 10.6667
        double expectedX = 10.0 + (2.0 / 3.0) * 1.0;
        Assert.AreEqual(expectedX, impact.X, 1e-4, "Auftreffort X muss korrekt interpoliert sein.");
    }

    [TestMethod]
    public void Test_Euler_Convergence_ErrorDecreasesWithHalfStep()
    {
        double y0 = 100.0;
        double totalTime = 1.0;
        double yAnalytic = y0 - 0.5 * G * totalTime * totalTime;

        double RunSim(double dt)
        {
            var s = new KinematicState(Time: 0.0, X: 0.0, Y: y0, Vx: 0.0, Vy: 0.0);
            int n = (int)Math.Round(totalTime / dt);
            for (int i = 0; i < n; i++)
            {
                s = EulerIntegrator.StepEuler(s, dt, _ => (0.0, -G));
            }
            return Math.Abs(s.Y - yAnalytic);
        }

        double errorLarge = RunSim(0.1);
        double errorSmall = RunSim(0.05);

        // Bei Verfahren 1. Ordnung halbiert sich der globale Fehler näherungsweise bei Halbierung von dt
        Assert.IsTrue(errorSmall < errorLarge * 0.65, 
            $"Fehler bei dt=0.05 ({errorSmall}) muss deutlich kleiner sein als bei dt=0.1 ({errorLarge}).");
    }

    // Ab hier Tests selbst eingefügt
    private static double DragK(BallisticEngine e) =>
    0.5 * e.AirDensity * e.DragCoefficient * e.CrossSection / e.Mass;

    [TestMethod]
    public void Test_Drag_IsComputedFromVectorMagnitude()
    {
        var e = new BallisticEngine();
        var s = new KinematicState(0, 0, 0, Vx: 100.0, Vy: 60.0);

        var (ax, ay) = e.Acceleration(s, 0.0);
        double dragAy = ay + e.Gravity;

        // Widerstand wirkt antiparallel zu v -> Verhältnis der Komponenten = vx/vy
        Assert.AreEqual(100.0 / 60.0, ax / dragAy, 1e-9);
        // Betrag über |v| = sqrt(vx^2+vy^2), NICHT vx^2 bzw. vy^2 getrennt
        Assert.AreEqual(-DragK(e) * s.Speed * 100.0, ax, 1e-9);
    }

    [TestMethod]
    public void Test_Wind_Sign_HeadwindBrakesMore_TailwindBrakesLess()
    {
        var e = new BallisticEngine();
        var s = new KinematicState(0, 0, 0, Vx: 100.0, Vy: 0.0);

        double axNone = e.Acceleration(s, 0.0).ax;
        double axHead = e.Acceleration(s, -10.0).ax;
        double axTail = e.Acceleration(s, +10.0).ax;

        Assert.IsTrue(axHead < axNone, "Gegenwind muss staerker bremsen.");
        Assert.IsTrue(axTail > axNone, "Rueckenwind muss weniger bremsen.");
        Assert.AreEqual(0.0, e.Acceleration(s, 100.0).ax, 1e-12, "Keine Relativbewegung -> kein Widerstand.");
    }

    [TestMethod]
    public void Test_TerrainInterpolation_FlatTerrain_MatchesGroundInterpolation()
    {
        var prev = new KinematicState(1.0, 10.0, 2.0, 10.0, -10.0);
        var curr = new KinematicState(1.1, 11.0, -1.0, 10.0, -10.0);

        var impact = ShotSimulator.InterpolateTerrainImpact(prev, curr, _ => 0.0);

        Assert.AreEqual(1.0 + (2.0 / 3.0) * 0.1, impact.Time, 1e-9);
        Assert.AreEqual(10.0 + 2.0 / 3.0, impact.X, 1e-9);
    }

    [TestMethod]
    public void Test_AngleFinder_HitsTarget_UnderHeadAndTailwind()
    {
        var e = new BallisticEngine();
        double angleHead = 0, angleTail = 0;

        foreach (double wx in new[] { -12.0, 0.0, 12.0 })
        {
            var angles = AngleFinder.FindAnglesDeg(e, wx);
            Assert.IsTrue(angles.Count > 0, $"Kein Winkel bei wx = {wx}");

            var impact = ShotSimulator.Simulate(e, angles[0] * Math.PI / 180.0, wx,
                SolverKind.Heun, 0.01, recordTrajectory: false).Impact;
            Assert.AreEqual(e.TargetX, impact.X, 1.5, $"Ziel verfehlt bei wx = {wx}");

            if (wx < 0) angleHead = angles[0];
            if (wx > 0) angleTail = angles[0];
        }

        Assert.IsTrue(angleHead > angleTail, "Gegenwind braucht einen groesseren Winkel als Rueckenwind.");
    }

    [TestMethod]
    public void Test_Heun_IsMoreAccurateThanEuler_AtSameStepSize()
    {
        var e = new BallisticEngine();
        double a = 30.0 * Math.PI / 180.0;

        double reference = ShotSimulator.Simulate(e, a, 0.0, SolverKind.Heun, 0.001, recordTrajectory: false).Impact.X;
        double errEuler = Math.Abs(ShotSimulator.Simulate(e, a, 0.0, SolverKind.Euler, 0.1, recordTrajectory: false).Impact.X - reference);
        double errHeun = Math.Abs(ShotSimulator.Simulate(e, a, 0.0, SolverKind.Heun, 0.1, recordTrajectory: false).Impact.X - reference);

        Assert.IsTrue(errHeun < errEuler / 10.0, $"Heun {errHeun} sollte viel genauer sein als Euler {errEuler}.");
    }
}
