import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import pytest
from sites import AuthError
from sites.pixiv import map_user, parse_user_id, read_refresh_token, whoami, list_following, user_info


def test_parse_user_id_accepts():
    assert parse_user_id("12345") == "12345"
    assert parse_user_id("https://www.pixiv.net/users/12345") == "12345"
    assert parse_user_id("https://www.pixiv.net/en/users/12345/artworks") == "12345"
    assert parse_user_id("https://www.pixiv.net/member.php?id=12345") == "12345"
    assert parse_user_id("https://touch.pixiv.net/users/99") == "99"


def test_parse_user_id_rejects():
    with pytest.raises(ValueError):
        parse_user_id("")
    with pytest.raises(ValueError):
        parse_user_id("https://x.com/alice")
    with pytest.raises(ValueError):
        parse_user_id("not-a-user")


def test_map_user_fields():
    assert map_user({
        "id": 12345,
        "account": "foo_bar",
        "name": "Foo",
        "profile_image_urls": {"medium": "https://i.pximg.net/a.png"},
    }) == {
        "rest_id": "12345",
        "screen_name": "foo_bar",
        "display_name": "Foo",
        "avatar_url": "https://i.pximg.net/a.png",
    }


def test_read_refresh_token_from_sibling_file(tmp_path):
    cookies = tmp_path / "cookies.txt"
    cookies.write_text("# Netscape\n")
    assert read_refresh_token(str(cookies)) is None
    (tmp_path / "refresh-token.txt").write_text("  secret-token \n")
    assert read_refresh_token(str(cookies)) == "secret-token"


def test_whoami_missing_refresh_token(tmp_path):
    cookies = tmp_path / "cookies.txt"
    cookies.write_text("# Netscape\n")
    with pytest.raises(AuthError, match="refresh-token"):
        whoami(str(cookies))


def test_whoami_missing_phpsessid(monkeypatch, tmp_path):
    cookies = tmp_path / "cookies.txt"
    cookies.write_text("# Netscape\n")
    (tmp_path / "refresh-token.txt").write_text("tok")

    class Jar:
        def get(self, name, default=None, domain=None, path=None):
            return None

    class Ext:
        cookies = Jar()
        cookies_domain = ".pixiv.net"

    monkeypatch.setattr("sites.pixiv.make_api", lambda _p: (object(), Ext()))
    with pytest.raises(AuthError, match="PHPSESSID"):
        whoami(str(cookies))


def test_whoami_uses_oauth_user(monkeypatch, tmp_path):
    cookies = tmp_path / "cookies.txt"
    cookies.write_text("# Netscape\n")
    (tmp_path / "refresh-token.txt").write_text("tok")

    class Jar:
        def get(self, name, default=None, domain=None, path=None):
            return "sess" if name == "PHPSESSID" else default

    class Ext:
        cookies = Jar()
        cookies_domain = ".pixiv.net"

    class Api:
        user = {"id": 12345, "account": "foo", "name": "Foo"}

        def login(self):
            pass

    monkeypatch.setattr("sites.pixiv.make_api", lambda _p: (Api(), Ext()))
    assert whoami(str(cookies)) == {
        "rest_id": "12345", "screen_name": "foo", "display_name": "Foo",
    }


def test_list_following_public_and_private(monkeypatch):
    class Api:
        def user_following(self, user_id, restrict="public"):
            assert user_id == "12345"
            if restrict == "public":
                yield {"user": {"id": 1, "account": "a", "name": "A",
                                "profile_image_urls": {"medium": "https://i/a.png"}}}
            else:
                yield {"user": {"id": 2, "account": "b", "name": "B",
                                "profile_image_urls": {}}}

    monkeypatch.setattr("sites.pixiv.make_api", lambda _p: (Api(), object()))
    users = list(list_following("cookies.txt", {"rest_id": "12345", "screen_name": "me"}))
    assert [u["screen_name"] for u in users] == ["a", "b"]
    assert users[0]["rest_id"] == "1"


def test_user_info_by_id(monkeypatch):
    class Api:
        def user_detail(self, user_id, fatal=True):
            assert user_id == "99"
            return {"user": {"id": 99, "account": "carol", "name": "Carol",
                             "profile_image_urls": {"medium": "https://i/c.png"}}}

    monkeypatch.setattr("sites.pixiv.make_api", lambda _p: (Api(), object()))
    assert user_info("cookies.txt", "https://www.pixiv.net/users/99") == {
        "rest_id": "99",
        "screen_name": "carol",
        "display_name": "Carol",
        "avatar_url": "https://i/c.png",
    }
