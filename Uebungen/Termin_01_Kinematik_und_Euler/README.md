# Aufgabenblatt 01: Kinematik, Zustandsraum & Expliziter Euler

**Lehrveranstaltung:** Systemsimulation / Digitaler Zwilling, FH OÖ Campus Wels

**Team:** *Brandstätter Nora, Reiter Victoria*

**Umfang:** Stufe A (Schiefer Wurf im Vakuum) und Stufe B, **Track B** (Artillery Duel mit Newton-Drag & Wind)

**Technik:** C# 12 / .NET 8, reine Konsolenanwendung, keine externen NuGet-Pakete (Tests: MSTest)


## 1. Projektstruktur und Architektur

```
KinematikEuler/
├── Models/
│   ├── KinematicState.cs        Unveränderlicher Zustand [t, x, y, vx, vy]
│   └── BallisticEngine.cs       Physik: Drag, Relativwind, Gelände, Startzustand
├── Solvers/
│   ├── EulerIntegrator.cs       Expliziter Euler + Bodeninterpolation (y = 0)
│   ├── HeunIntegrator.cs        Heun (Prädiktor-Korrektor, RK2)
│   ├── ShotSimulator.cs         Zeitschleife, Kollisionstest, Geländeinterpolation
│   └── AngleFinder.cs           Winkelsweep + Bisektion
├── Output/
│   └── ShotOutput.cs            ASCII-Plot und CSV-Export
└── Program.cs                   Steuerung und Konsolenausgabe
Tests/
└── KinematikEulerTests.cs       Unit-Tests (MSTest)
```

**Entkopplung:**

- `BallisticEngine` kennt weder Integrator noch Konsole. Sie liefert nur die Beschleunigung a(t, x, v).
- Die Integratoren bekommen die Physik als Delegate (`Func<KinematicState, (double ax, double ay)>`) und wissen nichts über Wind oder Gravitation.
- `ShotOutput` und `Program` sind die einzigen Klassen mit Textausgabe bzw. Dateizugriff.
- Es gibt keinen veränderlichen globalen Zustand: Zustände sind `readonly record struct`, Parameter sind `init`-Properties.

---

## 2. Mathematisches Modell

### Zustandsraum

Die Bewegungsgleichung 2. Ordnung ẍ = a(t, x, v) wird in ein System 1. Ordnung überführt:

```
d/dt [x, y, vx, vy]^T = [vx, vy, ax, ay]^T
```

### Expliziter Euler (Ordnung 1)

```
x_{k+1} = x_k + dt * f(t_k, x_k)
```

### Heun / RK2 (Ordnung 2)

```
x~      = x_k + dt * f(t_k, x_k)                         (Prädiktor)
x_{k+1} = x_k + dt/2 * [ f(t_k, x_k) + f(t_k+dt, x~) ]   (Korrektor)
```

### Luftwiderstand mit Relativwind (Track B)

Wind w = (wx, 0), Relativgeschwindigkeit v_rel = (vx − wx, vy), Betrag |v_rel| = sqrt((vx − wx)² + vy²):

```
k  = rho * cw * A / (2 m)
ax = −k * |v_rel| * (vx − wx)
ay = −g − k * |v_rel| * vy
```

Der Betrag wird **vektoriell** aus beiden Komponenten gebildet und nicht komponentenweise quadriert (ax = −k·vx² wäre falsch).
Vorzeichenkonvention: **wx > 0 = Rückenwind** (in Schussrichtung), **wx < 0 = Gegenwind**.

### Ereignisbehandlung (Kollision)

Kollision, sobald y_{k+1} ≤ y_terrain(x_{k+1}). Mit der Abstandsfunktion g = y − y_terrain(x):

```
tau = g_k / (g_k − g_{k+1}) in [0, 1]
t*  = t_k + tau * dt,   x* = x_k + tau * (x_{k+1} − x_k)
```

Damit ist die Einschlagszeit unabhängig vom Zeitraster, und das Geschoss taucht nicht um bis zu einen Schritt ins Gelände ein.
Grenze: Die Interpolation ist linear. Ein sehr schmaler Hügel könnte bei extrem großer Schrittweite zwischen zwei Schritten übersprungen werden. Bei dem hier glatten Geländeprofil tritt das nicht auf.

---

## 3. Stufe A: Schiefer Wurf im Vakuum (Euler)

Parameter: v0 = 150 m/s, α = 45°, g = 9,81 m/s², Start bei (0, 0).

| Variante | t_impact [s] | x_impact [m] | Fehler x [m] | y_max [m] | Fehler y_max [m] |
|---|---|---|---|---|---|
| Analytisch | 21,624 | 2293,58 | – | 573,39 | – |
| Euler dt = 0,1 s | 21,724 | 2304,18 | +10,60 | 578,70 | +5,31 |
| Euler dt = 0,01 s | 21,634 | 2294,64 | +1,06 | 573,92 | +0,53 |

### Antwort auf die Erkenntnisfrage

Die Aufgabenstellung erwartet eine exakte Reichweite (`Diff = 0.000 m`). Unsere Rechnung zeigt etwas anderes:

- Die **Geschwindigkeit** ist beim Euler-Verfahren exakt, weil die Beschleunigung konstant ist (vy,k = vy0 − g·k·dt).
- Der **Ort** nutzt aber die alte Geschwindigkeit des Intervallanfangs und hinkt dadurch um einen halben Schritt hinterher. Die diskrete Bahn ist y_k = vy0·t − g·t·(t − dt)/2, ihre Nullstelle liegt bei **t_ana + dt**.
- Daraus folgt: Der Fehler der Flugzeit ist ca. dt, der Fehler der Reichweite ca. **vx · dt** (≈ 106 m/s · 0,1 s ≈ 10,6 m), und y_max wird um ca. vy0·dt/2 überschätzt.
- Alle Fehler sind O(dt): Eine Verkleinerung von dt um den Faktor 10 verkleinert sie ebenfalls um den Faktor 10. Das bestätigt, dass Euler ein Verfahren 1. Ordnung ist.

Die Reichweite ist also **nicht** exakt. Der Euler-Fehler steckt in Ort und Flugzeit, und die Reichweite erbt ihn über x = vx·t.

---

## 4. Track B: Artillery Duel

Parameter: m = 10 kg, r = 0,06 m, c_w = 0,25, ρ = 1,225 kg/m³, v0 = 160 m/s,
Gelände y(x) = 50·sin(0,002x) + 20·cos(0,005x), Ziel bei x = 1400 m.

### Annahmen und Abweichungen von der Angabe

- **Zielhöhe:** Die Angabe nennt y_ziel ≈ 28,77 m. Nachgerechnet ergibt y_terrain(1400) = **31,83 m**. Das Programm berechnet den Wert selbst (`engine.TargetY`).
- **Starthöhe:** Die Angabe nennt keine Starthöhe. Das Gelände liegt bei x = 0 schon auf 20 m, daher startet das Geschoss auf Geländehöhe (y₀ = 20 m).
- **Wind:** Pro Schuss ist wx konstant, gezogen aus U(−15, +15) m/s.
- **Treffer:** Die Bisektion bricht bei |x_impact − x_ziel| < 0,01 m ab (gefordert: ±1,5 m).

### Vergleich Euler vs. Heun

α = 30°, kein Wind, Referenz: Heun mit dt = 0,001 s (x = 1880,88 m).

| Solver | dt [s] | t_impact [s] | x_impact [m] | Fehler x [m] |
|---|---|---|---|---|
| Euler | 0,1 | 16,304 | 1890,96 | +10,09 |
| Euler | 0,01 | 16,222 | 1881,89 | +1,01 |
| Heun | 0,1 | 16,213 | 1880,85 | −0,02 |
| Heun | 0,01 | 16,213 | 1880,87 | −0,00 |

Beobachtungen:

- Euler: Der Fehler sinkt mit dt linear (Faktor 10 bei dt/10), also Ordnung 1.
- Heun: Der Fehler sinkt bei dt/10 um etwa den Faktor 70 bis 100, also Ordnung 2.
- Heun ist bei dt = 0,1 s schon genauer als Euler bei dt = 0,01 s. Der doppelte Rechenaufwand pro Schritt (zwei Auswertungen von a) lohnt sich also.

### Automatischer Zielwinkel (Sweep + Bisektion)

1. **Sweep** über α ∈ [10°, 80°] in 0,1°-Schritten mit der Fehlerfunktion `error(α) = x_impact(α) − x_ziel`.
2. Bei jedem **Vorzeichenwechsel** liegt nach dem Zwischenwertsatz eine Nullstelle im Intervall.
3. **Bisektion** halbiert das Intervall, bis |error| < 0,01 m (konvergiert garantiert, nach ca. 10 bis 15 Schritten).

Es gibt zwei Lösungen: eine **flache** und eine **steile** Bahn, weil die Reichweite bei etwa 40° ein Maximum hat. Das Spiel nutzt die flache Lösung. Heun, dt = 0,01 s:

| Wind wx [m/s] | Art | α flach [°] | t_flug [s] | α steil [°] |
|---|---|---|---|---|
| −15 | Gegenwind | 21,267 | 10,987 | 63,925 |
| −10 | Gegenwind | 20,948 | 10,847 | 64,888 |
| 0 | kein Wind | 20,378 | 10,596 | 66,598 |
| +10 | Rückenwind | 19,884 | 10,378 | 68,054 |
| +15 | Rückenwind | 19,661 | 10,280 | 68,696 |

**Vorzeichenprüfung:** Bei Gegenwind wird v_rel größer, der Widerstand steigt und die Reichweite sinkt. Die flache Lösung braucht deshalb einen **größeren** Winkel (Flugzeit länger). Bei Rückenwind ist es umgekehrt. Das passt zur Erwartung und ist durch Unit-Tests abgesichert.

### Beispielausgabe eines Schusses

*(Hier die tatsächliche Konsolenausgabe eines Schusses einfügen, inklusive ASCII-Plot.)*

```
--- Schuss 1: Wind wx = ... m/s ---
Zielwinkel = ... Grad | Flugdauer = ... s | x_impact = ... m (Ziel 1400 m)
(ASCII-Plot)
```

Zu jedem Schuss wird eine Datei `shot_<n>.csv` mit den Spalten `t,x,y,vx,vy` geschrieben.

---

## 5. Tests

Ausführen mit `dotnet test`.

| Test | Prüft |
|---|---|
| `Test_ExplicitEuler_FreeFall_AnalyticComparison` | Euler gegen analytischen freien Fall |
| `Test_GroundImpact_Interpolation_ProducesExactZeroY` | Bodeninterpolation (y = 0) |
| `Test_Euler_Convergence_ErrorDecreasesWithHalfStep` | Ordnung 1: Fehler halbiert sich bei dt/2 |
| `Test_Drag_IsComputedFromVectorMagnitude` | Widerstand antiparallel zu v, Betrag aus beiden Komponenten |
| `Test_Wind_Sign_HeadwindBrakesMore_TailwindBrakesLess` | Windvorzeichen, v = w ergibt keinen Widerstand |
| `Test_TerrainInterpolation_FlatTerrain_MatchesGroundInterpolation` | Geländeinterpolation ist konsistent zur Bodeninterpolation |
| `Test_AngleFinder_HitsTarget_UnderHeadAndTailwind` | Ziel wird bei Gegen-, Rücken- und ohne Wind innerhalb ±1,5 m getroffen |
| `Test_Heun_IsMoreAccurateThanEuler_AtSameStepSize` | Heun mindestens 10-mal genauer bei gleichem dt |

---

## 6. Ausführen

```
dotnet run --project KinematikEuler
```

Menü: `1` = Stufe A, `3` = Track B (Track A ist nicht Teil unserer Abgabe).

---

## 7. Selbsteinschätzung nach Bewertungsrubrik

| Kriterium | Umsetzung |
|---|---|
| Architektur & Entkopplung | Model / Solver / Output in getrennten Klassen, keine globalen Zustände |
| Korrektheit der DGL & Relativbewegung | Euler und Heun nach Formel, vektorieller Drag mit v_rel = v − w |
| Ereignis- & Schnittpunktbehandlung | Lineare Interpolation über g = y − y_terrain(x) |
| Stabilitätsanalyse & Parameterstudie | Sweep + Bisektion, getestet bei Gegen- und Rückenwind |
| Dokumentation & Code-Qualität | Vergleichstabelle Euler/Heun, CSV und ASCII-Plot, diese README |
