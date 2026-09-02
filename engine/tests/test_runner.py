import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sites import apply_proxy_from_env, classify_error, configure_stdio, emit, parse_screen_name, walk_config
import pytest


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
