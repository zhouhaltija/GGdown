import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sites import parse_screen_name, walk_config
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
