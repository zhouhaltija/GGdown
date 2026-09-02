import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sites.twitter import map_transformed, parse_twid, whoami


def test_map_transformed_fields():
    u = map_transformed({
        "id": 44196397, "name": "elonmusk", "nick": "Elon Musk",
        "profile_image": "https://pbs.twimg.com/profile/a.jpg",
    })
    assert u == {
        "rest_id": "44196397",
        "screen_name": "elonmusk",
        "display_name": "Elon Musk",
        "avatar_url": "https://pbs.twimg.com/profile/a.jpg",
    }


def test_parse_twid():
    assert parse_twid("u%3D1647534841515192321") == "1647534841515192321"
    assert parse_twid("u=123") == "123"
    assert parse_twid('"u=123"') == "123"
    assert parse_twid(None) is None
    assert parse_twid("") is None


def test_whoami_uses_twid_and_user_by_rest_id(monkeypatch):
    class Jar:
        def get(self, name, default=None, domain=None, path=None):
            return {"auth_token": "tok", "twid": "u%3D99"}.get(name, default)

    class Ext:
        cookies = Jar()
        cookies_domain = ".x.com"

        def _transform_user(self, raw):
            return {"name": raw["core"]["screen_name"], "nick": raw["core"]["name"]}

    class Api:
        def user_by_rest_id(self, rest_id):
            assert rest_id == "99"
            return {"rest_id": "99", "core": {"screen_name": "alice", "name": "Alice"}}

    monkeypatch.setattr("sites.twitter.make_api", lambda _path: (Api(), Ext()))
    assert whoami("cookies.txt") == {"screen_name": "alice", "display_name": "Alice"}


def test_whoami_missing_auth_token_is_auth_error(monkeypatch):
    class Jar:
        def get(self, name, default=None, domain=None, path=None):
            return None

    class Ext:
        cookies = Jar()
        cookies_domain = ".x.com"

    monkeypatch.setattr("sites.twitter.make_api", lambda _path: (object(), Ext()))
    from sites import AuthError
    try:
        whoami("cookies.txt")
        raise AssertionError("expected AuthError")
    except AuthError as e:
        assert "auth_token" in str(e)
