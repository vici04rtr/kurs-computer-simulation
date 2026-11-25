using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MultithreadingBenchmark.Simulation;

namespace MultithreadingBenchmark.Benchmarks
{
    /// <summary>
    /// Ergebnis einer Messstufe der Amdahl-Skalierungsreihe.
    /// SerialFraction = aus S(p) rekonstruierter serieller Anteil s (NaN bei p = 1).
    /// </summary>
    public record BenchmarkResult(
        int Threads,
        double ElapsedMilliseconds,
        double Speedup,
        double Efficiency,
        double SerialFraction = double.NaN)
    {
        /// <summary>Anzeige-Text für die DataGrid-Spalte "Serieller Anteil s".</summary>
        public string SerialFractionText => double.IsNaN(SerialFraction) ? "–" : SerialFraction.ToString("P1");
    }

    /// <summary>Fortschrittsmeldung der Skalierungsreihe (für IProgress&lt;T&gt;).</summary>
    public record ScalingProgress(int Done, int Total, string Message);

    public class ParallelRunner
    {
        // ====================================================================
        // Warmup
        // ====================================================================

        /// <summary>
        /// Führt einen Warmup-Durchlauf durch, damit JIT-Kompilierung und Thread-Pool-Allokation
        /// die eigentliche Benchmark-Messung nicht verfälschen.
        /// </summary>
        public static void Warmup(int particleCount = 10000, int steps = 2)
        {
            var warmupField = new ParticleField(particleCount, seed: 1);
            for (int s = 0; s < Math.Max(1, steps); s++)
            {
                warmupField.StepSequential(0.001);
                warmupField.StepParallel(0.001, Environment.ProcessorCount);
            }
        }

        /// <summary>Warmup für die Schwarm-Simulation (sequentieller und paralleler Pfad inkl. Grid-Aufbau).</summary>
        public static void WarmupSwarm(int agentCount = 5000, int steps = 3)
        {
            var warmupField = new SwarmField(agentCount, seed: 1);
            for (int s = 0; s < Math.Max(1, steps); s++)
            {
                warmupField.StepSequential(0.05f);
                warmupField.StepParallel(0.05f, Environment.ProcessorCount);
            }
        }

        // ====================================================================
        // Einzelmessungen
        // ====================================================================

        /// <summary>
        /// Misst die sequentielle Laufzeit (1 Thread).
        /// </summary>
        public static double MeasureSequential(ParticleField field, int steps, double dt, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            for (int s = 0; s < steps; s++)
            {
                field.StepSequential(dt, cancellationToken);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Misst die parallele Laufzeit mit gegebener Thread-Anzahl.
        /// </summary>
        public static double MeasureParallel(ParticleField field, int steps, double dt, int threads, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            for (int s = 0; s < steps; s++)
            {
                field.StepParallel(dt, threads, cancellationToken);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Sequentielle Laufzeit der Schwarm-Simulation über <paramref name="steps"/> Schritte.</summary>
        public static double MeasureSequential(SwarmField field, int steps, float dt, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            for (int s = 0; s < steps; s++)
            {
                field.StepSequential(dt, cancellationToken);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Parallele Laufzeit der Schwarm-Simulation über <paramref name="steps"/> Schritte.</summary>
        public static double MeasureParallel(SwarmField field, int steps, float dt, int threads, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            for (int s = 0; s < steps; s++)
            {
                field.StepParallel(dt, threads, cancellationToken);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Kalibriert die sequentielle Schrittzeit T_1 [ms/Schritt] auf einem KLON des Schwarms
        /// (der laufende Zustand bleibt unangetastet). Ein Vorlaufschritt + Minimum über <paramref name="samples"/> Schritte.
        /// </summary>
        public static double MeasureSequentialStepMs(SwarmField field, float dt, int samples = 3, CancellationToken cancellationToken = default)
        {
            var probe = field.Clone();
            probe.StepSequential(dt, cancellationToken); // JIT/Cache-Warmup, nicht gewertet

            double best = double.MaxValue;
            for (int i = 0; i < Math.Max(1, samples); i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                probe.StepSequential(dt, cancellationToken);
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                best = Math.Min(best, ms);
            }
            return Math.Max(0.01, best);
        }

        // ====================================================================
        // Skalierungsreihen
        // ====================================================================

        /// <summary>
        /// Führt eine vollständige Skalierungsreihe der Partikel-Simulation (Stufe 1) über verschiedene Thread-Zahlen durch.
        /// Jede Stufe: frisches Feld (gleicher Seed), JIT-Warmup vorab, Best-of-<paramref name="repetitions"/>.
        /// </summary>
        public static List<BenchmarkResult> RunScalingSeries(
            int particleCount,
            int steps,
            double dt,
            int[] threadCounts,
            int repetitions = 3,
            IProgress<ScalingProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Warmup();

            return RunSeriesCore(
                threadCounts,
                repetitions,
                progress,
                cancellationToken,
                runSequential: ct => MeasureSequential(new ParticleField(particleCount, seed: 42), steps, dt, ct),
                runParallel: (p, ct) => MeasureParallel(new ParticleField(particleCount, seed: 42), steps, dt, p, ct));
        }

        /// <summary>
        /// Track B (B.4): Skalierungsreihe der Schwarm-Simulation. Gleiche Methodik wie Stufe 1
        /// (frischer Schwarm mit Seed 42, JIT-Warmup, Best-of-N), damit die Tabellen vergleichbar sind.
        /// </summary>
        public static List<BenchmarkResult> RunSwarmScalingSeries(
            int agentCount,
            int steps,
            float dt,
            int[] threadCounts,
            int repetitions = 3,
            IProgress<ScalingProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            WarmupSwarm();

            return RunSeriesCore(
                threadCounts,
                repetitions,
                progress,
                cancellationToken,
                runSequential: ct => MeasureSequential(new SwarmField(agentCount, seed: 42), steps, dt, ct),
                runParallel: (p, ct) => MeasureParallel(new SwarmField(agentCount, seed: 42), steps, dt, p, ct));
        }

        private static List<BenchmarkResult> RunSeriesCore(
            int[] threadCounts,
            int repetitions,
            IProgress<ScalingProgress>? progress,
            CancellationToken ct,
            Func<CancellationToken, double> runSequential,
            Func<int, CancellationToken, double> runParallel)
        {
            repetitions = Math.Max(1, repetitions);
            var parallelCounts = threadCounts.Where(p => p > 1).Distinct().OrderBy(p => p).ToList();

            int total = (1 + parallelCounts.Count) * repetitions;
            int done = 0;

            double BestOf(Func<double> run, string label)
            {
                double best = double.MaxValue;
                for (int r = 0; r < repetitions; r++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ScalingProgress(done, total, $"Messe {label} (Lauf {r + 1}/{repetitions}) ..."));
                    best = Math.Min(best, run());
                    done++;
                }
                return best;
            }

            // 1. Sequentielle Baseline T_1 messen
            double t1 = BestOf(() => runSequential(ct), "p = 1 (sequentiell)");

            var results = new List<BenchmarkResult>
            {
                new BenchmarkResult(Threads: 1, ElapsedMilliseconds: t1, Speedup: 1.0, Efficiency: 1.0)
            };

            // 2. Parallele Durchläufe für alle gewünschten Thread-Zahlen
            foreach (int p in parallelCounts)
            {
                double tp = BestOf(() => runParallel(p, ct), $"p = {p}");

                double speedup = t1 / tp;
                double efficiency = speedup / p;
                double s = EstimateSerialFraction(speedup, p);

                results.Add(new BenchmarkResult(p, tp, speedup, efficiency, s));
            }

            progress?.Report(new ScalingProgress(total, total, $"Skalierungsreihe für {results.Count} Thread-Stufen abgeschlossen."));
            return results;
        }

        /// <summary>
        /// Thread-Stufen p ∈ {1, 2, 4, ..., P_max} (Zweierpotenzen) plus P_max selbst, falls keine Zweierpotenz.
        /// </summary>
        public static int[] BuildThreadCounts(int maxThreads)
        {
            maxThreads = Math.Max(1, maxThreads);
            var counts = new List<int>();
            for (int p = 1; p <= maxThreads; p *= 2)
            {
                counts.Add(p);
            }
            if (!counts.Contains(maxThreads))
            {
                counts.Add(maxThreads);
            }
            return counts.ToArray();
        }

        // ====================================================================
        // Amdahl
        // ====================================================================

        /// <summary>
        /// Berechnet den theoretischen Speedup nach Amdahls Gesetz:
        /// S(p) = 1 / (s + (1 - s) / p)
        /// </summary>
        /// <param name="p">Anzahl Prozessorkerne / Threads.</param>
        /// <param name="s">Serieller Codeanteil s in [0, 1].</param>
        public static double AmdahlSpeedup(int p, double s)
        {
            if (p <= 0) throw new ArgumentOutOfRangeException(nameof(p));
            if (s < 0.0 || s > 1.0) throw new ArgumentOutOfRangeException(nameof(s));

            return 1.0 / (s + (1.0 - s) / p);
        }

        /// <summary>Parallele Effizienz E(p) = S(p) / p.</summary>
        public static double AmdahlEfficiency(int p, double s) => AmdahlSpeedup(p, s) / p;

        /// <summary>Grenz-Speedup S_max = 1 / s (p → ∞); +∞ für s = 0.</summary>
        public static double AmdahlLimit(double s) => s <= 0.0 ? double.PositiveInfinity : 1.0 / s;

        /// <summary>
        /// Schätzt den seriellen Codeanteil s aus gemessenem Speedup S auf p Kernen:
        /// s = (1/S - 1/p) / (1 - 1/p)
        /// </summary>
        public static double EstimateSerialFraction(double measuredSpeedup, int p)
        {
            if (p <= 1) return 1.0;
            double s = (1.0 / measuredSpeedup - 1.0 / p) / (1.0 - 1.0 / p);
            return Math.Clamp(s, 0.0, 1.0);
        }

        /// <summary>Mittelwert der geschätzten seriellen Anteile über alle Stufen mit p &gt; 1 (NaN, falls keine vorhanden).</summary>
        public static double MeanSerialFraction(IEnumerable<BenchmarkResult> results)
        {
            var values = results.Where(r => r.Threads > 1 && !double.IsNaN(r.SerialFraction))
                                .Select(r => r.SerialFraction)
                                .ToList();
            return values.Count == 0 ? double.NaN : values.Average();
        }
    }
}
