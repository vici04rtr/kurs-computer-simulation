using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Globalization;
using System.Text;
using KinematikEuler.Models;

namespace KinematikEuler.Output;

public static class ShotOutput
{
    /// <summary>ASCII-Plot (Standard 80 x 25): '*' Bahn, '.' Gelände, 'X' Ziel.</summary>
    public static string RenderAscii(
        IReadOnlyList<KinematicState> trajectory,
        BallisticEngine engine,
        int width = 80,
        int height = 25)
    {
        double xMax = Math.Max(trajectory.Max(s => s.X), engine.TargetX) * 1.05;

        double yMin = trajectory.Min(s => s.Y);
        double yMax = trajectory.Max(s => s.Y);
        for (int c = 0; c < width; c++)
        {
            double h = engine.TerrainHeight(c * xMax / (width - 1));
            yMin = Math.Min(yMin, h);
            yMax = Math.Max(yMax, h);
        }
        if (yMax - yMin < 1e-9) yMax = yMin + 1.0;

        int ToCol(double x) => Math.Clamp((int)Math.Round(x / xMax * (width - 1)), 0, width - 1);
        int ToRow(double y) => Math.Clamp((int)Math.Round((yMax - y) / (yMax - yMin) * (height - 1)), 0, height - 1);

        var grid = new char[height, width];
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
                grid[r, c] = ' ';

        // Gelände
        for (int c = 0; c < width; c++)
            grid[ToRow(engine.TerrainHeight(c * xMax / (width - 1))), c] = '.';

        // Flugbahn (Segmente unterteilen, damit keine Lücken entstehen)
        for (int i = 1; i < trajectory.Count; i++)
        {
            var a = trajectory[i - 1];
            var b = trajectory[i];
            int n = Math.Max(1, Math.Max(Math.Abs(ToCol(b.X) - ToCol(a.X)), Math.Abs(ToRow(b.Y) - ToRow(a.Y))));
            for (int j = 0; j <= n; j++)
            {
                double u = (double)j / n;
                grid[ToRow(a.Y + u * (b.Y - a.Y)), ToCol(a.X + u * (b.X - a.X))] = '*';
            }
        }

        // Ziel
        grid[ToRow(engine.TargetY), ToCol(engine.TargetX)] = 'X';

        var sb = new StringBuilder();
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++) sb.Append(grid[r, c]);
            sb.AppendLine();
        }
        sb.Append($"x: 0..{xMax:F0} m | y: {yMin:F0}..{yMax:F0} m | * Bahn  . Gelaende  X Ziel");
        return sb.ToString();
    }

    public static void WriteCsv(string path, IReadOnlyList<KinematicState> trajectory)
    {
        var inv = CultureInfo.InvariantCulture;
        using var writer = new StreamWriter(path);
        writer.WriteLine("t,x,y,vx,vy");
        foreach (var s in trajectory)
        {
            writer.WriteLine(string.Join(",",
                new[] { s.Time, s.X, s.Y, s.Vx, s.Vy }.Select(v => v.ToString("F4", inv))));
        }
    }
}
