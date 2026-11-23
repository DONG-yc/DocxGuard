"""Prepare or check a two-case blind evaluation of natural-language target selection.

Use a new directory for every run. Give each evaluator only its assigned workspace,
the request in docs/evaluation.md and the MCP transport. Review clarification text separately.
"""
import argparse
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path

from docx import Document
from PIL import Image


def png(color):
    data = BytesIO()
    Image.new("RGB", (120, 80), color).save(data, format="PNG")
    data.seek(0)
    return data


def prepare(root):
    root.mkdir(parents=True, exist_ok=False)
    for name in ("atlas", "delta"):
        dest = root / name
        dest.mkdir()
        doc = Document()
        sections = [
            ("物资盘点", ["品类", "结余"], "库存余额", "red"),
            ("站点采样汇总", ["站点", "样本数"], "各站采样量（春季）", "green"),
            ("运送记录", ["批次", "目的地"], "各站采样量（秋季）" if name == "delta" else "配送里程", "yellow"),
        ]
        for title, header, caption, color in sections:
            doc.add_paragraph("表 " + title)
            table = doc.add_table(rows=2, cols=2)
            for i, label in enumerate(header):
                table.cell(0, i).text = label
                table.cell(1, i).text = ["例", "2"][i]
            doc.add_paragraph("统计插图")
            doc.add_picture(png(color))
            doc.add_paragraph("图 " + caption)
        doc.save(dest / "summary.docx")
        (dest / "new.png").write_bytes(png("blue").read())


def check(root, before=None):
    for case in ("atlas", "delta"):
        workspace = root / case
        assert {p.name for p in workspace.iterdir()} == {"summary.docx", "new.png"}
        log = (root / (case + ".jsonl")).read_text(encoding="utf-8").splitlines()
        followup = []
        if case == "delta" and before is not None:
            first_turn = before.read_text(encoding="utf-8").splitlines()
            assert first_turn and log[:len(first_turn)] == first_turn, "First-turn log prefix differs"
            followup = [json.loads(line) for line in log[len(first_turn):]]
            log = first_turn
        calls = [json.loads(line) for line in log]
        actions = [e for e in calls if e["query"]["method"] == "tools/call"]
        names = [e["query"]["params"]["name"] for e in actions]
        assert "docxguard_table_figure_list" in names
        catalogs = [e["result"]["structuredContent"] for e in actions
                    if e["query"]["params"]["name"] == "docxguard_table_figure_list"
                    and not e["result"].get("isError")]
        digest = sha256((workspace / "summary.docx").read_bytes()).hexdigest()
        assert catalogs and all(page["sourceSha256"] == digest for page in catalogs)
        assert all(n in {"docxguard_inspect", "docxguard_table_figure_list", "docxguard_table_figure_preview"}
                   for n in names)
        previews = [e for e in actions if e["query"]["params"]["name"] == "docxguard_table_figure_preview"]
        if case == "atlas":
            assert previews and names.index("docxguard_table_figure_list") < names.index("docxguard_table_figure_preview")
            preview = previews[-1]
            assert not preview["result"].get("isError")
            args = preview["query"]["params"]["arguments"]
            assert args["figurePath"] == "new.png" and args["sourcePath"] == "summary.docx"
            report = preview["result"]["structuredContent"]["buildReport"]
            assert report["sourceSha256"] == sha256((workspace / "summary.docx").read_bytes()).hexdigest()
            assert report["table"] == 2 and report["rows"] == [["南站", "42"]]
            assert set(report["changed"]) == {"word/document.xml", "word/media/image2.png"}
        else:
            assert not previews
            captions = {item["after"] for page in catalogs for item in page["items"] if item["kind"] == "image"}
            assert {"图 各站采样量（春季）", "图 各站采样量（秋季）"} <= captions
        print(case, " -> ".join(names))
        if case == "delta" and before is not None:
            next_actions = [e for e in followup if e["query"]["method"] == "tools/call"]
            permitted = {"docxguard_inspect", "docxguard_table_figure_list", "docxguard_table_figure_preview"}
            assert all(e["query"]["params"]["name"] in permitted for e in next_actions)
            attempts = [e for e in next_actions if e["query"]["params"]["name"] == "docxguard_table_figure_preview"]
            assert attempts, "Missing preview after clarification"
            autumn = {item["imageRelationship"] for page in catalogs for item in page["items"]
                      if item["kind"] == "image" and item["after"] == "图 各站采样量（秋季）"}
            assert len(autumn) == 1
            for attempt in attempts:
                assert not attempt["result"].get("isError")
                args = attempt["query"]["params"]["arguments"]
                assert (args["sourcePath"], args["figurePath"]) == ("summary.docx", "new.png")
                result = attempt["result"]["structuredContent"]
                report = result["buildReport"]
                assert result["status"] == "previewed" and report["sourceSha256"] == digest
                assert report["table"] == 2 and report["rows"] == [["南站", "42"]]
                assert report["imageRelationship"] in autumn
                assert set(report["changed"]) == {"word/document.xml", "word/media/image3.png"}
            print("delta follow-up: autumn figure and original row previewed together")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["prepare", "check"])
    parser.add_argument("directory", type=Path)
    parser.add_argument("--before", type=Path, help="Snapshot of delta.jsonl immediately before clarification")
    args = parser.parse_args()
    if args.action == "prepare":
        prepare(args.directory)
    else:
        check(args.directory, args.before)
