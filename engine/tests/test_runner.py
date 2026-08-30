import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sites import classify_error, parse_screen_name, walk_config
import pytest


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
