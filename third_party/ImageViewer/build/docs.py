#!/usr/bin/env python3
"""把 docs/usage.md 生成为 docs/usage.html，并检查手册覆盖度。

设计约定（改手册前先读这段）：

* 单一源是 ``docs/usage.md``，HTML 永远由它生成，不要手改 ``docs/usage.html``。
* 版式在 ``docs/usage.template.html``，脚本只填 ``{{TOC}}`` 与 ``{{CONTENT}}``。
* 支持的 Markdown 子集（够用即可，不引入第三方依赖）：
  ``#`` 标题、``##`` 章节、``###`` 小节、段落、``-`` / ``1.`` 列表、
  管道表格、围栏代码块、``>`` 提示块、行内 ``**加粗**`` / ``` `代码` ``` / ``[文字](链接)``；
  另外支持两种指令：
  ``<a id="anchor"></a>`` 给下一个章节或小节指定固定锚点；移动或重命名标题时保留它，
  ``<!-- steps -->`` 让紧随其后的有序列表变成带序号圆点的时间线样式。
* ``--check`` 模式不写文件：确认 HTML 与 md 同步，并检查 31 个 ROI 工具、
  18 个高级测量项和顶层菜单分组都在手册里出现过。CI 跑这个模式，防止文档再漂移。

用法::

    python build/docs.py            # 生成 docs/usage.html
    python build/docs.py --check    # 校验同步 + 覆盖度
    python build/docs.py --offline  # 校验并打包离线手册
"""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
from hashlib import sha256
from html.parser import HTMLParser
from pathlib import Path
from tempfile import NamedTemporaryFile
from urllib.parse import unquote, urlsplit
from xml.etree import ElementTree
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
MD_PATH = DOCS / "usage.md"
HTML_PATH = DOCS / "usage.html"
TEMPLATE_PATH = DOCS / "usage.template.html"
PDF_STAMP_PATH = DOCS / "usage.pdf.source.json"
OFFLINE_ENTRY_PATH = ROOT / "build" / "docs-offline-start.html"
OFFLINE_ZIP_PATH = ROOT / "artifacts" / "docs" / "imageviewer-manual.zip"
UITEXT_PATH = ROOT / "ImageViewerControl" / "Resources" / "UiText.resx"
PLUGIN_DIR = ROOT / "ImageViewerControl" / "Plugins"

MENU_GROUPS = [
    "MenuFileAndProjects",
    "MenuDraw",
    "MenuMeasure",
    "MenuRecipe",
    "MenuViewControl",
    "MenuImageDisplay",
    "MenuAnalysisAndExport",
    "MenuPerformance",
]


class DocError(RuntimeError):
    """手写内容或仓库结构不满足生成前提时抛出。"""


# --------------------------------------------------------------------------- 行内


MODIFIERS = ("Ctrl", "Shift", "Alt")
# 单独出现也明确是"按一个键"的名字；数字键不在此列，因为正文里的 `0`、`1` 多是数值。
SINGLE_KEYS = {
    "Ctrl", "Shift", "Alt",
    "Esc", "Enter", "Delete", "Home", "End", "Tab", "PgUp", "PgDn",
    "↑", "↓", "←", "→",
}


def is_shortcut(text: str) -> bool:
    """判断行内代码是不是按键：`Ctrl+S`、`Ctrl+Shift+R`、`Ctrl++`、`↑`。

    Chinese: 只把确实表示按键的写法转成 <kbd>；`Ctrl+滚轮`、`px`、`.ivsession`
    这类含中文或属于数值的内容继续用 <code>，避免整篇都变成键帽。
    """
    if text in SINGLE_KEYS:
        return True
    if not any(text.startswith(modifier + "+") for modifier in MODIFIERS):
        return False
    # `Ctrl++` 表示"Ctrl 加加号"：去掉末尾的 "+" 后只剩修饰键本身。
    if text.endswith("++"):
        return text[:-2] in MODIFIERS

    body = text
    parts = body.split("+")
    if len(parts) < 2:
        return False
    return all(
        part and len(part) <= 12 and part.isascii() and " " not in part for part in parts
    )


def render_key(text: str) -> str:
    if text.endswith("++"):
        body, extra = text[:-2], ["+"]
    else:
        body, extra = text, []
    keys = body.split("+") + extra
    return "+".join(f"<kbd>{html.escape(key, quote=False)}</kbd>" for key in keys)


def render_inline(text: str) -> str:
    """渲染行内语法。先切出行内代码，避免代码里的星号被当成加粗。"""
    out: list[str] = []
    for part in re.split(r"(`[^`]+`)", text):
        if len(part) > 2 and part.startswith("`") and part.endswith("`"):
            content = part[1:-1]
            if is_shortcut(content):
                out.append(render_key(content))
            else:
                out.append("<code>" + html.escape(content, quote=False) + "</code>")
            continue
        escaped = html.escape(part, quote=False)
        escaped = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", escaped)
        escaped = re.sub(
            r"\[([^\]]+)\]\(([^)\s]+)\)", r'<a href="\2">\1</a>', escaped
        )
        out.append(escaped)
    return "".join(out)


# --------------------------------------------------------------------------- 块


def split_cells(row: str) -> list[str]:
    row = row.strip()
    if row.startswith("|"):
        row = row[1:]
    if row.endswith("|"):
        row = row[:-1]
    return [cell.strip() for cell in row.split("|")]


def is_separator(row: str) -> bool:
    return bool(re.fullmatch(r"\|?[\s:|-]+\|?", row.strip())) and "-" in row


def render_table(rows: list[str], section_title: str, indent: str) -> str:
    header = split_cells(rows[0])
    body = [split_cells(row) for row in rows[2:]]
    wide = ' class="wide"' if len(header) >= 5 else ""
    lines = [
        f'{indent}<div class="table-scroll" tabindex="0" role="region" aria-label="{html.escape(section_title.rstrip("：:"), quote=True)}：表格">',
        f"{indent}<table{wide}>",
        f"{indent}<thead>",
        f"{indent}<tr>",
    ]
    for cell in header:
        lines.append(f'{indent}<th scope="col">{render_inline(cell)}</th>')
    lines += [f"{indent}</tr>", f"{indent}</thead>", f"{indent}<tbody>"]
    for row in body:
        if not any(cell for cell in row):
            continue
        lines.append(f"{indent}<tr>")
        for cell in row:
            lines.append(f"{indent}<td>{render_inline(cell)}</td>")
        lines.append(f"{indent}</tr>")
    lines += [f"{indent}</tbody>", f"{indent}</table>", f"{indent}</div>"]
    return "\n".join(lines)


def render_list(items: list[tuple[bool, str]], ordered_class: str, indent: str) -> str:
    tag = "ol" if all(ordered for ordered, _ in items) else "ul"
    open_tag = f'<ol class="{ordered_class}">' if tag == "ol" and ordered_class else f"<{tag}>"
    lines = [f"{indent}{open_tag}"]
    for _, text in items:
        lines.append(f"{indent}<li>{render_inline(text)}</li>")
    lines.append(f"{indent}</{tag}>")
    return "\n".join(lines)


def convert(md_text: str) -> tuple[str, str, list[tuple[str, str, list[tuple[str, str]]]]]:
    """返回 (标题, 正文 HTML, 目录项)。"""
    lines = md_text.splitlines()
    title = ""
    body: list[str] = []
    toc: list[tuple[str, str, list[tuple[str, str]]]] = []
    pending_id: str | None = None
    pending_steps = False
    section_open = False
    section_title = "手册"
    index = 0

    def close_section() -> None:
        nonlocal section_open
        if section_open:
            body.append("    </section>")
            section_open = False

    def consume_anchor(heading: str) -> str:
        nonlocal pending_id
        if pending_id is None:
            raise DocError(f"标题「{heading}」缺少固定锚点，请在前一行指定 <a id=\"anchor\"></a>")
        anchor_id, pending_id = pending_id, None
        return anchor_id

    while index < len(lines):
        line = lines[index]
        stripped = line.strip()

        if not stripped:
            index += 1
            continue

        if pending_id is not None and not stripped.startswith(("## ", "### ")):
            raise DocError(f"锚点 #{pending_id} 后应紧跟章节或小节标题")

        if stripped.startswith("```"):
            language = stripped[3:].strip()
            index += 1
            code: list[str] = []
            while index < len(lines) and not lines[index].strip().startswith("```"):
                code.append(lines[index])
                index += 1
            index += 1
            class_attr = f' class="language-{html.escape(language, quote=True)}"' if language else ""
            lang_attr = f' data-lang="{html.escape(language, quote=True)}"' if language else ""
            body.append(
                f"      <pre{lang_attr}><code"
                + class_attr
                + ">"
                + html.escape("\n".join(code), quote=False)
                + "</code></pre>"
            )
            continue

        anchor = re.fullmatch(r'<a id="([a-z][a-z0-9-]*)"></a>', stripped)
        if anchor:
            pending_id = anchor.group(1)
            index += 1
            continue

        if re.fullmatch(r"<!--\s*steps\s*-->", stripped):
            pending_steps = True
            index += 1
            continue

        if stripped.startswith("# "):
            title = stripped[2:].strip()
            index += 1
            continue

        if stripped.startswith("## "):
            close_section()
            heading = stripped[3:].strip()
            anchor_id = consume_anchor(heading)
            section_title = heading
            toc.append((anchor_id, heading, []))
            body.append(f'    <section id="{anchor_id}">')
            body.append(
                f'      <h2><a class="anchor" href="#{anchor_id}" aria-label="链接到本节">#</a>'
                f"{render_inline(heading)}</h2>"
            )
            section_open = True
            index += 1
            continue

        if stripped.startswith("### "):
            heading = stripped[4:].strip()
            sub_id = consume_anchor(heading)
            if toc:
                toc[-1][2].append((sub_id, heading))
            body.append(f'      <h3 id="{sub_id}">{render_inline(heading)}</h3>')
            index += 1
            continue

        image = re.fullmatch(r"!\[([^\]]*)\]\(([^)\s]+)\)", stripped)
        if image:
            alt, src = image.group(1), image.group(2)
            body.append("        <figure>")
            body.append(
                f'          <img src="{html.escape(src, quote=True)}" '
                f'alt="{html.escape(alt, quote=True)}" loading="lazy">'
            )
            if alt:
                body.append(f"          <figcaption>{render_inline(alt)}</figcaption>")
            body.append("        </figure>")
            index += 1
            continue

        if stripped.startswith("|"):
            rows: list[str] = []
            while index < len(lines) and lines[index].strip().startswith("|"):
                rows.append(lines[index])
                index += 1
            if len(rows) < 2 or not is_separator(rows[1]):
                raise DocError(f"表格缺少分隔行：{rows[0].strip()}")
            body.append(render_table(rows, section_title, "        "))
            continue

        if stripped.startswith("> "):
            quoted: list[str] = []
            while index < len(lines) and lines[index].strip().startswith(">"):
                quoted.append(render_inline(lines[index].strip().lstrip(">").strip()))
                index += 1
            lines_html = "\n".join(
                f'          <span class="meta-line">{text}</span>' for text in quoted
            )
            body.append('        <div class="meta">\n' + lines_html + "\n        </div>")
            continue

        list_match = re.match(r"^(\d+\.|[-*])\s+(.*)$", stripped)
        if list_match:
            items: list[tuple[bool, str]] = []
            while index < len(lines):
                current = lines[index].strip()
                item = re.match(r"^(\d+\.|[-*])\s+(.*)$", current)
                if not item:
                    break
                items.append((item.group(1).endswith("."), item.group(2)))
                index += 1
            ordered_class = "step-list" if pending_steps and items[0][0] else ""
            pending_steps = False
            body.append(render_list(items, ordered_class, "        "))
            continue

        paragraph: list[str] = []
        while index < len(lines):
            current = lines[index].strip()
            if (
                not current
                or current.startswith("#")
                or current.startswith("|")
                or current.startswith(">")
                or current.startswith("```")
                or re.match(r"^(\d+\.|[-*])\s+", current)
                or current.startswith("![")
                or re.fullmatch(r'<a id="[^"]+"></a>', current)
                or re.fullmatch(r"<!--\s*steps\s*-->", current)
            ):
                break
            paragraph.append(current)
            index += 1
        body.append("        <p>" + " ".join(render_inline(text) for text in paragraph) + "</p>")

    if pending_id is not None:
        raise DocError(f"锚点 #{pending_id} 后应紧跟章节或小节标题")
    close_section()
    return title, "\n".join(body), toc


# --------------------------------------------------------------------------- 组装


def build_html() -> str:
    md_text = MD_PATH.read_text(encoding="utf-8")
    template = TEMPLATE_PATH.read_text(encoding="utf-8")
    title, content, toc = convert(md_text)

    template_title = re.search(r"<h1>(.*?)</h1>", template)
    if not template_title:
        raise DocError("模板里找不到 <h1>，无法核对标题")
    if template_title.group(1).strip() != title:
        raise DocError(
            f"md 标题“{title}”与模板 h1“{template_title.group(1).strip()}”不一致"
        )

    toc_blocks: list[str] = []
    for number, (anchor, text, subs) in enumerate(toc, start=1):
        block = [
            '      <div class="toc-group">',
            f'        <a href="#{anchor}"><span class="toc-num">{number:02d}</span>{render_inline(text)}</a>',
        ]
        if subs:
            block.append('        <ul class="toc-sub">')
            for sub_anchor, sub_text in subs:
                block.append(
                    f'          <li><a href="#{sub_anchor}">{render_inline(sub_text)}</a></li>'
                )
            block.append("        </ul>")
        block.append("      </div>")
        toc_blocks.append("\n".join(block))
    toc_html = "\n".join(toc_blocks)
    # 文末再给一份"全部章节"索引：滚到底部想跳回某节时，不用先滚回顶部或用浏览器后退。
    index_html = ""
    if toc:
        index_items = "\n".join(
            f'          <li><a href="#{anchor}">{render_inline(text)}</a></li>'
            for anchor, text, _ in toc
        )
        index_html = (
            '\n      <nav class="chapter-index" aria-label="全部章节">\n'
            '        <p class="chapter-index-title">全部章节</p>\n'
            f"        <ol>\n{index_items}\n        </ol>\n"
            "      </nav>"
        )
    return (
        template.replace("<!--{{TOC}}-->", toc_html)
        .replace("<!--{{CONTENT}}-->", content + index_html)
        .replace("\r\n", "\n")
    )


# --------------------------------------------------------------------------- 覆盖度


def load_strings() -> dict[str, str]:
    tree = ElementTree.parse(UITEXT_PATH)
    strings: dict[str, str] = {}
    for node in tree.getroot().findall("data"):
        name = node.get("name")
        value = node.find("value")
        if name and value is not None and value.text:
            strings[name] = value.text.strip()
    return strings


def collect_tools() -> list[tuple[str, bool]]:
    """从插件目录读出 (显示名资源键, 是否测量工具)。"""
    tools: list[tuple[str, bool]] = []
    for path in sorted(PLUGIN_DIR.glob("RoiPluginRegistry.*Catalog.cs")):
        for line in path.read_text(encoding="utf-8").splitlines():
            match = re.search(r'new RoiToolDescriptor\(UiText\.Get\("(\w+)"\)', line)
            if match:
                tools.append((match.group(1), "isMeasurement: true" in line))
    return tools


def collect_advanced() -> list[str]:
    kinds: list[str] = []
    for path in ROOT.glob("ImageViewerControl/**/*.cs"):
        text = path.read_text(encoding="utf-8")
        match = re.search(r"enum AdvancedMeasurementKind\s*\{(.*?)\}", text, re.S)
        if match:
            for name in match.group(1).split(","):
                name = name.strip()
                if name and not name.startswith("//"):
                    kinds.append(name)
            break
    return kinds


def collect_quality_fields() -> list[str]:
    """质量门限的可配置字段，用于核对手册是否解释了每个阈值。"""
    path = ROOT / "ImageViewer.Core" / "Analysis" / "ImageAnalysisQualityProfile.cs"
    text = path.read_text(encoding="utf-8")
    return re.findall(r"public\s+[\w<>?\[\]]+\s+(\w+)\s*\{\s*get;", text)


def check_coverage(md_text: str, strings: dict[str, str]) -> list[str]:
    problems: list[str] = []
    tools = collect_tools()
    drawing = [key for key, measurement in tools if not measurement]
    measuring = [key for key, measurement in tools if measurement]
    if (len(drawing), len(measuring)) != (10, 21):
        problems.append(
            f"工具目录数量变了：绘制 {len(drawing)} 个、测量 {len(measuring)} 个（手册按 10/21 描述）"
        )
    for key in drawing + measuring:
        name = strings.get(key)
        if not name:
            problems.append(f"UiText 缺少 {key} 的文案")
            continue
        if name not in md_text:
            problems.append(f"手册没写到工具「{name}」（{key}）")

    for kind in collect_advanced():
        key = "AdvancedMeasurementOperation" + kind
        name = strings.get(key)
        if name and name not in md_text:
            problems.append(f"手册没写到高级测量「{name}」（{key}）")

    for key in MENU_GROUPS:
        name = strings.get(key)
        if name and name not in md_text:
            problems.append(f"手册没写到菜单分组「{name}」（{key}）")

    # 错误标题、标定字段、质量门限：这三类最容易"代码加了、手册没跟上"。
    # 资源里改了文案或新增一条，这里就会失败，逼着人回来补表。
    for key, value in sorted(strings.items()):
        if key.startswith("Error") and key.endswith("Title") and value not in md_text:
            problems.append(f"手册没写到错误消息「{value}」（{key}）")
        if key.startswith("CalibrationDialog") and key.endswith("Label") and value not in md_text:
            problems.append(f"手册没写到标定字段「{value}」（{key}）")

    for field in collect_quality_fields():
        if field not in md_text:
            problems.append(f"手册没写到质量门限字段「{field}」")

    for target in re.findall(r"\]\((?!https?://)([^)#]+)", md_text):
        if not (DOCS / target).exists():
            problems.append(f"链接指向的文件不存在：docs/{target}")
    return problems


class AnchorCheck(HTMLParser):
    """检查生成后实际出现在页面中的 ID 与链接，包括模板本身。"""

    def __init__(self) -> None:
        super().__init__()
        self.ids: set[str] = set()
        self.duplicates: set[str] = set()
        self.targets: set[str] = set()
        self.references: set[str] = set()
        self.image_sources: set[str] = set()

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        for name, value in attrs:
            if not value:
                continue
            if name == "id":
                if value in self.ids:
                    self.duplicates.add(value)
                self.ids.add(value)
            if tag == "a" and name == "href" and value.startswith("#") and len(value) > 1:
                self.targets.add(value[1:])
            if (tag, name) in {("a", "href"), ("img", "src")}:
                self.references.add(value)
            if tag == "img" and name == "src":
                self.image_sources.add(value)

    def problems(self) -> list[str]:
        return [
            *(f"页面内重复的锚点 #{anchor}" for anchor in sorted(self.duplicates)),
            *(f"页面内链接指向不存在的锚点 #{anchor}" for anchor in sorted(self.targets - self.ids)),
        ]


# --------------------------------------------------------------------------- 离线交付


def pdf_source_hash(files: dict[str, bytes] | None = None) -> str:
    """指纹包括生成 PDF 的 HTML 和其中直接引用的本地图片。"""
    def read(name: str) -> bytes:
        return files[name] if files is not None else (ROOT / name).read_bytes()

    html_name = HTML_PATH.relative_to(ROOT).as_posix()
    html_bytes = read(html_name).replace(b"\r\n", b"\n")
    page = AnchorCheck()
    page.feed(html_bytes.decode("utf-8"))
    inputs = {html_name: sha256(html_bytes).hexdigest()}
    for reference in page.image_sources:
        url = urlsplit(reference)
        if url.scheme == "data":
            continue
        if url.scheme or url.netloc or not url.path:
            raise DocError(f"PDF 手册需要本地图片：{reference}")
        image = (HTML_PATH.parent / unquote(url.path)).resolve()
        if not image.is_relative_to(DOCS):
            raise DocError(f"PDF 手册图片须位于 docs 目录：{reference}")
        name = image.relative_to(ROOT).as_posix()
        inputs[name] = sha256(read(name)).hexdigest()
    return sha256(json.dumps(inputs, sort_keys=True).encode("utf-8")).hexdigest()


def verify_manual_pdf(files: dict[str, bytes]) -> None:
    pdf = files["docs/usage.pdf"]
    try:
        stamp = json.loads(PDF_STAMP_PATH.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise DocError("PDF 缺少有效的来源记录，请运行 build/docs-pdf.ps1 重新生成") from error

    expected = {"sourceSha256": pdf_source_hash(files), "pdfSha256": sha256(pdf).hexdigest()}
    if not pdf.startswith(b"%PDF-") or stamp != expected:
        raise DocError("PDF 与当前手册或来源记录不一致，请运行 build/docs-pdf.ps1 重新生成")


def collect_offline_manual() -> dict[str, bytes]:
    """沿手册的相对链接收集文档和图片，不导出其它仓库文件。"""
    files = {
        "START.html": OFFLINE_ENTRY_PATH.read_bytes(),
        "LICENSE": (ROOT / "LICENSE").read_bytes(),
    }
    pending = [Path("docs/usage.html"), Path("docs/usage.md"), Path("docs/usage.pdf"), Path("CHANGELOG.md")]
    allowed_extensions = {".html", ".md", ".pdf", ".png", ".jpg", ".jpeg", ".svg", ".webp"}
    while pending:
        relative = pending.pop()
        name = relative.as_posix()
        if name in files:
            continue
        source = ROOT / relative
        if not source.is_file():
            raise DocError(f"离线包所需文件不存在：{name}")
        data = source.read_bytes()
        if not data:
            raise DocError(f"离线包所需文件为空：{name}")
        files[name] = data
        if relative.suffix == ".html":
            page = AnchorCheck()
            page.feed(data.decode("utf-8"))
            references = page.references
            for image in page.image_sources:
                url = urlsplit(image)
                if (url.scheme or url.netloc) and url.scheme != "data":
                    raise DocError(f"离线包需要本地图片：{name} → {image}")
        elif relative.suffix == ".md":
            text = re.sub(r"(?ms)^\s*```[^\n]*\n.*?^\s*```[^\n]*$", "", data.decode("utf-8"))
            references = re.findall(r"\]\(([^)\s]+)\)", text)
        else:
            continue
        for reference in references:
            url = urlsplit(reference)
            if url.scheme == "file":
                raise DocError(f"离线包不能使用绝对文件地址：{name} → {reference}")
            if url.scheme or url.netloc or not url.path:
                continue
            target = (source.parent / unquote(url.path)).resolve()
            if target in {ROOT / "CHANGELOG.md", ROOT / "LICENSE"}:
                pending.append(target.relative_to(ROOT))
            elif target.is_relative_to(DOCS) and target.suffix.lower() in allowed_extensions and target != TEMPLATE_PATH:
                pending.append(target.relative_to(ROOT))
            else:
                raise DocError(f"离线包无法携带此本地链接：{name} → {reference}")

    verify_manual_pdf(files)

    sample_folder = ROOT / "ImageViewerDemo" / "Resources"
    session = (sample_folder / "MeasurementSample.ivsession").read_bytes()
    if json.loads(session).get("ImagePath") != "TestImage.png":
        raise DocError("测量示例的底图引用已变化，请更新离线包清单")
    files["examples/measurement/MeasurementSample.ivsession"] = session
    files["examples/measurement/TestImage.png"] = (sample_folder / "TestImage.png").read_bytes()

    start = AnchorCheck()
    start.feed(files["START.html"].decode("utf-8"))
    for reference in start.references:
        target = urlsplit(reference).path
        if target and unquote(target) not in files:
            raise DocError(f"离线入口引用了未打包的文件：{reference}")
    return files


def build_offline_zip() -> None:
    files = collect_offline_manual()
    OFFLINE_ZIP_PATH.parent.mkdir(parents=True, exist_ok=True)
    with NamedTemporaryFile(dir=OFFLINE_ZIP_PATH.parent, suffix=".zip", delete=False) as stream:
        staged = Path(stream.name)
    try:
        with ZipFile(staged, "w") as archive:
            for name, data in sorted(files.items()):
                entry = ZipInfo(name)
                entry.create_system = 3
                entry.external_attr = 0o100644 << 16
                archive.writestr(entry, data, compress_type=ZIP_DEFLATED, compresslevel=9)
        staged.replace(OFFLINE_ZIP_PATH)
    finally:
        staged.unlink(missing_ok=True)
    print(f"已生成 {OFFLINE_ZIP_PATH.relative_to(ROOT)}（{len(files)} 个文件）")


# --------------------------------------------------------------------------- 入口


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="生成、检查或打包 ImageViewer 使用手册")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="检查手册是否同步")
    mode.add_argument("--offline", action="store_true", help="检查手册并生成离线 ZIP")
    mode.add_argument("--pdf-source-hash", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    if args.pdf_source_hash:
        try:
            print(pdf_source_hash())
        except (DocError, OSError, ValueError) as error:
            print(f"无法读取 PDF 手册来源：{error}", file=sys.stderr)
            return 1
        return 0

    check_only = args.check or args.offline
    try:
        html_text = build_html()
    except DocError as error:
        print(f"手册检查未通过：{error}", file=sys.stderr)
        return 1
    md_text = MD_PATH.read_text(encoding="utf-8")
    anchors = AnchorCheck()
    anchors.feed(html_text)
    problems = check_coverage(md_text, load_strings()) + anchors.problems()

    if check_only:
        current = HTML_PATH.read_text(encoding="utf-8").replace("\r\n", "\n")
        if current != html_text:
            problems.append("docs/usage.html 与 usage.md 不同步，运行 python build/docs.py 重新生成")
    if problems:
        print("手册检查未通过：", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    if not check_only:
        HTML_PATH.write_text(html_text, encoding="utf-8", newline="\n")
        print(f"已生成 {HTML_PATH.relative_to(ROOT)}（{len(html_text)} 字符）")
    print("手册检查通过：HTML 与 md 同步，工具、菜单和内部链接检查通过")
    if args.offline:
        try:
            build_offline_zip()
        except (DocError, OSError, ValueError) as error:
            print(f"离线包生成失败：{error}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
