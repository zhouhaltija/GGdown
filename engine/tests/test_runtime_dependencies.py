"""验证精简依赖后的真实适配器闭包，避免开发环境中的多余包掩盖缺失依赖。"""
from pathlib import Path
import subprocess
import sys
import tomllib

REPO = Path(__file__).resolve().parents[2]
UNUSED = {
    "lxml", "javascript", "fastapi", "uvicorn", "openpyxl", "pydantic", "pyperclip",
}


def _runtime_closure() -> dict[str, str]:
    """从锁文件的运行时依赖图推导发行包集合，忽略仅开发环境使用的分组。"""
    lock = tomllib.loads((REPO / "engine/uv.lock").read_text(encoding="utf-8"))
    packages = {item["name"].lower(): item for item in lock["package"]}
    pending = [item["name"] for item in packages["ggdown-engine"]["dependencies"]]
    closure: dict[str, str] = {}
    while pending:
        name = pending.pop().lower()
        if name in closure:
            continue
        closure[name] = packages[name]["version"]
        pending.extend(item["name"] for item in packages[name].get("dependencies", []))
    return closure


def _manifest_packages(text: str) -> dict[str, str]:
    """解析 THIRD-PARTY.md 的发行包表；gallery-dl 有独立章节，另行校验。"""
    section = text.split("## Bundled Python packages", 1)[1].split("\n## ", 1)[0]
    declared: dict[str, str] = {}
    for line in section.splitlines():
        if not line.startswith("|") or "Packages (version)" in line:
            continue
        cells = [cell.strip() for cell in line.strip("|").split("|")]
        if len(cells) < 2:
            continue
        for entry in cells[1].split(";"):
            name, _, version = entry.strip().partition(" ")
            if name and version:
                declared[name.lower().replace("_", "-")] = version
    return declared


def test_runtime_dependencies_exclude_unused_application_features():
    config = tomllib.loads((REPO / "engine/pyproject.toml").read_text(encoding="utf-8"))
    roots = {item.split("=", 1)[0].split(">", 1)[0].split("<", 1)[0] for item in config["project"]["dependencies"]}
    assert not roots & UNUSED


def test_adapters_work_without_optional_application_dependencies():
    script = f'''
import importlib.abc
import sys
sys.path[:0] = {str([str(REPO / "engine"), str(REPO / "third_party/TikTokDownloader")])}
blocked = {UNUSED | {"pydantic_core", "annotated_doc", "annotated_types", "anyio", "click", "et_xmlfile", "h11", "starlette", "typing_inspection"}!r}
class BlockOptional(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname.split(".")[0] in blocked:
            raise ModuleNotFoundError("禁止依赖: " + fullname)
sys.meta_path.insert(0, BlockOptional())
from runtime_smoke import verify_runtime
verify_runtime()
'''
    result = subprocess.run([sys.executable, "-I", "-B", "-c", script], capture_output=True, timeout=30)
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")


def test_third_party_manifest_matches_locked_runtime_packages():
    """发行清单必须与锁文件的运行时闭包一致，避免打包内容与许可证说明脱节。"""
    closure = _runtime_closure()
    gallery_dl = closure.pop("gallery-dl")
    manifest = (REPO / "THIRD-PARTY.md").read_text(encoding="utf-8")

    declared = _manifest_packages(manifest)
    assert declared == closure, {
        "未声明": sorted(set(closure.items()) - set(declared.items())),
        "多余声明": sorted(set(declared.items()) - set(closure.items())),
    }
    assert f"Version: {gallery_dl} (pinned" in manifest
