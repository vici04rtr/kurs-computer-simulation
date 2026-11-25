using Microsoft.VisualStudio.TestTools.UnitTesting;
using MultithreadingBenchmark.Benchmarks;
using MultithreadingBenchmark.Simulation;
using System;
using System.Threading;

namespace MultithreadingBenchmark.Tests
{
    [TestClass]
    public class ThreadingTests
    {
        [TestMethod]
        public void TestSequentialVsParallelEquivalence()
        {
            // Verifiziert, dass Parallel.For exakt die gleichen physikalischen Zustände erzeugt wie die serielle Schleife.
            const int count = 2000;
            const int steps = 10;
            const double dt = 0.001;

            var fieldSeq = new ParticleField(count, seed: 12345);
            var fieldPar = fieldSeq.Clone();

            for (int s = 0; s < steps; s++)
            {
                fieldSeq.StepSequential(dt);
                fieldPar.StepParallel(dt, maxDegreeOfParallelism: 4);
            }

            Assert.AreEqual(fieldSeq.Count, fieldPar.Count);

            for (int i = 0; i < count; i++)
            {
                Assert.AreEqual(fieldSeq.Particles[i].X, fieldPar.Particles[i].X, 1e-12, $"Particle[{i}].X differs!");
                Assert.AreEqual(fieldSeq.Particles[i].Y, fieldPar.Particles[i].Y, 1e-12, $"Particle[{i}].Y differs!");
                Assert.AreEqual(fieldSeq.Particles[i].Vx, fieldPar.Particles[i].Vx, 1e-12, $"Particle[{i}].Vx differs!");
                Assert.AreEqual(fieldSeq.Particles[i].Vy, fieldPar.Particles[i].Vy, 1e-12, $"Particle[{i}].Vy differs!");
            }
        }

        [TestMethod]
        public void TestAmdahlSpeedupCalculation()
        {
            // Amdahl-Formel: S(p) = 1 / (s + (1 - s)/p)
            // Bei rein parallelem Code (s = 0): S(p) = p
            Assert.AreEqual(4.0, ParallelRunner.AmdahlSpeedup(4, 0.0), 1e-6);

            // Bei rein seriellem Code (s = 1): S(p) = 1
            Assert.AreEqual(1.0, ParallelRunner.AmdahlSpeedup(8, 1.0), 1e-6);

            // Bei s = 0.1 (10% seriell), p = 4:
            // S(4) = 1 / (0.1 + 0.9/4) = 1 / (0.1 + 0.225) = 1 / 0.325 = 3.076923
            double expected = 1.0 / (0.1 + 0.9 / 4.0);
            Assert.AreEqual(expected, ParallelRunner.AmdahlSpeedup(4, 0.1), 1e-5);
        }

        [TestMethod]
        public void TestSerialFractionEstimation()
        {
            double sTheoretical = 0.08;
            int p = 8;
            double speedup = ParallelRunner.AmdahlSpeedup(p, sTheoretical);

            double estimatedS = ParallelRunner.EstimateSerialFraction(speedup, p);
            Assert.AreEqual(sTheoretical, estimatedS, 1e-5);
        }

        [TestMethod]
        public void TestCustomAmdahlReference()
        {
            // Aufgabe 1.4 - "Golden Reference Numbers" aus dem Aufgabenblatt

            // (1) p = 4, s = 0,10  =>  S(4) = 1 / (0,10 + 0,90 / 4) = 3,076923 ; E(4) = 0,7692 (76,9 %)
            double s4 = ParallelRunner.AmdahlSpeedup(4, 0.10);
            Assert.AreEqual(3.076923, s4, 1e-6, "S(4) bei s = 0,10");
            Assert.AreEqual(0.7692, s4 / 4.0, 1e-4, "E(4) bei s = 0,10");
            Assert.AreEqual(s4 / 4.0, ParallelRunner.AmdahlEfficiency(4, 0.10), 1e-12);

            // (2) Gemessener Speedup S(8) = 5,128205  =>  rekonstruierter serieller Anteil s = 0,080 (8,0 %)
            Assert.AreEqual(0.080, ParallelRunner.EstimateSerialFraction(5.128205, 8), 1e-6, "s aus S(8)");

            // (3) Grenz-Speedup S_max = 1/s fuer p -> unendlich
            Assert.AreEqual(10.0, ParallelRunner.AmdahlLimit(0.10), 1e-12);
            Assert.AreEqual(10.0, ParallelRunner.AmdahlSpeedup(1_000_000, 0.10), 1e-3);
            Assert.IsTrue(double.IsPositiveInfinity(ParallelRunner.AmdahlLimit(0.0)));

            // (4) Hin- und Rueckrechnung ist konsistent (Rundlauf ueber mehrere p)
            foreach (int p in new[] { 2, 4, 8, 16 })
            {
                double s = 0.07;
                Assert.AreEqual(s, ParallelRunner.EstimateSerialFraction(ParallelRunner.AmdahlSpeedup(p, s), p), 1e-9);
            }
        }

        [TestMethod]
        public void TestParallelCancellationThrows()
        {
            // Aufgabe 1.2: Ein bereits ausgeloestes Token muss in beiden Pfaden zum kooperativen Abbruch fuehren.
            var field = new ParticleField(5000, seed: 1);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(() => field.StepSequential(0.001, cts.Token));
            Assert.ThrowsException<OperationCanceledException>(() => field.StepParallel(0.001, 4, cts.Token));
        }

        [TestMethod]
        public void TestParallelCancellationLeavesNoChangeBeforeFirstStep()
        {
            // Wurde vor dem ersten Schritt abgebrochen, darf sich der Zustand nicht veraendert haben.
            var field = new ParticleField(1000, seed: 3);
            var reference = field.Clone();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try { field.StepParallel(0.001, 2, cts.Token); } catch (OperationCanceledException) { }

            for (int i = 0; i < field.Count; i++)
            {
                Assert.AreEqual(reference.Particles[i].X, field.Particles[i].X, 0.0);
                Assert.AreEqual(reference.Particles[i].Vx, field.Particles[i].Vx, 0.0);
            }
        }

        [TestMethod]
        public void TestScalingSeriesStructure()
        {
            // Kleine Messreihe: p = 1 ist die Baseline (S = 1, E = 1), jede weitere Stufe liefert s in [0, 1].
            var results = ParallelRunner.RunScalingSeries(
                particleCount: 3000, steps: 2, dt: 0.001, threadCounts: new[] { 1, 2, 4 }, repetitions: 1);

            Assert.AreEqual(3, results.Count);
            Assert.AreEqual(1, results[0].Threads);
            Assert.AreEqual(1.0, results[0].Speedup, 1e-12);
            Assert.AreEqual(1.0, results[0].Efficiency, 1e-12);
            Assert.IsTrue(double.IsNaN(results[0].SerialFraction));

            for (int i = 1; i < results.Count; i++)
            {
                Assert.IsTrue(results[i].ElapsedMilliseconds > 0.0);
                Assert.IsTrue(results[i].SerialFraction >= 0.0 && results[i].SerialFraction <= 1.0);
            }
        }

        [TestMethod]
        public void TestBuildThreadCounts()
        {
            CollectionAssert.AreEqual(new[] { 1, 2, 4, 8 }, ParallelRunner.BuildThreadCounts(8));
            CollectionAssert.AreEqual(new[] { 1, 2, 4, 6 }, ParallelRunner.BuildThreadCounts(6));
            CollectionAssert.AreEqual(new[] { 1 }, ParallelRunner.BuildThreadCounts(1));
        }

        [TestMethod]
        public void TestWarmupRunsSuccessfully()
        {
            // Stellt sicher, dass Warmup ohne Exception durchläuft
            ParallelRunner.Warmup(particleCount: 500, steps: 1);
        }
    }
}
