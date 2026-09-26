import sys
import asyncio
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "third_party" / "TikTokDownloader"))

from sites import AuthError
from sites.douyin import (
    DouyinClient, _download_with_client, fetch_current_user, map_user, media_files, parse_target,
    read_netscape_cookies, require_authenticated_user,
)


class FakeResponse:
    def __init__(self, payload):
        self.payload = payload

    def raise_for_status(self):
        pass

    def json(self):
        return self.payload

    @property
    def headers(self):
        return {"Content-Range": "bytes 0-0/10000"}

    @property
    def url(self):
        return "https://media.example/original.mp4"


class FakeSession:
    def __init__(self, payload):
        self.payload = payload
        self.calls = []

    async def get(self, url, **kwargs):
        self.calls.append((url, kwargs))
        return FakeResponse(self.payload)


def test_fetch_current_user_requires_identity_and_sends_cookie():
    session = FakeSession({"status_code": 0, "user": {"sec_uid": "MS4wLjABtest", "nickname": "示例作者"}})
    user = asyncio.run(fetch_current_user(session, "sessionid=fictional"))
    assert user["rest_id"] == "MS4wLjABtest"
    assert session.calls[0][1]["headers"]["Cookie"] == "sessionid=fictional"
    assert "user/profile/self" in session.calls[0][0]


def test_client_extracts_gallery_using_third_party_extractor():
    session = FakeSession({})
    client = DouyinClient(session, "sessionid=fictional")
    raw = {
        "aweme_id": "1234567890123456789",
        "create_time": 1700000000,
        "desc": "图集示例",
        "images": [{"url_list": ["https://img.example/one.jpg"]}],
    }
    media = asyncio.run(client.extract_media(raw, original_quality=False))
    assert media["id"] == "1234567890123456789"
    assert media["downloads"] == ["https://img.example/one.jpg"]


def test_client_quality_follows_third_party_video_selection():
    session = FakeSession({})
    client = DouyinClient(session, "sessionid=fictional")
    raw = {
        "aweme_id": "1234567890123456789", "create_time": 1700000000,
        "video": {
            "play_addr": {"uri": "v123"},
            "bit_rate": [
                {"FPS": 60, "bit_rate": 900, "play_addr": {"data_size": 900, "height": 720, "width": 1280, "url_list": ["https://media.example/low.mp4"]}},
                {"FPS": 30, "bit_rate": 800, "play_addr": {"data_size": 800, "height": 1080, "width": 1920, "url_list": ["https://media.example/high.mp4"]}},
            ],
        },
    }
    normal = asyncio.run(client.extract_media(raw, original_quality=False))
    assert normal["downloads"] == "https://media.example/high.mp4"
    original = asyncio.run(client.extract_media(raw, original_quality=True))
    assert original["downloads"] == "https://media.example/original.mp4"


def test_media_stream_does_not_send_login_cookie_to_media_host(tmp_path):
    class FakeStream:
        async def __aenter__(self):
            return self

        async def __aexit__(self, *args):
            pass

        def raise_for_status(self):
            pass

        async def aiter_content(self):
            yield b"media"

    class StreamingSession:
        def stream(self, method, url, **kwargs):
            assert method == "GET"
            assert url == "https://media.example/file.mp4"
            assert "Cookie" not in kwargs["headers"]
            return FakeStream()

    target = tmp_path / "video.part"
    asyncio.run(DouyinClient(StreamingSession(), "sessionid=fictional").stream_file(
        "https://media.example/file.mp4", target
    ))
    assert target.read_bytes() == b"media"


def test_read_netscape_cookies_keeps_only_live_douyin_values(tmp_path):
    path = tmp_path / "cookies.txt"
    path.write_text(
        "# Netscape HTTP Cookie File\n"
        ".douyin.com\tTRUE\t/\tTRUE\t4102444800\tsessionid\tfictional-session\n"
        "www.douyin.com\tFALSE\t/\tTRUE\t4102444800\tUIFID\tfictional-uifid\n"
        ".douyin.com\tTRUE\t/\tTRUE\t1\told\texpired\n"
        ".evil.example\tTRUE\t/\tTRUE\t4102444800\tbad\tsecret\n",
        encoding="utf-8",
    )
    assert read_netscape_cookies(str(path)) == "sessionid=fictional-session; UIFID=fictional-uifid"


def test_read_netscape_cookies_raises_generic_auth_error_on_malformed_file(tmp_path):
    path = tmp_path / "bad.txt"
    path.write_text("this is not a cookie file\n", encoding="utf-8")
    with pytest.raises(AuthError, match="Cookie 文件解析失败"):
        read_netscape_cookies(str(path))


@pytest.mark.parametrize(
    "text,expected",
    [
        ("https://www.douyin.com/user/MS4wLjABtest", ("user", "MS4wLjABtest")),
        ("复制链接 https://www.douyin.com/video/1234567890123456789?foo=1", ("work", "1234567890123456789")),
        ("https://www.douyin.com/note/1234567890123456789", ("work", "1234567890123456789")),
        ("https://www.iesdouyin.com/share/user/MS4wLjABtest", ("user", "MS4wLjABtest")),
        ("https://www.iesdouyin.com/share/video/1234567890123456789", ("work", "1234567890123456789")),
        ("https://www.iesdouyin.com/share/note/1234567890123456789", ("work", "1234567890123456789")),
        ("https://www.iesdouyin.com/share/slides/1234567890123456789", ("work", "1234567890123456789")),
    ],
)
def test_parse_target_accepts_supported_links(text, expected):
    assert parse_target(text) == expected


@pytest.mark.parametrize("text", ["", "https://evil.example/video/1234567890123456789", "https://www.douyin.com/collection/1234567890123456789"])
def test_parse_target_rejects_unsupported_links(text):
    with pytest.raises(ValueError):
        parse_target(text)


def test_map_user_uses_sec_uid_for_stable_identity():
    raw = {
        "sec_uid": "MS4wLjABtest",
        "unique_id": "creator_123",
        "nickname": "示例作者",
        "avatar_larger": {"url_list": ["https://img.example/avatar.jpg"]},
        "follower_count": 42,
        "aweme_count": 7,
    }
    assert map_user(raw) == {
        "rest_id": "MS4wLjABtest",
        "screen_name": "creator_123",
        "display_name": "示例作者",
        "avatar_url": "https://img.example/avatar.jpg",
        "banner_url": None,
        "bio": None,
        "followers_count": 42,
        "media_count": 7,
    }


def test_media_files_maps_video_and_gallery():
    video = {"id": "1234567890123456789", "type": "视频", "downloads": "https://media.example/video.mp4?sig=secret"}
    assert media_files(video) == [{
        "item_id": "1234567890123456789", "index": 1,
        "url": "https://media.example/video.mp4?sig=secret", "suffix": "mp4",
    }]
    gallery = {"id": "1234567890123456789", "type": "图集", "downloads": ["https://img.example/1", "https://img.example/2"]}
    assert [part["index"] for part in media_files(gallery)] == [1, 2]
    assert [part["suffix"] for part in media_files(gallery)] == ["jpg", "jpg"]


def test_whoami_response_needs_authenticated_user():
    with pytest.raises(AuthError):
        require_authenticated_user({"status_code": 8, "status_msg": "用户未登录", "user": None})
    with pytest.raises(ValueError):
        require_authenticated_user({"status_code": 0, "user": {}})
    assert require_authenticated_user({"status_code": 0, "user": {"sec_uid": "MS4wLjABtest"}}) == {"sec_uid": "MS4wLjABtest"}


class FakeDownloadClient:
    def __init__(self, fail=False):
        self.fail = fail
        self.transfers = 0

    async def resolve(self, value):
        return parse_target(value)

    async def detail(self, work_id):
        return {"aweme_id": work_id}

    async def posts(self, identifier):
        return []

    async def extract_media(self, raw, original_quality):
        assert original_quality is True
        return {"id": raw["aweme_id"], "type": "视频", "downloads": "https://media.example/video.mp4?secret=signed"}

    async def stream_file(self, url, path):
        self.transfers += 1
        path.write_bytes(b"abc")
        if self.fail:
            raise OSError("transfer interrupted")


def test_download_emits_file_events_and_archive_skip(tmp_path):
    client = FakeDownloadClient()
    events = []
    spec = {
        "urls": ["https://www.douyin.com/video/1234567890123456789"],
        "options": {
            "base-directory": str(tmp_path / "downloads"),
            "douyin": {"original_quality": True, "archive": str(tmp_path / "archive.txt")},
        },
    }
    asyncio.run(_download_with_client(spec, client, lambda ev, **data: events.append((ev, data))))
    target = tmp_path / "downloads" / "douyin" / "_links" / "1234567890123456789_1.mp4"
    assert target.read_bytes() == b"abc"
    assert [name for name, _ in events] == ["url-start", "file-start", "file-done", "job-done"]
    assert events[2][1]["size"] == 3
    assert "secret=signed" not in str(events)

    events.clear()
    asyncio.run(_download_with_client(spec, client, lambda ev, **data: events.append((ev, data))))
    assert client.transfers == 1
    assert [name for name, _ in events] == ["url-start", "file-skip", "job-done"]
    assert events[-1][1] == {"total": 1, "skipped": 1, "failed": 0}


def test_failed_transfer_cleans_partial_file_and_counts_failure(tmp_path):
    client = FakeDownloadClient(fail=True)
    events = []
    spec = {
        "urls": ["https://www.douyin.com/video/1234567890123456789"],
        "options": {
            "base-directory": str(tmp_path / "downloads"),
            "douyin": {"original_quality": True, "archive": str(tmp_path / "archive.txt")},
        },
    }
    asyncio.run(_download_with_client(spec, client, lambda ev, **data: events.append((ev, data))))
    assert [name for name, _ in events] == ["url-start", "file-start", "log", "job-done"]
    assert events[-1][1]["failed"] == 1
    assert not list((tmp_path / "downloads").rglob("*.part"))
    assert not list((tmp_path / "downloads").rglob("*.mp4"))


def test_cancelled_transfer_cleans_partial_file(tmp_path):
    class CancellingClient(FakeDownloadClient):
        async def stream_file(self, url, path):
            path.write_bytes(b"partial")
            raise asyncio.CancelledError()

    spec = {
        "urls": ["https://www.douyin.com/video/1234567890123456789"],
        "options": {"base-directory": str(tmp_path / "downloads"), "douyin": {"original_quality": True}},
    }
    with pytest.raises(asyncio.CancelledError):
        asyncio.run(_download_with_client(spec, CancellingClient(), lambda *args, **kwargs: None))
    assert not list((tmp_path / "downloads").rglob("*.part"))


def test_user_download_requires_non_empty_posts(tmp_path):
    class EmptyPostsClient:
        async def resolve(self, value):
            return parse_target(value)

        async def posts(self, identifier):
            return []

        async def extract_media(self, raw, original_quality):
            raise AssertionError("should not reach extract_media when posts is empty")

    spec = {
        "urls": ["https://www.douyin.com/user/MS4wLjABtest"],
        "options": {"base-directory": str(tmp_path / "downloads"), "douyin": {"original_quality": True}},
    }
    with pytest.raises(ValueError, match="未取得抖音发布作品"):
        asyncio.run(_download_with_client(spec, EmptyPostsClient(), lambda *args, **kwargs: None))


def test_stale_part_file_is_removed_before_next_download(tmp_path):
    part = tmp_path / "downloads" / "douyin" / "_links" / "1234567890123456789_1.mp4.part"
    part.parent.mkdir(parents=True, exist_ok=True)
    part.write_bytes(b"stale")

    client = FakeDownloadClient()
    spec = {
        "urls": ["https://www.douyin.com/video/1234567890123456789"],
        "options": {"base-directory": str(tmp_path / "downloads"), "douyin": {"original_quality": True}},
    }
    asyncio.run(_download_with_client(spec, client, lambda *args, **kwargs: None))
    assert not part.exists()
    assert (tmp_path / "downloads" / "douyin" / "_links" / "1234567890123456789_1.mp4").exists()
