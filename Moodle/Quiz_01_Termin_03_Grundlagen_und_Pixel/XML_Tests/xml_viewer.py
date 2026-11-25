import xml.etree.ElementTree as ET
import glob
import os

xml_files = sorted(glob.glob("*.xml"))

if not xml_files:
    print("Keine XML-Dateien im Ordner gefunden!")
    exit()

# HTML-Gerüst mit Tabs
html = """<!DOCTYPE html>
<html lang="de">
<head>
    <meta charset="UTF-8">
    <title>Moodle Quiz Vorschau</title>
    <script src="https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-mml-chtml.js"></script>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; max-width: 1000px; margin: 20px auto; padding: 0 20px; line-height: 1.5; color: #222; background-color: #f8f9fa; }
        h1 { margin-bottom: 15px; }
        .tab-bar { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 25px; border-bottom: 2px solid #ddd; padding-bottom: 10px; }
        .tab-btn { background: #fff; border: 1px solid #ccc; border-radius: 6px; padding: 8px 14px; cursor: pointer; font-size: 0.9em; font-weight: 500; transition: all 0.2s; }
        .tab-btn:hover { background: #e9ecef; }
        .tab-btn.active { background: #0056b3; color: #fff; border-color: #0056b3; }
        .tab-content { display: none; }
        .tab-content.active { display: block; }
        .question-card { background: #fff; border: 1px solid #e0e0e0; border-radius: 8px; padding: 20px; margin-bottom: 20px; box-shadow: 0 2px 4px rgba(0,0,0,0.04); }
        .q-name { font-weight: bold; color: #0056b3; font-size: 1.05em; margin-bottom: 12px; }
        .answers { margin-top: 15px; padding-left: 20px; }
        .answer { padding: 8px 12px; margin-bottom: 6px; border-radius: 4px; background: #fdfdfd; border: 1px solid #eee; }
        .correct { background-color: #e6f4ea; border-color: #34a853; border-left: 5px solid #34a853; font-weight: 500; }
        .feedback { margin-top: 15px; padding: 12px; background-color: #f1f3f4; border-radius: 6px; font-size: 0.9em; border-left: 4px solid #1a73e8; }
    </style>
</head>
<body>
<h1>Moodle Quiz Übersicht</h1>
<div class="tab-bar">
"""

# Tab-Buttons erzeugen
for i, f in enumerate(xml_files):
    active_cls = " active" if i == 0 else ""
    title = f.replace(".xml", "")
    html += f'    <button class="tab-btn{active_cls}" onclick="openTab(\'tab-{i}\', this)">{title}</button>\n'

html += "</div>\n"

# Inhalte für jeden Tab erzeugen
for i, f in enumerate(xml_files):
    active_cls = " active" if i == 0 else ""
    html += f'<div id="tab-{i}" class="tab-content{active_cls}">\n'
    html += f'    <h2>Datei: {f}</h2>\n'
    
    try:
        tree = ET.parse(f)
        root = tree.getroot()
        count = 0
        for q in root.findall("question"):
            if q.get("type") == "category":
                continue
            count += 1
            name = q.findtext("name/text", default=f"Frage {count}")
            text = q.findtext("questiontext/text", default="")
            feedback = q.findtext("generalfeedback/text", default="")
            
            html += '    <div class="question-card">\n'
            html += f'        <div class="q-name">#{count}: {name}</div>\n'
            html += f'        <div class="q-text">{text}</div>\n'
            
            answers = q.findall("answer")
            if answers:
                html += '        <div class="answers"><strong>Antworten:</strong><ul>\n'
                for a in answers:
                    fraction = float(a.get("fraction", 0))
                    a_text = a.findtext("text", default="")
                    is_correct = " correct" if fraction > 0 else ""
                    tag = " <b>[Richtig]</b>" if fraction > 0 else ""
                    html += f'            <li class="answer{is_correct}">{a_text}{tag}</li>\n'
                html += '        </ul></div>\n'
                
            if feedback:
                html += f'        <div class="feedback"><strong>Erklärung / Feedback:</strong><br>{feedback}</div>\n'
                
            html += '    </div>\n'
    except Exception as e:
        html += f'<p style="color:red;">Fehler beim Laden von {f}: {e}</p>'
        
    html += "</div>\n"

# JavaScript zum Wechseln der Tabs
html += """
<script>
function openTab(tabId, btn) {
    document.querySelectorAll('.tab-content').forEach(el => el.classList.remove('active'));
    document.querySelectorAll('.tab-btn').forEach(el => el.classList.remove('active'));
    document.getElementById(tabId).classList.add('active');
    btn.classList.add('active');
    if (window.MathJax) {
        MathJax.typesetPromise();
    }
}
</script>
</body>
</html>
"""

with open("vorschau.html", "w", encoding="utf-8") as out:
    out.write(html)

print("Vorschau mit Tabs erfolgreich aktualisiert!")