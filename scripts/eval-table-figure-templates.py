"""Generate two shareable DOCX fixtures and run discovery through publication.

Requires python-docx, Pillow, lxml and a Release CLI build.
"""
from io import BytesIO
import json
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory

from docx import Document
from PIL import Image


def png(color):
    data = BytesIO()
    Image.new("RGB", (120, 80), color).save(data, format="PNG")
    data.seek(0)
    return data


def section(document, title, color, header):
    document.add_paragraph(f"Table: {title}")
    table = document.add_table(rows=2, cols=len(header))
    table.style = "Table Grid"
    for index, label in enumerate(header):
        table.cell(0, index).text = label
        table.cell(1, index).text = f"Example {index + 1}"
    document.add_paragraph("Current observations")
    document.add_picture(png(color))
    document.add_paragraph(f"Figure: {title}")


def evaluate():
    runner = Path(__file__).with_name("eval-table-figure.py")
    with TemporaryDirectory() as temp:
        folder = Path(temp)
        source, request, figure = (folder / name for name in ("template.docx", "rows.json", "new.png"))
        figure.write_bytes(png("blue").read())
        for inventory_first in (False, True):
            document = Document()
            if inventory_first:
                section(document, "Inventory", "red", ["SKU", "Count"])
            header = ["Site", "Count", "Unit"] if inventory_first else ["Site", "Count"]
            section(document, "Observations", "green", header)
            document.save(source)
            rows = [["North", "42"] + (["birds"] if inventory_first else [])]
            request.write_text(json.dumps({"rows": rows}), encoding="utf-8")
            output = subprocess.check_output([sys.executable, "-X", "utf8", runner, source, request,
                                              figure, "Table: Observations", "Figure: Observations"])
            report = json.loads(output)
            assert report["table"] == (2 if inventory_first else 1)
            assert report["rows"] == rows
            print(f"Passed {len(header)}-column template: table {report['table']}, image {report['imageRelationship']}")


if __name__ == "__main__":
    evaluate()
