namespace MultithreadingBenchmark.Infrastructure
{
    /// <summary>
    /// Live umschaltbare Laufzeiteinstellungen. Unveränderlich (immutable): Der UI-Thread legt bei jeder Änderung
    /// eine NEUE Instanz an und veröffentlicht sie per Volatile.Write; der Worker liest sie pro Schritt per Volatile.Read.
    /// Dadurch sieht der Worker stets einen konsistenten Satz (UseParallel, Threads) – ohne Lock.
    /// </summary>
    public sealed record RunSettings(bool UseParallel, int Threads);

    /// <summary>Vom UI-Thread gesetzter neuer Zielpunkt/Attraktor (Weltkoordinaten 0..1000), vom Worker übernommen.</summary>
    public sealed record TargetPoint(double X, double Y);

    /// <summary>
    /// Fortschrittspaket der Echtzeit-Schwarmsimulation (Track B) für IProgress&lt;T&gt;.
    /// Enthält das bereits im Hintergrund gerenderte Bild – der UI-Thread muss nur noch WritePixels aufrufen.
    /// </summary>
    public sealed record SwarmProgress(
        long TotalSteps,
        double MeanStepMs,
        double StepsPerSecond,
        bool UsedParallel,
        int ThreadCount,
        int AgentCount,
        double SequentialReferenceMs,
        uint[] Frame);
}
