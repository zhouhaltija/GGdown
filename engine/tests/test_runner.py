import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sites import apply_proxy_from_env, classify_error, configure_stdio, emit, load_site, parse_screen_name, walk_config
import pytest
import json


def test_diagnostic_error_log_does_not_count_as_a_failed_file(tmp_path, monkeypatch, capsys):
    import logging
    import runner
    from argparse import Namespace
    from gallery_dl import job

    spec = tmp_path / "job.json"
    spec.write_text(json.dumps({"urls": ["https://example.com/test.jpg"]}), encoding="utf-8")

    def diagnostic_only(self):
        logging.getLogger("gallery-dl").error("诊断信息，不代表某个文件失败")
        return 0

    monkeypatch.setattr(job.DownloadJob, "run", diagnostic_only)
    handlers = list(logging.getLogger().handlers)
    try:
        runner.cmd_download(Namespace(site="twitter", cookies="cookies.txt", job=str(spec)))
    finally:
        logging.getLogger().handlers[:] = handlers
    events = [json.loads(line) for line in capsys.readouterr().out.splitlines()]
    assert events[-1] == {"ev": "job-done", "total": 0, "skipped": 0, "failed": 0}


def test_gallery_file_failure_is_structured_and_not_counted_per_log(tmp_path):
    import subprocess
    import threading
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
    from pathlib import Path

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/123_1.jpg":
                self.send_response(404)
                self.end_headers()
            else:
                self.send_response(200)
                self.send_header("Content-Type", "image/jpeg")
                self.end_headers()
                self.wfile.write(b"image-content")

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    base = f"http://127.0.0.1:{server.server_port}"
    spec = tmp_path / "job.json"
    spec.write_text(json.dumps({
        "urls": [base + "/123_1.jpg", base + "/456_1.jpg"],
        "options": {"base-directory": str(tmp_path / "files"),
                    "extractor": {"filename": "{filename}.{extension}", "generic": {"enabled": True}},
                    "downloader": {"http": {"retries": 0, "validate": False}}},
    }), encoding="utf-8")
    try:
        env = dict(os.environ, ALL_PROXY="", HTTPS_PROXY="", HTTP_PROXY="")
        result = subprocess.run([sys.executable, str(Path(__file__).resolve().parents[1] / "runner.py"),
                                 "download", "--cookies", str(tmp_path / "cookies"), "--job", str(spec)],
                                capture_output=True, text=True, encoding="utf-8", timeout=15, env=env)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)
    assert result.returncode == 0, result.stderr
    events = [json.loads(line) for line in result.stdout.splitlines()]
    failures = [event for event in events if event["ev"] == "file-failed"]
    assert len(failures) == 1, result.stdout + result.stderr
    assert failures[0]["path"].endswith("123_1.jpg")
    assert failures[0]["item_id"] == "123"
    assert failures[0]["kind"] == "download"
    assert failures[0]["msg"]
    assert events[-1] == {"ev": "job-done", "total": 2, "skipped": 0, "failed": 1}


def test_gallery_non_file_failure_is_not_reported_as_success(tmp_path, monkeypatch):
    import runner
    from argparse import Namespace
    from gallery_dl import job

    spec = tmp_path / "job.json"
    spec.write_text(json.dumps({"urls": ["https://example.com/test.jpg"]}), encoding="utf-8")
    monkeypatch.setattr(job.DownloadJob, "run", lambda self: 1)
    with pytest.raises(RuntimeError):
        runner.cmd_download(Namespace(site="twitter", cookies="cookies.txt", job=str(spec)))


def test_load_site_twitter():
    mod = load_site("twitter")
    assert hasattr(mod, "whoami")
    assert hasattr(mod, "list_following")
    assert hasattr(mod, "user_info")


def test_load_site_pixiv():
    mod = load_site("pixiv")
    assert hasattr(mod, "whoami")
    assert hasattr(mod, "list_following")
    assert hasattr(mod, "user_info")


def test_load_site_douyin():
    mod = load_site("douyin")
    assert hasattr(mod, "whoami")
    assert hasattr(mod, "user_info")
    assert hasattr(mod, "download")


def test_douyin_download_routes_to_site_adapter(tmp_path, monkeypatch):
    import runner
    from argparse import Namespace

    job = tmp_path / "job.json"
    job.write_text(json.dumps({"urls": ["https://www.douyin.com/video/1234567890123456789"]}), encoding="utf-8")
    calls = []

    class FakeSite:
        def download(self, cookies, spec, emitter):
            calls.append((cookies, spec))
            emitter("job-done", total=0, skipped=0, failed=0)

    monkeypatch.setattr(runner, "load_site", lambda name: FakeSite() if name == "douyin" else None)
    monkeypatch.setattr(runner, "emit", lambda *args, **kwargs: None)
    runner.cmd_download(Namespace(site="douyin", cookies="fictional-path", job=str(job)))
    assert calls == [("fictional-path", {"urls": ["https://www.douyin.com/video/1234567890123456789"]})]


def test_load_site_unknown_raises():
    with pytest.raises(ValueError, match="unknown site"):
        load_site("nope")


def test_configure_stdio_allows_star_operator():
    configure_stdio()
    enc = (sys.stdout.encoding or "").lower().replace("-", "")
    assert enc in {"utf8", "utf_8"}
    sys.stdout.write("\u22c6")
    sys.stdout.flush()
    emit("account", screen_name="x", display_name="foo\u22c6bar")


def test_apply_proxy_from_env_sets_gallery_dl_config(monkeypatch):
    from gallery_dl import config
    config.clear()
    monkeypatch.setenv("ALL_PROXY", "socks5h://127.0.0.1:1080")
    apply_proxy_from_env()
    assert config.get(("extractor",), "proxy") == "socks5h://127.0.0.1:1080"
    assert config.get(("downloader",), "proxy") == "socks5h://127.0.0.1:1080"
    assert config.get(("downloader", "http"), "proxy") == "socks5h://127.0.0.1:1080"


def test_apply_proxy_from_env_noop_without_env(monkeypatch):
    from gallery_dl import config
    config.clear()
    monkeypatch.delenv("ALL_PROXY", raising=False)
    monkeypatch.delenv("HTTPS_PROXY", raising=False)
    monkeypatch.delenv("HTTP_PROXY", raising=False)
    apply_proxy_from_env()
    assert config.get(("extractor",), "proxy") is None


def test_walk_config_flattens_nested_dicts():
    nested = {
        "extractor": {"twitter": {"videos": True, "filename": "{tweet_id}.jpg"}},
        "base-directory": "D:/x",
    }
    paths = {p: v for p, v in walk_config(nested)}
    assert paths[("extractor", "twitter", "videos")] is True
    assert paths[("extractor", "twitter", "filename")] == "{tweet_id}.jpg"
    assert paths[("base-directory",)] == "D:/x"


@pytest.mark.parametrize("value,expected", [
    ("elonmusk", "elonmusk"),
    ("@elonmusk", "elonmusk"),
    ("https://x.com/elonmusk", "elonmusk"),
    ("https://twitter.com/elonmusk/media", "elonmusk"),
    ("x.com/elonmusk?foo=1", "elonmusk"),
])
def test_parse_screen_name_accepts(value, expected):
    assert parse_screen_name(value) == expected


@pytest.mark.parametrize("value", ["", "https://google.com/x", "!!bad!!", "https://x.com/"])
def test_parse_screen_name_rejects(value):
    with pytest.raises(ValueError):
        parse_screen_name(value)


def test_classify_error_authentication_error_instance():
    # 审查 Important-5 回归覆盖：AuthenticationError 是认证失效专用类型 → "auth"
    from gallery_dl import exception as gdl_exc
    assert classify_error(gdl_exc.AuthenticationError("Invalid login credentials")) == "auth"


def test_classify_error_auth_failure_string():
    # 审查 Important-5 回归覆盖：中途 Cookie 失效的 API 特征串（非 gallery-dl 异常类型）→ "auth"
    assert classify_error(Exception("Could not authenticate you")) == "auth"


def test_classify_error_unrelated_returns_none():
    # AuthorizationError（账号被封/受保护内容）与普通异常不得误映射为 auth
    from gallery_dl import exception as gdl_exc
    assert classify_error(Exception("boom")) is None
    assert classify_error(gdl_exc.AuthorizationError("Insufficient privileges")) is None
