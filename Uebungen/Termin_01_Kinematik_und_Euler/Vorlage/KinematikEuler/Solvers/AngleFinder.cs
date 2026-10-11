using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using KinematikEuler.Models;

namespace KinematikEuler.Solvers;

public static class AngleFinder
{
    /// <summary>
    /// Liefert alle Winkel [Grad], bei denen der Einschlag das Ziel trifft.
    /// Typisch zwei Lösungen: flache Bahn (Index 0) und steile Bahn (letzter Eintrag).
    /// </summary>
    public static IReadOnlyList<double> FindAnglesDeg(
        BallisticEngine engine,
        double windX,
        SolverKind solver = SolverKind.Heun,
        double dt = 0.01,
        double minDeg = 10.0,
        double maxDeg = 80.0,
        double sweepStepDeg = 0.1,
        double toleranceM = 0.01)
    {
        double Error(double deg) =>
            ShotSimulator.Simulate(engine, deg * Math.PI / 180.0, windX, solver, dt,
                                   recordTrajectory: false).Impact.X - engine.TargetX;

        var solutions = new List<double>();
        int steps = (int)Math.Round((maxDeg - minDeg) / sweepStepDeg);

        double prevDeg = minDeg;
        double prevErr = Error(prevDeg);

        for (int i = 1; i <= steps; i++)
        {
            double deg = minDeg + i * sweepStepDeg;
            double err = Error(deg);

            if (prevErr * err <= 0.0)
                solutions.Add(Bisect(Error, prevDeg, deg, prevErr, toleranceM));

            prevDeg = deg;
            prevErr = err;
        }

        return solutions;
    }

    private static double Bisect(Func<double, double> error, double lo, double hi, double errLo, double tolerance)
    {
        double mid = 0.5 * (lo + hi);
        for (int i = 0; i < 60; i++)
        {
            mid = 0.5 * (lo + hi);
            double errMid = error(mid);
            if (Math.Abs(errMid) < tolerance) break;

            if (errLo * errMid <= 0.0) hi = mid;
            else { lo = mid; errLo = errMid; }
        }
        return mid;
    }
}
