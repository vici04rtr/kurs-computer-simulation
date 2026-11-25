using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultithreadingBenchmark.Benchmarks;
using MultithreadingBenchmark.Infrastructure;
using MultithreadingBenchmark.Simulation;

namespace MultithreadingBenchmark
{
    /// <summary>
    /// Datenpaket für die thread-sichere Rückmeldung des Simulationsfortschritts an den WPF-UI-Thread via IProgress&lt;T&gt;.
    /// <para>
    /// <see cref="Frame"/> enthält das im Hintergrund-Thread bereits fertig gerenderte Bild. Der UI-Thread kopiert es nur noch
    /// per WritePixels in die WriteableBitmap und liest dabei NIE das gerade vom Worker veränderte Partikelfeld.
    /// </para>
    /// </summary>
    public record SimulationProgress(
        int CurrentStep,
        int TotalSteps,
        double StepDurationMs,
        bool UsedParallel,
        int ThreadCount,
        uint[]? Frame = null);

    public partial class MainWindow : Window
    {
        private enum Stage
        {
            Particles = 0,   // Stufe 1: Basisaufgabe
            Swarm = 1        // Stufe 2: Track B (Simulation Game)
        }

        // ---- Konstanten --------------------------------------------------------------------------------
        private const int BitmapWidth = 600;
        private const int BitmapHeight = 600;
        private const int ParticleCount = 100_000;
        private const double Dt = 0.002;                 // Stufe 1: Zeitschritt Partikel
        private const float SwarmDt = 0.05f;             // Stufe 2: Zeitschritt Schwarm
        private const int ReportIntervalMs = 33;         // Stufe 2: max. ~30 UI-Updates pro Sekunde (Drosselung)
        private const int MaxFramesInFlight = 2;         // Back-Pressure: max. 2 ungezeigte Frames in der UI-Queue

        // ---- Gemeinsamer Zustand -----------------------------------------------------------------------
        private readonly WriteableBitmap _bitmap;
        private readonly uint[] _pixelBuffer;                       // nur UI-Thread (Leerlauf-Rendering)
        private readonly FrameBufferPool _framePool = new(BitmapWidth * BitmapHeight);

        private Stage _stage = Stage.Particles;
        private bool _isBusy;                // irgendein Hintergrund-Task läuft (Simulation oder Messreihe)
        private bool _simulationRunning;     // eine Simulationsschleife läuft (Worker besitzt dann das Feld)

        // Kooperativer Not-Aus (Aufgabe 1.2) – wird für Simulation UND Skalierungsreihe wiederverwendet
        private CancellationTokenSource? _cts;

        // UI -> Worker: unveränderliche Objekte, per Volatile.Write/Read veröffentlicht (lock-frei, konsistent)
        private TargetPoint? _pendingTarget;
        private RunSettings _particleSettings = new(true, 1);
        private RunSettings _swarmSettings = new(true, 1);
        private int _framesInFlight;         // per Interlocked: Worker erhöht, UI-Thread verringert

        // ---- Stufe 1 -----------------------------------------------------------------------------------
        private readonly ParticleField _field;
        private readonly double _baselineSequentialStepMs;
        private List<BenchmarkResult>? _particleResults;

        // ---- Stufe 2 / Track B -------------------------------------------------------------------------
        private SwarmField _swarm;
        private int _swarmCount = 100_000;
        private List<BenchmarkResult>? _swarmResults;

        public MainWindow()
        {
            InitializeComponent();

            int maxCores = Environment.ProcessorCount;
            int sliderMax = Math.Max(2, maxCores * 2);

            SliderThreads.Maximum = sliderMax;
            SliderThreads.Value = maxCores;
            TxtThreads.Text = maxCores.ToString();

            SliderSwarmThreads.Maximum = sliderMax;
            SliderSwarmThreads.Value = maxCores;
            TxtSwarmThreads.Text = maxCores.ToString();

            _particleSettings = new RunSettings(true, maxCores);
            _swarmSettings = new RunSettings(true, maxCores);

            _field = new ParticleField(ParticleCount, seed: 42)
            {
                AttractorX = 500.0,
                AttractorY = 500.0,
                Mu = 120_000.0,
                Epsilon = 5.0
            };
            _swarm = new SwarmField(_swarmCount, seed: 42);

            _bitmap = new WriteableBitmap(BitmapWidth, BitmapHeight, 96, 96, PixelFormats.Bgra32, null);
            _pixelBuffer = new uint[BitmapWidth * BitmapHeight];
            ParticleImage.Source = _bitmap;

            // Einmalige sequentielle Baseline-Messung (Warmup + Referenzzeit T_1) auf einem Wegwerf-Feld,
            // damit der Ausgangszustand von _field unverändert (Seed 42) bleibt.
            ParallelRunner.Warmup(5000, 1);
            var probe = new ParticleField(ParticleCount, seed: 42);
            probe.StepSequential(Dt);   // Vorlauf (Cache/JIT), nicht gewertet
            double best = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                probe.StepSequential(Dt);
                best = Math.Min(best, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            _baselineSequentialStepMs = Math.Max(0.1, best);

            ApplyStage(Stage.Particles);
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();   // laufende Hintergrundarbeit sauber anhalten
            base.OnClosed(e);
        }

        // ====================================================================
        // Hilfsmethoden (nur UI-Thread)
        // ====================================================================

        private void WriteBitmap(uint[] buffer)
        {
            _bitmap.WritePixels(
                new Int32Rect(0, 0, BitmapWidth, BitmapHeight),
                buffer,
                BitmapWidth * sizeof(uint),
                0);
        }

        /// <summary>Rendert den aktuellen Zustand direkt auf dem UI-Thread – NUR erlaubt, wenn kein Worker läuft.</summary>
        private void RenderIdle()
        {
            if (_stage == Stage.Particles)
            {
                _field.RenderToPixelBuffer(_pixelBuffer, BitmapWidth, BitmapHeight);
            }
            else
            {
                _swarm.RenderToPixelBuffer(_pixelBuffer, BitmapWidth, BitmapHeight);
            }
            WriteBitmap(_pixelBuffer);
        }

        private void SetBusy(bool busy, bool simulation = false)
        {
            _isBusy = busy;
            _simulationRunning = busy && simulation;

            BtnStart.IsEnabled = !busy;
            BtnSwarmStart.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy && _stage == Stage.Particles;
            BtnSwarmCancel.IsEnabled = busy && _stage == Stage.Swarm;

            BtnResetParticles.IsEnabled = !busy;
            BtnRunScaling.IsEnabled = !busy;
            CmbStage.IsEnabled = !busy;
            CmbAgentCount.IsEnabled = !busy;
        }

        private void ApplyStage(Stage stage)
        {
            _stage = stage;
            bool particles = stage == Stage.Particles;

            PanelStage1.Visibility = particles ? Visibility.Visible : Visibility.Collapsed;
            PanelStage2.Visibility = particles ? Visibility.Collapsed : Visibility.Visible;

            int maxCores = Environment.ProcessorCount;
            if (particles)
            {
                TxtTitle.Text = "Parallele Partikelsimulation";
                TxtSubHeader.Text = $"Stufe 1 | Host-System: {maxCores} logische Prozessorkerne | N = {ParticleCount:N0} Partikel";
                TxtViewportHint.Text = "Linksklick ins Bild: Gravitations-Attraktor (rotes Fadenkreuz) verschieben";
                BtnResetParticles.Content = "Partikel zurücksetzen";
                ScalingGrid.ItemsSource = _particleResults;
                TxtScalingSummary.Text = _particleResults == null
                    ? "Noch keine Messreihe durchgeführt."
                    : BuildScalingSummary(_particleResults);
                TxtStatus.Text = $"Stufe 1 aktiv. N = {ParticleCount:N0} Partikel initialisiert.";
            }
            else
            {
                TxtTitle.Text = "Track B: Schwarm- & Zombie-Horde";
                TxtSubHeader.Text = $"Stufe 2 / Track B | Host-System: {maxCores} logische Prozessorkerne | N = {_swarmCount:N0} Agenten";
                TxtViewportHint.Text = "Mausklick/Ziehen: Spieler-Ziel P_target setzen\n" +
                                       "Grün = Walker (langsam), Orange = Runner (schnell), Grau = Hindernisse";
                BtnResetParticles.Content = "Agenten zurücksetzen";
                ScalingGrid.ItemsSource = _swarmResults;
                TxtScalingSummary.Text = _swarmResults == null
                    ? "Noch keine Messreihe durchgeführt."
                    : BuildScalingSummary(_swarmResults);
                TxtStatus.Text = $"Stufe 2 / Track B aktiv. N = {_swarmCount:N0} Agenten initialisiert.";
            }

            SetBusy(false);
            RenderIdle();
        }

        private void CmbStage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || e.OriginalSource != CmbStage) return;

            if (_isBusy)
            {
                // Sollte nicht vorkommen (ComboBox ist während des Laufs deaktiviert) – Sicherheitsnetz
                CmbStage.SelectedIndex = (int)_stage;
                return;
            }

            ApplyStage(CmbStage.SelectedIndex == 1 ? Stage.Swarm : Stage.Particles);
        }

        // ====================================================================
        // Live-Einstellungen (UI -> Worker, lock-frei)
        // ====================================================================

        private void PublishParticleSettings()
        {
            Volatile.Write(ref _particleSettings,
                new RunSettings(ChkUseParallel.IsChecked == true, (int)SliderThreads.Value));
        }

        private void PublishSwarmSettings()
        {
            Volatile.Write(ref _swarmSettings,
                new RunSettings(ChkSwarmParallel.IsChecked == true, (int)SliderSwarmThreads.Value));
        }

        private void ChkUseParallel_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            PublishParticleSettings();
        }

        private void SliderThreads_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || TxtThreads == null) return;
            TxtThreads.Text = ((int)SliderThreads.Value).ToString();
            PublishParticleSettings();
        }

        private void ChkSwarmParallel_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            PublishSwarmSettings();
        }

        private void SliderSwarmThreads_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || TxtSwarmThreads == null) return;
            TxtSwarmThreads.Text = ((int)SliderSwarmThreads.Value).ToString();
            PublishSwarmSettings();
        }

        private void CmbAgentCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _isBusy || e.OriginalSource != CmbAgentCount) return;
            if (CmbAgentCount.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

            _swarmCount = int.Parse(tag);
            _swarm = new SwarmField(_swarmCount, seed: 42);
            _swarmResults = null;                       // Messreihe gehört zur alten Agentenzahl
            ScalingGrid.ItemsSource = null;
            TxtScalingSummary.Text = "Noch keine Messreihe durchgeführt.";
            TxtSubHeader.Text = $"Stufe 2 / Track B | Host-System: {Environment.ProcessorCount} logische Prozessorkerne | N = {_swarmCount:N0} Agenten";
            TxtStatus.Text = $"Schwarm mit N = {_swarmCount:N0} Agenten neu initialisiert.";
            RenderIdle();
        }

        // ====================================================================
        // Maus: Attraktor (Stufe 1) bzw. Spieler-Ziel (Stufe 2)
        // ====================================================================

        private void ParticleImage_MouseDown(object sender, MouseButtonEventArgs e) => ApplyTargetFromMouse(e);

        private void ParticleImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) ApplyTargetFromMouse(e);
        }

        private void ApplyTargetFromMouse(MouseEventArgs e)
        {
            if (ParticleImage.ActualWidth <= 0 || ParticleImage.ActualHeight <= 0) return;

            Point pos = e.GetPosition(ParticleImage);
            double wx = Math.Clamp(pos.X / ParticleImage.ActualWidth * 1000.0, 0.0, 1000.0);
            double wy = Math.Clamp(pos.Y / ParticleImage.ActualHeight * 1000.0, 0.0, 1000.0);

            if (_simulationRunning)
            {
                // Der Worker besitzt das Feld: UI-Thread schreibt NICHT hinein, sondern hinterlegt nur einen
                // unveränderlichen Zielpunkt, den der Worker zu Beginn des nächsten Zeitschritts übernimmt.
                Volatile.Write(ref _pendingTarget, new TargetPoint(wx, wy));
                return;
            }
            if (_isBusy) return;   // Messreihe läuft: Eingabe ignorieren

            if (_stage == Stage.Particles)
            {
                _field.AttractorX = wx;
                _field.AttractorY = wy;
                TxtStatus.Text = $"Gravitations-Attraktor verschoben nach ({wx:F0}, {wy:F0}) m.";
            }
            else
            {
                _swarm.SetTarget(wx, wy);
                TxtStatus.Text = $"Spieler-Ziel P_target gesetzt auf ({wx:F0}, {wy:F0}).";
            }
            RenderIdle();
        }

        private void BtnResetParticles_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (_stage == Stage.Particles)
            {
                _field.Initialize(seed: 42);
                _field.AttractorX = 500.0;
                _field.AttractorY = 500.0;
                SimProgressBar.Value = 0;
                TxtStatus.Text = "Partikelfeld auf Ausgangszustand (Seed 42) zurückgesetzt.";
            }
            else
            {
                _swarm.Initialize(seed: 42);
                _swarm.SetTarget(500.0, 500.0);
                TxtStatus.Text = "Schwarm auf Ausgangszustand (Seed 42) zurückgesetzt.";
            }
            RenderIdle();
        }

        // ====================================================================
        // STUFE 1 – Aufgabe 1.1 / 1.2: Reaktive Entkopplung mit Task.Run, IProgress<T>, CancellationToken
        // ====================================================================

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            const int totalSteps = 200;

            // Aufgabe 1.2: neue CancellationTokenSource je Lauf, Not-Aus-Button wird aktiviert (in SetBusy)
            var cts = new CancellationTokenSource();
            _cts = cts;
            CancellationToken token = cts.Token;

            Volatile.Write(ref _pendingTarget, null);
            PublishParticleSettings();
            SetBusy(true, simulation: true);
            SimProgressBar.Value = 0;
            TxtStatus.Text = "Berechnung läuft im Hintergrund (Task.Run) – das Fenster bleibt bedienbar ...";

            // Aufgabe 1.1: Progress<T> wird im UI-Thread erzeugt und fängt dessen SynchronizationContext ein;
            // der Callback läuft daher IMMER im UI-Thread (hier ist WritePixels/ProgressBar erlaubt).
            IProgress<SimulationProgress> progress = new Progress<SimulationProgress>(p => UpdateUiProgress(p));

            try
            {
                await Task.Run(() => RunParticleLoop(totalSteps, progress, token), token);
                TxtStatus.Text = $"Simulation abgeschlossen ({totalSteps} Schritte).";
            }
            catch (OperationCanceledException)
            {
                TxtStatus.Text = "Simulation durch Benutzer (Not-Aus) abgebrochen.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Fehler in der Simulation: {ex.Message}";
            }
            finally
            {
                cts.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        /// <summary>
        /// Simulationsschleife – läuft im Thread-Pool (Task.Run), berührt KEIN WPF-Objekt.
        /// Meldet nur alle 5 Schritte (gedrosselt) einen Zwischenstand inkl. fertig gerendertem Bild.
        /// </summary>
        private void RunParticleLoop(int totalSteps, IProgress<SimulationProgress> progress, CancellationToken token)
        {
            long windowTicks = 0;
            int windowSteps = 0;

            for (int step = 1; step <= totalSteps; step++)
            {
                token.ThrowIfCancellationRequested();

                // Neuen Attraktor aus der UI übernehmen (nur der Worker schreibt ins Feld, solange er läuft)
                if (Volatile.Read(ref _pendingTarget) is { } target)
                {
                    _field.AttractorX = target.X;
                    _field.AttractorY = target.Y;
                }

                RunSettings cfg = Volatile.Read(ref _particleSettings);

                long t0 = Stopwatch.GetTimestamp();
                if (cfg.UseParallel)
                {
                    _field.StepParallel(Dt, cfg.Threads, token);
                }
                else
                {
                    _field.StepSequential(Dt, token);
                }
                windowTicks += Stopwatch.GetTimestamp() - t0;
                windowSteps++;

                if (step % 5 == 0 || step == totalSteps)
                {
                    double meanMs = windowTicks * 1000.0 / Stopwatch.Frequency / windowSteps;

                    // Bild im Worker rendern (reiner CPU-Code); UI-Thread kopiert es nur noch in die Bitmap.
                    uint[] frame = _framePool.Rent();
                    _field.RenderToPixelBuffer(frame, BitmapWidth, BitmapHeight);

                    progress.Report(new SimulationProgress(
                        step, totalSteps, meanMs, cfg.UseParallel, cfg.UseParallel ? cfg.Threads : 1, frame));

                    windowTicks = 0;
                    windowSteps = 0;
                }
            }
        }

        /// <summary>Läuft ausschließlich im UI-Thread (Aufruf über Progress&lt;T&gt;).</summary>
        private void UpdateUiProgress(SimulationProgress p)
        {
            SimProgressBar.Value = (double)p.CurrentStep / p.TotalSteps * 100.0;

            if (p.Frame != null)
            {
                WriteBitmap(p.Frame);
                _framePool.Return(p.Frame);
            }

            double fps = 1000.0 / Math.Max(0.01, p.StepDurationMs);
            double speedup = _baselineSequentialStepMs / Math.Max(0.01, p.StepDurationMs);
            double estS = p.UsedParallel ? ParallelRunner.EstimateSerialFraction(speedup, p.ThreadCount) : 1.0;

            TxtStepTime.Text = $"Mittlere Schrittzeit:  {p.StepDurationMs,7:F2} ms/step";
            TxtFps.Text = $"Simulations-Durchsatz: {fps,7:F1} Steps/s (FPS)";
            TxtSpeedup.Text = $"Gemessener Speedup:    {speedup,7:F2} x (vs. 1 Kern)";
            TxtAmdahlS.Text = $"Serieller Anteil s:    {estS * 100.0,7:F2} %";
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            // Aufgabe 1.2: kooperativer Abbruch – Parallel.For / for-Schleife prüfen das Token
            RequestCancel(BtnCancel);
        }

        private void BtnSwarmCancel_Click(object sender, RoutedEventArgs e)
        {
            RequestCancel(BtnSwarmCancel);
        }

        private void RequestCancel(Button cancelButton)
        {
            if (_cts == null) return;
            _cts.Cancel();
            cancelButton.IsEnabled = false;
            TxtStatus.Text = "Not-Aus ausgelöst – warte auf kooperativen Abbruch ...";
        }

        // ====================================================================
        // STUFE 2 / TRACK B – Echtzeit-Schwarmsimulation (Endlosschleife im Hintergrund)
        // ====================================================================

        private async void BtnSwarmStart_Click(object sender, RoutedEventArgs e)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            CancellationToken token = cts.Token;

            Volatile.Write(ref _pendingTarget, null);
            Interlocked.Exchange(ref _framesInFlight, 0);
            PublishSwarmSettings();
            SetBusy(true, simulation: true);
            TxtStatus.Text = "Kalibriere sequentielle Referenzzeit T_1 (auf Klon, JIT-Warmup) ...";

            SwarmField swarm = _swarm;
            IProgress<SwarmProgress> progress = new Progress<SwarmProgress>(p => UpdateSwarmUi(p));

            try
            {
                await Task.Run(() => RunSwarmLoop(swarm, progress, token), token);
            }
            catch (OperationCanceledException)
            {
                TxtStatus.Text = "Echtzeit-Simulation durch Benutzer (Not-Aus) gestoppt – Zustand bleibt erhalten.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Fehler in der Schwarmsimulation: {ex.Message}";
            }
            finally
            {
                cts.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        /// <summary>
        /// Endlos-Echtzeitschleife (Task.Run). Pro Iteration ein Zeitschritt (sequentiell ODER Parallel.For, live
        /// umschaltbar), UI-Meldung höchstens alle <see cref="ReportIntervalMs"/> ms und nur, wenn die UI nicht
        /// bereits mit ungezeigten Frames im Rückstand ist (Back-Pressure per Interlocked).
        /// Beendet wird sie ausschließlich über das CancellationToken (OperationCanceledException).
        /// </summary>
        private void RunSwarmLoop(SwarmField swarm, IProgress<SwarmProgress> progress, CancellationToken token)
        {
            // Sequentielle Referenz T_1 für die Live-Speedup-Anzeige (auf einem Klon – Spielzustand bleibt unberührt)
            double seqRefMs = ParallelRunner.MeasureSequentialStepMs(swarm, SwarmDt, 3, token);

            long reportIntervalTicks = Stopwatch.Frequency * ReportIntervalMs / 1000;
            long windowStart = Stopwatch.GetTimestamp();
            long windowStepTicks = 0;
            int windowSteps = 0;
            RunSettings? lastCfg = null;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (Volatile.Read(ref _pendingTarget) is { } target)
                {
                    swarm.SetTarget(target.X, target.Y);
                }

                RunSettings cfg = Volatile.Read(ref _swarmSettings);
                if (!ReferenceEquals(cfg, lastCfg))
                {
                    // Modus/Threads wurden live umgeschaltet: Mess-Fenster neu beginnen (keine Mischwerte anzeigen)
                    lastCfg = cfg;
                    windowStart = Stopwatch.GetTimestamp();
                    windowStepTicks = 0;
                    windowSteps = 0;
                }

                long t0 = Stopwatch.GetTimestamp();
                if (cfg.UseParallel)
                {
                    swarm.StepParallel(SwarmDt, cfg.Threads, token);
                }
                else
                {
                    swarm.StepSequential(SwarmDt, token);
                }
                long now = Stopwatch.GetTimestamp();
                windowStepTicks += now - t0;
                windowSteps++;

                if (now - windowStart >= reportIntervalTicks && Volatile.Read(ref _framesInFlight) < MaxFramesInFlight)
                {
                    double meanMs = windowStepTicks * 1000.0 / Stopwatch.Frequency / windowSteps;
                    double windowSeconds = (now - windowStart) / (double)Stopwatch.Frequency;

                    // Im sequentiellen Modus ist die gemessene Schrittzeit selbst die aktuelle Referenz T_1
                    if (!cfg.UseParallel) seqRefMs = meanMs;

                    uint[] frame = _framePool.Rent();
                    swarm.RenderToPixelBuffer(frame, BitmapWidth, BitmapHeight);

                    Interlocked.Increment(ref _framesInFlight);
                    progress.Report(new SwarmProgress(
                        swarm.StepCount,
                        meanMs,
                        windowSteps / windowSeconds,
                        cfg.UseParallel,
                        cfg.UseParallel ? cfg.Threads : 1,
                        swarm.Count,
                        seqRefMs,
                        frame));

                    windowStart = Stopwatch.GetTimestamp();
                    windowStepTicks = 0;
                    windowSteps = 0;
                }
            }
        }

        /// <summary>Läuft ausschließlich im UI-Thread (Aufruf über Progress&lt;T&gt;).</summary>
        private void UpdateSwarmUi(SwarmProgress p)
        {
            WriteBitmap(p.Frame);
            _framePool.Return(p.Frame);
            Interlocked.Decrement(ref _framesInFlight);

            double speedup = p.UsedParallel ? p.SequentialReferenceMs / Math.Max(0.01, p.MeanStepMs) : 1.0;
            double estS = p.UsedParallel ? ParallelRunner.EstimateSerialFraction(speedup, p.ThreadCount) : 1.0;

            TxtSwarmMode.Text = p.UsedParallel
                ? $"Modus: TPL Parallel.For, p = {p.ThreadCount}"
                : "Modus: sequentiell (1 Thread)";
            TxtSwarmStepTime.Text = $"Mittlere Schrittzeit:  {p.MeanStepMs,7:F2} ms/step";
            TxtSwarmFps.Text = $"Simulations-Durchsatz: {p.StepsPerSecond,7:F1} Steps/s (FPS)";
            TxtSwarmSpeedup.Text = $"Gemessener Speedup:    {speedup,7:F2} x (vs. T_1 = {p.SequentialReferenceMs:F1} ms)";
            TxtSwarmAmdahl.Text = $"Serieller Anteil s:    {estS * 100.0,7:F2} %";
            TxtSwarmSteps.Text = $"Simulierte Schritte:   {p.TotalSteps,7:N0}  (N = {p.AgentCount:N0})";

            TxtStatus.Text = p.UsedParallel
                ? $"Echtzeit läuft: {p.StepsPerSecond:F0} Steps/s mit {p.ThreadCount} Threads."
                : $"Echtzeit läuft: {p.StepsPerSecond:F0} Steps/s sequentiell (1 Thread).";
        }

        // ====================================================================
        // Aufgabe 1.3 / B.4: Asynchrone Amdahl-Skalierungsreihe (1 .. P_max Kerne)
        // ====================================================================

        private async void BtnRunScaling_Click(object sender, RoutedEventArgs e)
        {
            int maxCores = Environment.ProcessorCount;
            int[] threadCounts = ParallelRunner.BuildThreadCounts(maxCores);
            Stage stage = _stage;
            int swarmCount = _swarmCount;

            var cts = new CancellationTokenSource();
            _cts = cts;
            CancellationToken token = cts.Token;

            SetBusy(true, simulation: false);
            ScalingProgressBar.Value = 0;
            TxtScalingSummary.Text = "Messreihe läuft ...";
            TxtStatus.Text = "Berechne Amdahl-Skalierungsreihe (im Hintergrund) ...";

            IProgress<ScalingProgress> progress = new Progress<ScalingProgress>(sp =>
            {
                ScalingProgressBar.Value = sp.Total == 0 ? 0 : 100.0 * sp.Done / sp.Total;
                TxtStatus.Text = sp.Message;
            });

            try
            {
                // Aufgabe 1.3: die gesamte mehrstufige Messung läuft im Hintergrund (await Task.Run) – UI bleibt frei
                List<BenchmarkResult> results = await Task.Run(() =>
                    stage == Stage.Particles
                        ? ParallelRunner.RunScalingSeries(ParticleCount, steps: 15, dt: Dt, threadCounts, 3, progress, token)
                        : ParallelRunner.RunSwarmScalingSeries(swarmCount, steps: 30, dt: SwarmDt, threadCounts, 3, progress, token),
                    token);

                if (stage == Stage.Particles) _particleResults = results; else _swarmResults = results;

                if (_stage == stage)   // Stufe kann nicht gewechselt worden sein (ComboBox gesperrt) – nur zur Sicherheit
                {
                    ScalingGrid.ItemsSource = results;
                    TxtScalingSummary.Text = BuildScalingSummary(results);
                }
                ScalingProgressBar.Value = 100;
                TxtStatus.Text = $"Skalierungsreihe für {results.Count} Thread-Stufen abgeschlossen.";
            }
            catch (OperationCanceledException)
            {
                TxtScalingSummary.Text = "Messreihe durch Benutzer (Not-Aus) abgebrochen.";
                TxtStatus.Text = "Skalierungsreihe durch Benutzer (Not-Aus) abgebrochen.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Fehler in der Skalierungsreihe: {ex.Message}";
            }
            finally
            {
                cts.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        private static string BuildScalingSummary(List<BenchmarkResult> results)
        {
            double s = ParallelRunner.MeanSerialFraction(results);
            if (double.IsNaN(s))
            {
                return "Keine Stufe mit p > 1 vorhanden – serieller Anteil nicht bestimmbar.";
            }

            double limit = ParallelRunner.AmdahlLimit(s);
            string limitText = double.IsPositiveInfinity(limit) ? "unbegrenzt" : limit.ToString("F1");
            BenchmarkResult best = results.OrderByDescending(r => r.Speedup).First();

            return $"Ø serieller Anteil s (p > 1): {s:P1}\n" +
                   $"Grenz-Speedup S_max = 1/s ≈ {limitText}\n" +
                   $"Bester Speedup: S({best.Threads}) = {best.Speedup:F2}  (E = {best.Efficiency:P0})";
        }
    }
}
