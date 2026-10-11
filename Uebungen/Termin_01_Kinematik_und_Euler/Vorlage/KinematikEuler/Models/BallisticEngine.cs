using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KinematikEuler.Models;

/// <summary>
/// Physikmodell für Track B: Geschoss mit quadratischem Newton-Luftwiderstand,
/// Relativwind und Geländeprofil. Enthält KEINE Integration und KEINE Ausgabe.
/// </summary>
public sealed class BallisticEngine
{
    public double Gravity { get; init; } = 9.81;           // Gravitation [m/s^2]
    public double AirDensity { get; init; } = 1.225;       // Luftdichte [kg/m^3]
    public double Mass { get; init; } = 10.0;              // Granatenmasse [kg]
    public double Radius { get; init; } = 0.06;            // Kaliberradius [m]
    public double DragCoefficient { get; init; } = 0.25;   // Widerstandsbeiwert c_w [-]
    public double MuzzleVelocity { get; init; } = 160.0;   // Abgangsgeschwindigkeit [m/s]
    public double TargetX { get; init; } = 1400.0;         // Zielweite [m]

    public double CrossSection => Math.PI * Radius * Radius; // Querschnittsfläche A = pi * r^2 [m^2]
    public double TargetY => TerrainHeight(TargetX); // Zielhöhe [m]

    // k = rho * cw * A / (2m)  ->  a_drag = -k * |v_rel| * v_rel
    private double DragFactor => 0.5 * AirDensity * DragCoefficient * CrossSection / Mass;

    // Höhenprofil des Geländes: y = f(x) = 50*sin(0.002*x) + 20*cos(0.005*x)
    public double TerrainHeight(double x) =>
        50.0 * Math.Sin(0.002 * x) + 20.0 * Math.Cos(0.005 * x);

    /// <summary>Abschuss auf Geländehöhe bei x = 0.</summary>
    public KinematicState CreateLaunchState(double angleRad) =>
        new(Time: 0.0,
            X: 0.0,
            Y: TerrainHeight(0.0),
            Vx: MuzzleVelocity * Math.Cos(angleRad),
            Vy: MuzzleVelocity * Math.Sin(angleRad));

    /// <summary>
    /// Beschleunigung mit Relativwind w = (windX, 0). windX &gt; 0 = Rückenwind (in Schussrichtung).
    /// v_rel = (vx - wx, vy), der Betrag |v_rel| wird VEKTORIELL aus beiden Komponenten gebildet.
    /// </summary>
    public (double ax, double ay) Acceleration(KinematicState s, double windX)
    {
        double vrelX = s.Vx - windX;
        double vrelY = s.Vy;
        double vrel = Math.Sqrt(vrelX * vrelX + vrelY * vrelY);

        double ax = -DragFactor * vrel * vrelX;
        double ay = -Gravity - DragFactor * vrel * vrelY;
        return (ax, ay);
    }
}
