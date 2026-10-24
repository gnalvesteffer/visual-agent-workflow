"""Validate repository documentation links and optionally render Mermaid locally.

Install tools/documentation with npm ci and the existing Playwright browser
requirements for --mermaid. No document is sent to an external renderer.
"""
import argparse
from pathlib import Path
import re
import sys
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parent.parent


def check_links():
    files = [ROOT / "README.md", ROOT / "AGENTS.md", *sorted((ROOT / "docs").rglob("*.md"))]
    files += sorted((ROOT / ".agents/skills").glob("*/SOURCE.md"))
    files += [ROOT / ".agents/skills/documentation-maintenance/SKILL.md"]
    diagrams = []
    failures = []
    for path in files:
        source = path.read_text()
        # Examples inside code fences are not links to repository files.
        prose = re.sub(r"^(`{3,}|~{3,})[^\n]*\n.*?^\1\s*$", "", source, flags=re.M | re.S)
        for target in re.findall(r"\]\((<?[^)]+>?)\)", prose):
            target = target.strip().split(' "', 1)[0].strip("<>")
            if re.match(r"[a-zA-Z][a-zA-Z0-9+.-]*:", target) or target.startswith("#"):
                continue
            file_part = unquote(target.split("#", 1)[0])
            destination = ROOT / file_part.lstrip("/") if file_part.startswith("/") else path.parent / file_part
            if not destination.exists():
                failures.append(f"{path.relative_to(ROOT)}: missing local target {target}")
        if path == ROOT / "README.md" or (ROOT / "docs") in path.parents:
            for match in re.finditer(r"^```mermaid\s*\n(.*?)^```\s*$", source, flags=re.M | re.S):
                line = source[:match.start()].count("\n") + 1
                diagrams.append((f"{path.relative_to(ROOT)}:{line}", match.group(1)))
    for required in ["docs/README.md", "docs/architecture.md", "docs/user-guide.md"]:
        if not (ROOT / required).is_file(): failures.append(f"Required documentation missing: {required}")
    if failures:
        raise ValueError("\n".join(failures))
    print(f"Local links passed in {len(files)} documentation/guidance files; found {len(diagrams)} Mermaid diagrams.")
    return diagrams


def check_mermaid(diagrams, executable):
    from playwright.sync_api import sync_playwright
    library = ROOT / "tools/documentation/node_modules/mermaid/dist/mermaid.min.js"
    if not library.exists(): raise ValueError("Install the pinned renderer: npm ci --prefix tools/documentation")
    with sync_playwright() as p:
        options = {"headless": True}
        if executable: options["executable_path"] = executable
        browser = p.chromium.launch(**options)
        try:
            page = browser.new_page()
            page.add_script_tag(path=str(library))
            page.evaluate("mermaid.initialize({startOnLoad:false,securityLevel:'strict',theme:'neutral'})")
            for index, (name, source) in enumerate(diagrams):
                try:
                    result = page.evaluate("""async ({source,id}) => {
                        await mermaid.parse(source);
                        const {svg} = await mermaid.render(id,source);
                        return svg.includes('<svg');
                    }""", {"source":source,"id":f"documentation_diagram_{index}"})
                    if not result: raise ValueError("Renderer returned no SVG")
                except Exception as error:
                    raise ValueError(f"Mermaid validation/rendering failed at {name}: {error}") from error
            print(f"All {len(diagrams)} Mermaid diagrams parsed and rendered locally.")
        finally:
            browser.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mermaid", action="store_true")
    parser.add_argument("--browser-executable")
    args = parser.parse_args()
    try:
        diagrams = check_links()
        if args.mermaid: check_mermaid(diagrams, args.browser_executable)
    except ValueError as error:
        print(error, file=sys.stderr)
        sys.exit(1)
