using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using KinematikEuler.Models;

namespace KinematikEuler.Solvers;

public static class HeunIntegrator
{
    /// <summary>
    /// Heun (Prädiktor-Korrektor, RK2):
    ///   x~     = x_k + dt * f(x_k)
    ///   x_k+1  = x_k + dt/2 * ( f(x_k) + f(x~) )
    /// </summary>
    public static KinematicState StepHeun(
        KinematicState current,
        double dt,
        Func<KinematicState, (double ax, double ay)> accelerationFunc)
    {
        // Steigung am Intervallanfang
        var (ax1, ay1) = accelerationFunc(current);
        double k1x = current.Vx, k1y = current.Vy;

        // Prädiktor (Euler-Hilfsschritt)
        var predicted = new KinematicState(
            current.Time + dt,
            current.X + dt * k1x,
            current.Y + dt * k1y,
            current.Vx + dt * ax1,
            current.Vy + dt * ay1);

        // Steigung am Intervallende
        var (ax2, ay2) = accelerationFunc(predicted);
        double k2x = predicted.Vx, k2y = predicted.Vy;

        // Korrektor (Mittelwert der Steigungen)
        return new KinematicState(
            current.Time + dt,
            current.X + 0.5 * dt * (k1x + k2x),
            current.Y + 0.5 * dt * (k1y + k2y),
            current.Vx + 0.5 * dt * (ax1 + ax2),
            current.Vy + 0.5 * dt * (ay1 + ay2));
    }
}

Hallo