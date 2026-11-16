using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VectorTrussCanvas.Geometry;
using VectorTrussCanvas.Models;

namespace VectorTrussCanvas;

public partial class BridgeSketcherWindow : Window
{
    private TrussGeometry _sketchTruss = new();
    private readonly Transform2D _transform = new();
    private TrussNode? _draggedNode = null;
    private TrussNode? _selectedSourceNode = null; // Für Klick-zu-Klick Stabverbindung
    private HashSet<TrussNode> _selectedNodes = new();

    public BridgeSketcherWindow()
    {
        InitializeComponent();
        // Start mit zwei Standardknoten und einem Stab
        _sketchTruss.AddNode("K1", 0.0, 0.0, isFixed: true);
        _sketchTruss.AddNode("K2", 4.0, 0.0, isRoller: true);
        _sketchTruss.AddBar(1, 2);
    }

    private void Redraw()
    {
        if (SketchCanvas == null || SketchCanvas.ActualWidth < 10 || SketchCanvas.ActualHeight < 10)
            return;

        SketchCanvas.Children.Clear();

        if (_draggedNode == null)
        {
            var (xMin, xMax, yMin, yMax) = _sketchTruss.GetBounds();
            _transform.MarginX = 70.0;
            _transform.MarginY = 70.0;
            _transform.Update(xMin - 2, xMax + 2, yMin - 2, yMax + 2, SketchCanvas.ActualWidth, SketchCanvas.ActualHeight);
        }

        // 1. Stäbe zeichnen & Stablängen-Label am Mittelpunkt anzeigen
        foreach (var bar in _sketchTruss.Bars)
        {
            var nodeA = _sketchTruss.Nodes.FirstOrDefault(n => n.Id == bar.NodeAId);
            var nodeB = _sketchTruss.Nodes.FirstOrDefault(n => n.Id == bar.NodeBId);
            if (nodeA == null || nodeB == null) continue;

            Point pA = _transform.WorldToScreen(nodeA.X, nodeA.Y);
            Point pB = _transform.WorldToScreen(nodeB.X, nodeB.Y);

            var line = new Line
            {
                X1 = pA.X,
                Y1 = pA.Y,
                X2 = pB.X,
                Y2 = pB.Y,
                Stroke = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                StrokeThickness = 3.0
            };
            SketchCanvas.Children.Add(line);

            // Euklidische Stablänge berechnen [m]
            double lengthM = Math.Sqrt(Math.Pow(nodeB.X - nodeA.X, 2) + Math.Pow(nodeB.Y - nodeA.Y, 2));
            Point pMid = new Point((pA.X + pB.X) / 2.0, (pA.Y + pB.Y) / 2.0);

            var barLabel = new TextBlock
            {
                Text = $"{lengthM:F2} m",
                Foreground = Brushes.Yellow,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromArgb(160, 20, 20, 20))
            };
            barLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(barLabel, pMid.X - barLabel.DesiredSize.Width / 2.0);
            Canvas.SetTop(barLabel, pMid.Y - barLabel.DesiredSize.Height / 2.0);
            SketchCanvas.Children.Add(barLabel);
        }

        // 2. Auflager zeichnen
        foreach (var node in _sketchTruss.Nodes)
        {
            Point pos = _transform.WorldToScreen(node.X, node.Y);
            if (node.IsFixedSupport) DrawSupport(pos, isFixed: true);
            else if (node.IsRollerSupport) DrawSupport(pos, isFixed: false);
        }

        // 3. Externe Lastpfeile zeichnen
        foreach (var load in _sketchTruss.Loads)
        {
            var node = _sketchTruss.Nodes.FirstOrDefault(n => n.Id == load.NodeId);
            if (node == null) continue;

            Point pos = _transform.WorldToScreen(node.X, node.Y);
            VectorArrow.AddForceArrow(SketchCanvas, pos, load.Fx, load.Fy, lengthPx: 50.0);
        }

        // 4. Knoten zeichnen (mit Farbcodierung für Mehrfachauswahl & Stabverbindung)
        foreach (var node in _sketchTruss.Nodes)
        {
            Point pos = _transform.WorldToScreen(node.X, node.Y);
            double radius = 7.0;

            Brush fillBrush;
            if (_selectedNodes.Contains(node))
                fillBrush = Brushes.OrangeRed;
            else if (node == _selectedSourceNode)
                fillBrush = Brushes.Yellow;
            else
                fillBrush = new SolidColorBrush(Color.FromRgb(0, 160, 255));

            var ellipse = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = fillBrush,
                Stroke = Brushes.White,
                StrokeThickness = _selectedNodes.Contains(node) ? 2.5 : 1.5
            };
            Canvas.SetLeft(ellipse, pos.X - radius);
            Canvas.SetTop(ellipse, pos.Y - radius);
            SketchCanvas.Children.Add(ellipse);

            var label = new TextBlock
            {
                Text = $"{node.Name} ({node.X:F1}, {node.Y:F1})",
                Foreground = _selectedNodes.Contains(node) ? Brushes.Orange : Brushes.LightGreen,
                FontSize = 10,
                FontWeight = _selectedNodes.Contains(node) ? FontWeights.Bold : FontWeights.Normal
            };
            Canvas.SetLeft(label, pos.X + 10);
            Canvas.SetTop(label, pos.Y - 14);
            SketchCanvas.Children.Add(label);
        }

        // 5. DIN 406 Bemaßungsketten (Gesamtlänge & Gesamthöhe), falls Knoten vorhanden sind
        if (_sketchTruss.Nodes.Count > 0)
        {
            var leftNode = _sketchTruss.Nodes.OrderBy(n => n.X).First();
            var rightNode = _sketchTruss.Nodes.OrderByDescending(n => n.X).First();
            Point pLeft = _transform.WorldToScreen(leftNode.X, 0.0);
            Point pRight = _transform.WorldToScreen(rightNode.X, 0.0);
            double dimY = Math.Max(pLeft.Y, pRight.Y) + 35.0;
            double totalLength = rightNode.X - leftNode.X;

            VectorArrow.AddHorizontalDimensionLine(
                SketchCanvas, pLeft, pRight, dimY,
                $"{totalLength:F2} m");

            var bottomNode = _sketchTruss.Nodes.OrderBy(n => n.Y).First();
            var topNode = _sketchTruss.Nodes.OrderByDescending(n => n.Y).First();
            Point pBottom = _transform.WorldToScreen(leftNode.X, bottomNode.Y);
            Point pTop = _transform.WorldToScreen(leftNode.X, topNode.Y);
            double dimX = pLeft.X - 35.0;
            double totalHeight = topNode.Y - bottomNode.Y;

            VectorArrow.AddVerticalDimensionLine(
                SketchCanvas, pBottom, pTop, dimX,
                $"{totalHeight:F2} m");
        }
    }

    private void DrawSupport(Point pos, bool isFixed)
    {
        double h = 16.0, w = 12.0;
        var triangle = new Polygon
        {
            Points = new PointCollection { pos, new Point(pos.X - w / 2, pos.Y + h), new Point(pos.X + w / 2, pos.Y + h) },
            Stroke = Brushes.Orange,
            Fill = new SolidColorBrush(Color.FromArgb(80, 255, 165, 0)),
            StrokeThickness = 1.5
        };
        SketchCanvas.Children.Add(triangle);

        if (!isFixed)
        {
            var rollerLine = new Line
            {
                X1 = pos.X - w / 2 - 3,
                Y1 = pos.Y + h + 3,
                X2 = pos.X + w / 2 + 3,
                Y2 = pos.Y + h + 3,
                Stroke = Brushes.Orange,
                StrokeThickness = 1.5
            };
            SketchCanvas.Children.Add(rollerLine);
        }
    }

    private void SketchCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Point mousePos = e.GetPosition(SketchCanvas);
        var (worldX, worldY) = _transform.ScreenToWorld(mousePos.X, mousePos.Y);

        var clickedNode = _sketchTruss.Nodes.FirstOrDefault(n =>
        {
            Point pScreen = _transform.WorldToScreen(n.X, n.Y);
            return (mousePos - pScreen).Length <= 20.0;
        });

        // 1. RECHTSKLICK: Lager umschalten
        if (e.RightButton == MouseButtonState.Pressed && clickedNode != null)
        {
            if (!clickedNode.IsFixedSupport && !clickedNode.IsRollerSupport)
            {
                clickedNode.IsFixedSupport = true;
                TxtSketchInfo.Text = $"Knoten {clickedNode.Name}: Festlager zugewiesen.";
            }
            else if (clickedNode.IsFixedSupport)
            {
                clickedNode.IsFixedSupport = false;
                clickedNode.IsRollerSupport = true;
                TxtSketchInfo.Text = $"Knoten {clickedNode.Name}: Loselager zugewiesen.";
            }
            else
            {
                clickedNode.IsRollerSupport = false;
                TxtSketchInfo.Text = $"Knoten {clickedNode.Name}: Lager entfernt.";
            }
            Redraw();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            // 2. ALT + KLICK: Knoten zur Mehrfachauswahl hinzufügen / entfernen
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            {
                if (clickedNode != null)
                {
                    if (_selectedNodes.Contains(clickedNode))
                    {
                        _selectedNodes.Remove(clickedNode);
                        TxtSketchInfo.Text = $"Knoten {clickedNode.Name} aus Auswahl entfernt.";
                    }
                    else
                    {
                        _selectedNodes.Add(clickedNode);
                        TxtSketchInfo.Text = $"Knoten {clickedNode.Name} zur Auswahl hinzugefügt ({_selectedNodes.Count} gesamt).";
                    }
                    Redraw();
                }
                return;
            }

            // 3. STRG + KLICK: Last auf Knoten aufbringen / entfernen
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                if (clickedNode != null)
                {
                    var existingLoad = _sketchTruss.Loads.FirstOrDefault(l => l.NodeId == clickedNode.Id);
                    if (existingLoad == null)
                    {
                        _sketchTruss.Loads.Add(new TrussLoad(clickedNode.Id, 0.0, -25.0));
                        TxtSketchInfo.Text = $"Last (-25 kN) auf Knoten {clickedNode.Name} aufgebracht.";
                    }
                    else
                    {
                        _sketchTruss.Loads.Remove(existingLoad);
                        TxtSketchInfo.Text = $"Last von Knoten {clickedNode.Name} entfernt.";
                    }
                    Redraw();
                }
                return;
            }

            // 4. SHIFT + KLICK: Stabverbindung
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                if (clickedNode != null)
                {
                    if (_selectedSourceNode == null)
                    {
                        _selectedSourceNode = clickedNode;
                        TxtSketchInfo.Text = $"Startknoten {clickedNode.Name} gewählt. Klicke Zielknoten zum Verbinden an.";
                        Redraw();
                    }
                    else
                    {
                        if (_selectedSourceNode.Id != clickedNode.Id)
                        {
                            _sketchTruss.AddBar(_selectedSourceNode.Id, clickedNode.Id);
                            TxtSketchInfo.Text = $"Stab zwischen {_selectedSourceNode.Name} und {clickedNode.Name} erstellt.";
                        }
                        _selectedSourceNode = null;
                        Redraw();
                    }
                }
                return;
            }

            // 5. NORMALER KLICK AUF KNOTEN: Knoten verschieben (Drag & Drop)
            if (clickedNode != null)
            {
                _draggedNode = clickedNode;
                _selectedSourceNode = null;
                SketchCanvas.CaptureMouse();
                TxtSketchInfo.Text = $"Ziehe Knoten {_draggedNode.Name}...";
                Redraw();
            }
            else
            {
                // 6. KLICK INS LEERE: Auswahl aufheben & neuen Knoten erstellen
                _selectedSourceNode = null;
                _selectedNodes.Clear(); // Auswahl bei Klick ins Leere aufheben

                double finalX = worldX;
                double finalY = worldY;

                if (ChkGridSnap.IsChecked == true)
                {
                    finalX = Math.Round(worldX * 2.0) / 2.0;
                    finalY = Math.Round(worldY * 2.0) / 2.0;
                }

                string name = $"K{_sketchTruss.Nodes.Count + 1}";
                _sketchTruss.AddNode(name, Math.Round(finalX, 3), Math.Round(finalY, 3));
                TxtSketchInfo.Text = $"Knoten {name} bei ({finalX:F1}, {finalY:F1}) m erstellt.";
                Redraw();
            }
        }
    }

    private void SketchCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        // Live-Verschieben des Knotens während der Mausbewegung
        if (_draggedNode != null)
        {
            Point mousePos = e.GetPosition(SketchCanvas);
            var (worldX, worldY) = _transform.ScreenToWorld(mousePos.X, mousePos.Y);

            if (ChkGridSnap.IsChecked == true)
            {
                _draggedNode.X = Math.Round(Math.Round(worldX * 2.0) / 2.0, 3);
                _draggedNode.Y = Math.Round(Math.Round(worldY * 2.0) / 2.0, 3);
            }
            else
            {
                _draggedNode.X = Math.Round(worldX, 3);
                _draggedNode.Y = Math.Round(worldY, 3);
            }

            TxtSketchInfo.Text = $"Knoten {_draggedNode.Name}: X = {_draggedNode.X:F2} m | Y = {_draggedNode.Y:F2} m";
            Redraw();
        }
    }

    private void SketchCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Beenden des Drag & Drop
        if (_draggedNode != null)
        {
            _draggedNode = null;
            SketchCanvas.ReleaseMouseCapture();
            TxtSketchInfo.Text = "Knoten platziert.";
            Redraw(); // Auto-Fit/BoundingBox wird nach dem Loslassen aktualisiert
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // ENTF-Taste löscht entweder alle per Alt-Klick ausgewählten Knoten oder den letzten Knoten
        if (e.Key == Key.Delete)
        {
            if (_selectedNodes.Count > 0)
            {
                foreach (var node in _selectedNodes.ToList())
                {
                    _sketchTruss.Nodes.Remove(node);
                    _sketchTruss.Bars.RemoveAll(b => b.NodeAId == node.Id || b.NodeBId == node.Id);
                    _sketchTruss.Loads.RemoveAll(l => l.NodeId == node.Id);
                }
                int deletedCount = _selectedNodes.Count;
                _selectedNodes.Clear();
                ReindexNodes();
                TxtSketchInfo.Text = $"{deletedCount} ausgewählte Knoten und deren Stäbe gelöscht.";
                Redraw();
            }
            else if (_sketchTruss.Nodes.Count > 0)
            {
                var last = _sketchTruss.Nodes.Last();
                _sketchTruss.Nodes.Remove(last);
                _sketchTruss.Bars.RemoveAll(b => b.NodeAId == last.Id || b.NodeBId == last.Id);
                _sketchTruss.Loads.RemoveAll(l => l.NodeId == last.Id);
                ReindexNodes();
                TxtSketchInfo.Text = $"Letzter Knoten ({last.Name}) gelöscht.";
                Redraw();
            }
        }
    }

    private void SketchCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(_sketchTruss, options);

        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON Files (*.json)|*.json", FileName = "bridge_blueprint.json" };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, json);
            TxtSketchInfo.Text = "Erfolgreich als JSON exportiert!";
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _sketchTruss.Nodes.Clear();
        _sketchTruss.Bars.Clear();
        _sketchTruss.Loads.Clear();
        _selectedSourceNode = null;
        Redraw();
        TxtSketchInfo.Text = "Zeichnung zurückgesetzt.";
    }
    private void ReindexNodes()
    {
        for (int i = 0; i < _sketchTruss.Nodes.Count; i++)
        {
            _sketchTruss.Nodes[i].Name = $"K{i + 1}";
        }
    }
}