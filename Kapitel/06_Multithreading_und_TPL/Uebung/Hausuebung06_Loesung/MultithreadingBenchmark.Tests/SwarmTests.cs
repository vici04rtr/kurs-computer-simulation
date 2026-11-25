using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MultithreadingBenchmark.Benchmarks;
using MultithreadingBenchmark.Simulation;

namespace MultithreadingBenchmark.Tests
{
    /// <summary>Tests für Stufe 2 / Track B: Schwarm-Simulation mit striktem Double-Buffering.</summary>
    [TestClass]
    public class SwarmTests
    {
        private const float Dt = 0.05f;

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(4)]
        [DataRow(7)]
        public void TestSwarmSequentialEqualsParallelBitExact(int threads)
        {
            // Race-Condition-Check (Leitfrage 3): Dank Double-Buffering muss das Ergebnis UNABHAENGIG von der
            // Thread-Anzahl bitgleich zur sequentiellen Referenz sein. Fehlendes Double-Buffering wuerde hier auffallen.
            var seq = new SwarmField(5000, seed: 7);
            var par = seq.Clone();

            for (int step = 0; step < 25; step++)
            {
                seq.StepSequential(Dt);
                par.StepParallel(Dt, threads);
            }

            Assert.AreEqual(seq.StepCount, par.StepCount);
            for (int i = 0; i < seq.Count; i++)
            {
                var a = seq.StateCurrent[i];
                var b = par.StateCurrent[i];
                Assert.AreEqual(a.X, b.X, 0f, $"Agent[{i}].X");
                Assert.AreEqual(a.Y, b.Y, 0f, $"Agent[{i}].Y");
                Assert.AreEqual(a.Vx, b.Vx, 0f, $"Agent[{i}].Vx");
                Assert.AreEqual(a.Vy, b.Vy, 0f, $"Agent[{i}].Vy");
                Assert.AreEqual(a.Type, b.Type);
            }
        }

        [TestMethod]
        public void TestSwarmPhysicalInvariants()
        {
            // Agenten bleiben auf der Karte, ausserhalb der Hindernisse und unter v_max.
            var swarm = new SwarmField(10000, seed: 3);
            swarm.SetTarget(500, 500);

            for (int step = 0; step < 100; step++)
            {
                swarm.StepParallel(Dt, 4);
            }

            Assert.AreEqual(10000, swarm.Count);
            foreach (var a in swarm.StateCurrent)
            {
                Assert.IsFalse(float.IsNaN(a.X) || float.IsNaN(a.Y) || float.IsNaN(a.Vx) || float.IsNaN(a.Vy));
                Assert.IsTrue(a.X >= 0f && a.X <= SwarmField.WorldSize);
                Assert.IsTrue(a.Y >= 0f && a.Y <= SwarmField.WorldSize);

                float vmax = a.Type == AgentType.Runner ? swarm.RunnerMaxSpeed : swarm.WalkerMaxSpeed;
                Assert.IsTrue(MathF.Sqrt(a.Vx * a.Vx + a.Vy * a.Vy) <= vmax * 1.001f, "||v|| <= v_max verletzt");

                foreach (var o in swarm.Obstacles)
                {
                    float d = MathF.Sqrt((a.X - o.X) * (a.X - o.X) + (a.Y - o.Y) * (a.Y - o.Y));
                    Assert.IsTrue(d >= o.Radius - 0.05f, "Agent steckt im Kreishindernis");
                }
            }
        }

        [TestMethod]
        public void TestSwarmMovesTowardsTarget()
        {
            // Zielverfolgung: Die mittlere Entfernung zum Spieler-Ziel muss deutlich sinken.
            var swarm = new SwarmField(5000, seed: 11);
            swarm.SetTarget(500, 500);
            double before = MeanDistanceTo(swarm, 500, 500);

            for (int step = 0; step < 150; step++)
            {
                swarm.StepParallel(Dt, 4);
            }

            double after = MeanDistanceTo(swarm, 500, 500);
            Assert.IsTrue(after < 0.8 * before, $"Mittlere Distanz vorher {before:F1}, nachher {after:F1}");
        }

        [TestMethod]
        public void TestSwarmDoubleBufferingSwapsReferences()
        {
            var swarm = new SwarmField(1000, seed: 5);
            Agent[] currentBefore = swarm.StateCurrent;
            Agent[] nextBefore = swarm.StateNext;

            swarm.StepSequential(Dt);

            Assert.AreSame(nextBefore, swarm.StateCurrent, "Nach dem Schritt muss der Next-Puffer zum Current-Puffer werden.");
            Assert.AreSame(currentBefore, swarm.StateNext, "Der alte Current-Puffer wird zum neuen Scratch-Puffer.");
            Assert.AreEqual(1L, swarm.StepCount);
        }

        [TestMethod]
        public void TestSwarmCancellationKeepsStateConsistent()
        {
            var swarm = new SwarmField(2000, seed: 9);
            var reference = swarm.Clone();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(() => swarm.StepSequential(Dt, cts.Token));
            Assert.ThrowsException<OperationCanceledException>(() => swarm.StepParallel(Dt, 4, cts.Token));

            Assert.AreEqual(0L, swarm.StepCount, "Abgebrochene Schritte duerfen nicht gezaehlt/getauscht werden.");
            for (int i = 0; i < swarm.Count; i++)
            {
                Assert.AreEqual(reference.StateCurrent[i].X, swarm.StateCurrent[i].X, 0f);
                Assert.AreEqual(reference.StateCurrent[i].Vy, swarm.StateCurrent[i].Vy, 0f);
            }
        }

        [TestMethod]
        public void TestSwarmScalingSeriesAndWarmup()
        {
            ParallelRunner.WarmupSwarm(agentCount: 500, steps: 1);

            var results = ParallelRunner.RunSwarmScalingSeries(
                agentCount: 3000, steps: 2, dt: Dt, threadCounts: new[] { 1, 2, 4 }, repetitions: 1);

            Assert.AreEqual(3, results.Count);
            Assert.AreEqual(1.0, results[0].Speedup, 1e-12);
            Assert.IsTrue(results[1].Speedup > 0.0 && results[2].Speedup > 0.0);
        }

        private static double MeanDistanceTo(SwarmField swarm, double x, double y)
        {
            double sum = 0.0;
            foreach (var a in swarm.StateCurrent)
            {
                sum += Math.Sqrt((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y));
            }
            return sum / swarm.Count;
        }
    }
}
