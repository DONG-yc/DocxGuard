"""Run against a published CLI: python demo-release.py <DocxGuard.Cli.dll> <new-directory>."""
from hashlib import sha256
from io import BytesIO
import json
import os
from pathlib import Path
import subprocess
import sys
from zipfile import ZipFile

from docx import Document
from PIL import Image, ImageDraw


def chart(values):
    canvas = Image.new("RGB", (480, 180), "white")
    draw = ImageDraw.Draw(canvas)
    for i, (name, count) in enumerate(values):
        y = 12 + i * 50
        draw.text((12, y + 8), name, fill="black")
        draw.rectangle((90, y, 90 + count * 7, y + 28), fill="#306a85")
        draw.text((100 + count * 7, y + 8), str(count), fill="black")
    result = BytesIO()
    canvas.save(result, "PNG")
    result.seek(0)
    return result


def run(dll, dest):
    dest.mkdir(parents=True, exist_ok=False)
    values = [("North", 18), ("West", 26)]
    doc = Document()
    doc.add_heading("Station sample counts", 0)
    table = doc.add_table(rows=0, cols=2)
    table.style = "Table Grid"
    for name, value in [("Station", "Samples"), *values]:
        left, right = table.add_row().cells
        left.text, right.text = name, str(value)
    doc.add_picture(chart(values))
    doc.add_paragraph("Figure 1. Observations by station")
    source = dest / "template.docx"
    doc.save(source)
    figure = dest / "updated.png"
    figure.write_bytes(chart(values + [("South", 42)]).read())
    env = dict(os.environ, DOCXGUARD_PYTHON=sys.executable)

    def cli(*args):
        command = [os.environ.get("DOCXGUARD_DOTNET", "dotnet"), str(dll.resolve()), "table-figure", *args]
        return json.loads(subprocess.check_output(command, env=env))

    catalog = cli("list", source)
    table, image = catalog["items"]
    request = dest / "request.json"
    request.write_text(json.dumps(dict(sourceSha256=catalog["sourceSha256"], table=table["table"],
                                       imageRelationship=image["imageRelationship"], rows=[["South", "42"]])), encoding="utf-8")
    args = (source, request, figure)
    preview = cli("preview", *args)["buildReport"]
    result = dest / "result"
    published = cli("apply", *args, preview["outputSha256"], result)
    output = result / "output.docx"
    assert published["status"] == "published"
    assert json.loads((result / "build-report.json").read_bytes()) == preview
    assert sha256(output.read_bytes()).hexdigest() == preview["outputSha256"]
    assert sha256(source.read_bytes()).hexdigest() == catalog["sourceSha256"]
    assert [c.text for c in Document(output).tables[0].rows[-1].cells] == ["South", "42"]
    with ZipFile(output) as out:
        assert out.read("word/media/image1.png") == figure.read_bytes()
    print(f"Verified DOCX and build report: {result.resolve()}")


if __name__ == "__main__":
    run(*map(Path, sys.argv[1:]))
