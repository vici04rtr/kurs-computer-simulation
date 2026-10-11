using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using KinematikEuler.Models;

namespace KinematikEuler.Solvers;

public enum SolverKind { Euler, Heun }

public sealed record ShotResult(
    KinematicState Impact,
    IReadOnlyList<KinematicState> Trajectory,
    bool Completed);

public static class ShotSimulator
{
    public static ShotResult Simulate(
        BallisticEngine engine,
        double angleRad,
        double windX,
        SolverKind solver,
        double dt,
        bool recordTrajectory = true,
        double maxTime = 120.0)
    {
        Func<KinematicState, (double ax, double ay)> acc = s => engine.Acceleration(s, windX);

        var state = engine.CreateLaunchState(angleRad);
        var trajectory = new List<KinematicState>();
        if (recordTrajectory) trajectory.Add(state);

        while (state.Time < maxTime)
        {
            var previous = state;
            state = solver == SolverKind.Euler
                ? EulerIntegrator.StepEuler(previous, dt, acc)
                : HeunIntegrator.StepHeun(previous, dt, acc);

            // Kollision: y_{k+1} <= y_terrain(x_{k+1})
            if (state.Y <= engine.TerrainHeight(state.X))
            {
                var impact = InterpolateTerrainImpact(previous, state, engine.TerrainHeight);
                if (recordTrajectory) trajectory.Add(impact);
                return new ShotResult(impact, trajectory, true);
            }

            if (recordTrajectory) trajectory.Add(state);
        }

        return new ShotResult(state, trajectory, false);
    }

    /// <summary>
    /// Lineare Schnittpunkt-Interpolation gegen ein Geländeprofil.
    /// g = y - terrain(x);  tau = g_k / (g_k - g_{k+1})
    /// </summary>
    public static KinematicState InterpolateTerrainImpact(
        KinematicState previous,
        KinematicState current,
        Func<double, double> terrain)
    {
        double gPrev = previous.Y - terrain(previous.X);
        double gCurr = current.Y - terrain(current.X);
        double denom = gPrev - gCurr;

        if (Math.Abs(denom) < 1e-12)
            return current;

        double tau = Math.Clamp(gPrev / denom, 0.0, 1.0);

        return new KinematicState(
            previous.Time + tau * (current.Time - previous.Time),
            previous.X + tau * (current.X - previous.X),
            previous.Y + tau * (current.Y - previous.Y),
            previous.Vx + tau * (current.Vx - previous.Vx),
            previous.Vy + tau * (current.Vy - previous.Vy));
    }
}
