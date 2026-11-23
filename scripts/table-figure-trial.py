"""Exercise a bounded table-and-PNG edit against a local DocxGuard release build.

Usage: python scripts/table-figure-trial.py source.docx request.json figure.png new.docx
Request fields: sourceSha256, table (1-based body table), rows (arrays of strings),
imageRelationship (main document relationship id). Requires lxml and Pillow.
"""
import argparse
from copy import deepcopy
from io import BytesIO
import json
import os
from pathlib import Path
import posixpath
import shutil
import subprocess
import tempfile
from zipfile import ZipFile

from lxml import etree as ET
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
A = "{http://schemas.openxmlformats.org/drawingml/2006/main}"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def inspect(path):
    local = ROOT / ".dotnet" / "dotnet.exe"
    host = os.environ.get("DOCXGUARD_INSPECT_COMMAND")
    command = json.loads(host) if host else [str(local) if local.exists() else "dotnet",
               str(ROOT / "src/DocxGuard.Cli/bin/Release/net10.0/DocxGuard.Cli.dll")]
    command += ["inspect", "--json", "--limit", "1", "--", str(path)]
    return json.loads(subprocess.check_output(command, stdin=subprocess.DEVNULL, timeout=120))


def append_rows(doc, table, rows):
    tables = doc.findall(f"{W}body/{W}tbl")
    require(type(table) is int and 1 <= table <= len(tables), "Unknown body table")
    target = tables[table - 1]
    template = deepcopy(target[-1])
    for node in template.iter(W + "tr", W + "p"):
        for identifier in ("paraId", "textId"):
            node.attrib.pop("{http://schemas.microsoft.com/office/word/2010/wordml}" + identifier, None)
    require(template.tag == W + "tr", "Expected a trailing data row")
    require(isinstance(rows, list) and 1 <= len(rows) <= 64, "Expected 1–64 new rows")
    # Only copy ordinary text cells and formatting. Reject fields, merged cells,
    # identifiers, revision markup and drawings that cannot safely be duplicated.
    props = {W + tag for tag in ("tblPrEx", "trPr", "tcPr", "pPr", "rPr")}
    allowed = {W + tag for tag in ("tr", "tc", "p", "r", "t")} | props
    for element in template.iter():
        require(element.tag in allowed or any(p.tag in props for p in element.iterancestors()),
                "Template contains non-text content")
        name = ET.QName(element)
        require(name.namespace == W[1:-1] and not name.localname.endswith("Change")
                and name.localname not in {"gridSpan", "vMerge", "hMerge", "tblHeader", "numPr", "sectPr"},
                "Unsupported row formatting")
        require(all(ET.QName(attr).localname.lower() not in {"id", "paraid", "textid"}
                    for attr in element.attrib), "Template contains identifiers")
    for values in rows:
        row = deepcopy(template)
        cells = row.findall(W + "tc")
        require(isinstance(values, list) and len(values) == len(cells) > 0, "Wrong column count")
        for cell, value in zip(cells, values):
            texts = cell.findall(f"{W}p/{W}r/{W}t")
            require(len(texts) == 1 and len(cell.findall(W + "p")) == 1, "Expected a plain cell")
            require(isinstance(value, str) and len(value) <= 4096 and not any(ord(c) < 32 for c in value),
                    "Expected a plain single-line cell value")
            texts[0].text = value
            texts[0].set("{http://www.w3.org/XML/1998/namespace}space", "preserve")
        target.append(row)


def image_part(archive, main, doc, rid):
    require(sum(value == rid for node in doc.iter() for value in node.attrib.values()) == 1
            and any(node.get(f"{{{R}}}embed") == rid for node in doc.iter(A + "blip")),
            "Expected one embedded DrawingML picture")
    base, name = posixpath.split(main)
    relfile = posixpath.join(base, "_rels", name + ".rels")
    rels = ET.fromstring(archive.read(relfile))
    matches = [r for r in rels if r.get("Id") == rid and r.get("Type") == R + "/image"]
    require(len(matches) == 1, "Expected one image relationship")
    def resolve(folder, target):
        require(not any(c in target for c in "%?#\\"), "Unsupported relationship URI")
        return posixpath.normpath(posixpath.join(folder, target)).lstrip("/")
    part = resolve(base, matches[0].get("Target"))
    count = 0
    for path in archive.namelist():
        if path.endswith(".rels"):
            folder = posixpath.dirname(posixpath.dirname(path))
            for rel in ET.fromstring(archive.read(path)):
                count += resolve(folder, rel.get("Target")).casefold() == part.casefold()
    require(count == 1, "Image is shared by multiple relationships")
    return part


def png_size(data):
    with Image.open(BytesIO(data)) as picture:
        require(picture.format == "PNG" and picture.width * picture.height <= 32_000_000,
                "Expected a bounded PNG image")
        picture.load()
        return picture.size


def run(source, request, figure, output):
    require(output.suffix.lower() == ".docx" and not output.exists(), "Expected a new .docx path")
    with tempfile.TemporaryDirectory(dir=output.parent) as temp:
        snapshot = Path(temp) / "source.docx"
        candidate = Path(temp) / "result.docx"
        shutil.copyfile(source, snapshot)
        before = inspect(snapshot)
        require(before["sourcePackageSha256"] == request["sourceSha256"], "Stale source")
        main = before["summary"]["mainDocumentPartName"].lstrip("/")
        require(figure.stat().st_size <= 32_000_000, "Image too large")
        replacement = figure.read_bytes()
        with ZipFile(snapshot) as original:
            doc = ET.fromstring(original.read(main))
            append_rows(doc, request["table"], request["rows"])
            part = image_part(original, main, doc, request["imageRelationship"])
            require(png_size(original.read(part)) == png_size(replacement), "Expected matching PNG dimensions")
            changes = {main: ET.tostring(doc, encoding="UTF-8", xml_declaration=True), part: replacement}
            with ZipFile(candidate, "w") as result:
                for entry in original.infolist():
                    data = changes[entry.filename] if entry.filename in changes else original.read(entry.filename)
                    result.writestr(deepcopy(entry), data)
            after = inspect(candidate)
            with ZipFile(candidate) as result:
                require(result.namelist() == original.namelist(), "Entry list changed")
                changed = {p for p in result.namelist() if result.read(p) != original.read(p)}
                require(changed == set(changes), "Unexpected package delta")
            cells = sum(map(len, request["rows"]))
            require(after["summary"]["cellCount"] == before["summary"]["cellCount"] + cells,
                    "Unexpected cell count")
        # Atomic create without replacing an existing destination.
        os.link(candidate, output)
        return {"sourceSha256": before["sourcePackageSha256"], "table": request["table"],
                "imageRelationship": request["imageRelationship"], "rows": request["rows"],
                "rowsAdded": len(request["rows"]), "cellsAdded": cells, "changed": sorted(changed),
                "outputSha256": after["sourcePackageSha256"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ("source", "request", "figure", "output"):
        parser.add_argument(arg, type=Path)
    args = parser.parse_args()
    print(json.dumps(run(args.source, json.loads(args.request.read_text(encoding="utf-8-sig")),
                         args.figure, args.output), indent=2))
