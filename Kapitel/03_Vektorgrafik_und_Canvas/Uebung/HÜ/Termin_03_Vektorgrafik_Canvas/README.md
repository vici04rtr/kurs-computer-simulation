# Track B.2: Bridge Blueprint Sketcher – Interaktiver Brückenbau-Editor

**Lehrveranstaltung:** Systemsimulation / Digitaler Zwilling  
**Studiengang:** B.Sc. Automatisierungstechnik (5. Semester)  
**Teammitglieder:** Reiter Victoria, Brandstätter Nora  
**Institution:** Fachhochschule Oberösterreich – Campus Wels  
**Modul:** Aufgabenblatt 03 – Vektorgrafik, WPF Canvas & Koordinatentransformation  

---

## 1. Projektübersicht

Der **Bridge Blueprint Sketcher** ist ein interaktiver 2D-CAD-Zeichen- und Entwurfsmodus für Fachwerkträger. Die Anwendung basiert auf rein vektorieller 2D-Grafik (`System.Windows.Controls.Canvas`) und führt exakte mathematische Transformationen zwischen ingenieurtechnischen Weltkoordinaten $[m]$ und Bildschirm-Canvas-Pixeln $[px]$ durch.

Das Projekt ist didaktisch sauber strukturiert: Es verzichtet vollständig auf vorzeitige Statik-Berechnungen (FEM) und konzentriert sich auf die performante, flüssige Interaktivität sowie die Persistenz von Tragwerkstopologien.

---

## 2. Funktionsumfang & Features

* **Affine Welt-zu-Bildschirm-Transformation (`Transform2D`):**
  * Isotropes Seitenverhältnis ($s = \min(s_x, s_y)$) zur geschützten Wahrung der Proportionalität[cite: 1].
  * Korrekte Inversion der vertikalen Achse ($Y_{\text{Welt}}$ positiv nach oben vs. $Y_{\text{Canvas}}$ positiv nach unten)[cite: 1].
  * Auto-Fit & Zentrierung der Bounding-Box mit konfigurierbarem Randabstand (`Margin`)[cite: 1].

* **Zuschaltbares Raster-Snapping (0.5 m Grid):**
  * Über eine CheckBox im Header kann das Snapping aktiviert/deaktiviert werden[cite: 1].
  * Beim Platzieren und Ziehen von Knoten rasten diese im aktivierten Zustand präzise auf $0{,}5\,\text{m}$-Gitterpunkte ein[cite: 1].

* **Interaktives Erstellen & Verbinden (Klick-zu-Klick):**
  * **Knoten erstellen:** Linksklick auf eine freie Canvas-Fläche[cite: 1].
  * **Stäbe zeichnen:** `Shift` + Linksklick auf den Startknoten (wird gelb markiert) und anschließender Klick auf den Zielknoten[cite: 1].
  * **Knoten verschieben:** Linksklick und Ziehen (Drag & Drop) bestehender Knoten mit flüssigem Mouse-Tracking[cite: 1].

* **Normgerechte Bemaßung & Stablängen:**
  * **Stablängen-Beschriftung:** Für jeden gezeichneten Stab wird über den Satz des Pythagoras automatisch die euklidische Länge in Metern berechnet und als gelbes Textlabel im Stabmittelpunkt angezeigt[cite: 1].
  * **Gesamt-Bemaßungsketten (DIN 406):** Automatische Generierung von Maßhilfslinien, Maßlinien mit Pfeilen und zentrierten Maßzahlen für die horizontale Gesamtspannweite (unten) sowie die vertikale Gesamthöhe (links)[cite: 1].

* **Lager- & Lastenmanagement:**
  * **Lager umschalten (Zyklus):** Rechtsklick auf einen Knoten schaltet den Zustand im Kreis durch (*Kein Lager* $\to$ *Festlager* $\to$ *Loselager* $\to$ *Kein Lager*)[cite: 1].
  * **Einzellasten aufbringen:** `Ctrl` + Linksklick auf einen Knoten fügt eine vertikale Kraft von $-25\,\text{kN}$ hinzu, die über vektorielle Kraftpfeile (`VectorArrow`) dargestellt wird[cite: 1].

* **Mehrfachauswahl & Löschen:**
  * **Auswahl:** `Alt` + Linksklick ermöglicht das Markieren mehrerer Knoten (optische Hervorhebung in Orange/Red)[cite: 1].
  * **Löschen:** Taste `Entf` (Delete) entfernt die ausgewählten Knoten mitsamt aller daran angeschlossenen Stäbe und Lasten[cite: 1].

* **JSON Import & Export (`System.Text.Json`):**
  * Das erstellte Tragwerk (Knoten, Stäbe, Lager, Lasten) kann per Knopfdruck als strukturiertes `.json` gespeichert werden[cite: 1].
  * *Hinweis:* Diese Datei dient als direkte Schnittstelle für spätere Übungseinheiten (z. B. FEM-Cholesky-Solver in T07)[cite: 1].

---

## 3. Tastatur- & Maussteuerung (Legende)

| Eingabe / Aktion | Beschreibung / Funktion |
| :--- | :--- |
| **Linksklick ins Leere** | Erstellt einen neuen Knoten bei den Mauskoordinaten. |
| **Linksklick + Ziehen** | Verschiebt einen bestehenden Knoten per Drag & Drop. |
| **`Shift` + Linksklick** | Wählt einen Startknoten; zweiter Klick verbindet mit neuem Stab. |
| **`Alt` + Linksklick** | Fügt Knoten zur Mehrfachauswahl hinzu / entfernt sie. |
| **`Ctrl` + Linksklick** | Bringt eine Vertikallast ($-25\,\text{kN}$) auf bzw. entfernt sie. |
| **Rechtsklick auf Knoten** | Schaltet Lager um (*Kein Lager* $\to$ *Festlager* $\to$ *Loselager*). |
| **Taste `Entf` (Delete)** | Löscht ausgewählte Knoten (oder den zuletzt erstellten Knoten). |

---

## 4. Technische Umsetzung & Software-Architektur

* **`BridgeSketcherWindow.xaml` / `.cs`:** Eigenständiges WPF-Fenster für den Sketcher-Modus mit integriertem Legenden-Overlay (`IsHitTestVisible="False"`).
* **`TrussGeometry.cs`:** Rein geometrisches Datenmodell zur Verwaltung der Sammlungen (`Nodes`, `Bars`, `Loads`) mit Hilfsmethoden für das dynamische Hinzufügen/Entfernen.
* **`Transform2D.cs`:** Berechnet den Skalierungsfaktor und Offsets für die Canvas-Abbildung. Auto-Fit wird während des Dragging pausiert, um Bounding-Box-Jittering zu vermeiden.
* **`VectorArrow.cs`:** Analytische Berechnung der Pfeilspitzen mittels Drehmatrix ($L_{\text{tip}} = 14\,\text{px}$, Spreizwinkel $\beta = 15^\circ$).

---

## 5. Ausführen des Projekts

1. Das Projekt in Visual Studio 2022 / JetBrains Rider öffnen.
2. Anwendung starten (`F5` bzw. `dotnet run`).
3. Im Hauptfenster auf den Button **„Track B.2 Sketcher öffnen“** klicken.