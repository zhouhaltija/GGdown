import sys
import asyncio
import json
from datetime import datetime, timedelta, timezone
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
    user = asyncio.run(fetch_current_user(session, "sessionid=fictional; UIFID=sample-uifid"))
    assert user["rest_id"] == "MS4wLjABtest"
    assert session.calls[0][1]["headers"]["Cookie"] == "sessionid=fictional; UIFID=sample-uifid"
    assert session.calls[0][1]["headers"]["uifid"] == "sample-uifid"
    assert "user/profile/self" in session.calls[0][0]
    assert "a_bogus=" in session.calls[0][0]


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


def share_page(item):
    data = {"loaderData": {"video_(id)/page": {"videoInfoRes": {"item_list": [item]}}}}
    return "<script>window._ROUTER_DATA = " + json.dumps(data) + ";</script>"


def test_detail_falls_back_to_mobile_share_page_when_api_is_empty(monkeypatch):
    from src.interface import detail as detail_module

    async def empty_detail(self):
        return []

    monkeypatch.setattr(detail_module.Detail, "run", empty_detail)
    raw = {"aweme_id": "1234567890123456789", "video": {"play_addr": {"url_list": ["https://media.example/video.mp4"]}}}
    session = FakeSession({})
    response = FakeResponse({})
    response.text = share_page(raw)

    async def get(url, **kwargs):
        session.calls.append((url, kwargs))
        return response

    session.get = get
    result = asyncio.run(DouyinClient(session, "sessionid=fictional").detail(raw["aweme_id"]))

    assert result == raw
    url, request = session.calls[0]
    assert url.startswith("https://www.iesdouyin.com/share/video/1234567890123456789/")
    assert "Mobile" in request["headers"]["User-Agent"]
    assert "Cookie" not in request["headers"]


def test_detail_keeps_api_result_without_requesting_share_page(monkeypatch):
    from src.interface import detail as detail_module

    raw = {"aweme_id": "1234567890123456789", "video": {}}

    async def api_detail(self):
        return raw

    monkeypatch.setattr(detail_module.Detail, "run", api_detail)
    session = FakeSession({})
    assert asyncio.run(DouyinClient(session, "sessionid=fictional").detail(raw["aweme_id"])) == raw
    assert session.calls == []


@pytest.mark.parametrize("html", [
    "<html>请求被拒绝</html>",
    "<script>window._ROUTER_DATA = invalid;</script>",
    "<script>window._ROUTER_DATA = {};</script>",
    share_page({"aweme_id": "9999999999999999999", "video": {}}),
])
def test_detail_rejects_unavailable_or_wrong_share_work(monkeypatch, html):
    from src.interface import detail as detail_module

    async def empty_detail(self):
        return []

    monkeypatch.setattr(detail_module.Detail, "run", empty_detail)
    response = FakeResponse({})
    response.text = html

    async def get(*args, **kwargs):
        return response

    session = FakeSession({})
    session.get = get
    with pytest.raises(ValueError, match="未取得抖音作品详情"):
        asyncio.run(DouyinClient(session, "sessionid=fictional").detail("1234567890123456789"))


def test_video_without_bitrates_uses_share_play_address():
    raw = {
        "aweme_id": "1234567890123456789", "create_time": 1700000000,
        "video": {"bit_rate": None, "play_addr": {"uri": "v123", "url_list": ["https://media.example/share.mp4"]}},
    }
    media = asyncio.run(DouyinClient(FakeSession({}), "").extract_media(raw, original_quality=False))
    assert media_files(media)[0]["url"] == "https://media.example/share.mp4"


@pytest.mark.parametrize("video", [
    ["错误的视频结构"],
    {"play_addr": ["错误的播放地址结构"]},
    {"play_addr": {"url_list": {"url": "https://media.example/video.mp4"}}},
])
def test_share_video_with_malformed_play_address_reports_media_error(video):
    raw = {"aweme_id": "1234567890123456789", "video": video}
    with pytest.raises(ValueError, match="作品媒体地址无效"):
        media = asyncio.run(DouyinClient(FakeSession({}), "").extract_media(raw, original_quality=False))
        media_files(media)


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
        ".douyin.com\tTRUE\t/\tTRUE\t0\tsession_zero\tzero-val\n"
        ".douyin.com\tTRUE\t/\tTRUE\t1\told\texpired\n"
        ".evil.example\tTRUE\t/\tTRUE\t4102444800\tbad\tsecret\n",
        encoding="utf-8",
    )
    assert read_netscape_cookies(str(path)) == "sessionid=fictional-session; session_zero=zero-val; UIFID=fictional-uifid"


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


def test_posts_fetches_all_pages_with_earliest_date(monkeypatch):
    from src.interface import account as account_module

    calls = []

    class FakeAccount:
        def __init__(self, params, **kwargs):
            calls.append(kwargs)

        async def run(self, **kwargs):
            calls.append(kwargs)
            return ([{"aweme_id": "1"}, {"aweme_id": "2"}], None, None)

    monkeypatch.setattr(account_module, "Account", FakeAccount)
    client = object.__new__(DouyinClient)
    client.params = object()
    client.cookies = "fictional"

    result = asyncio.run(client.posts("MS4wLjABtest", "2025-01-01"))

    assert len(result) == 2
    # 第三方库只解析 yyyy/MM/dd；提前一天停止，避免其本机时区日期漏掉北京时间边界作品。
    assert calls[0]["earliest"] == "2024/12/31"
    assert calls[1] == {}  # 不传 single_page=True，让第三方库抓取后续页面


@pytest.mark.parametrize("media_filter,expected", [
    ("videos", {"1000000000000000001_1.mp4"}),
    ("galleries", {"1000000000000000002_1.jpg"}),
])
def test_user_download_filters_type_and_date(tmp_path, media_filter, expected):
    china_time = timezone(timedelta(hours=8))

    class Client:
        earliest = None

        async def resolve(self, value):
            return parse_target(value)

        async def posts(self, identifier, earliest_date=""):
            self.earliest = earliest_date
            return [
                {"id": "1000000000000000001", "kind": "video", "create_time": int(datetime(2025, 2, 3, tzinfo=china_time).timestamp())},
                {"id": "1000000000000000002", "kind": "gallery", "create_time": int(datetime(2025, 2, 4, tzinfo=china_time).timestamp())},
                {"id": "1000000000000000003", "kind": "video", "create_time": int(datetime(2024, 12, 31, tzinfo=china_time).timestamp())},
            ]

        async def extract_media(self, raw, original_quality):
            downloads = "https://media.example/1.mp4" if raw["kind"] == "video" else ["https://media.example/1.jpg"]
            return {"id": raw["id"], "downloads": downloads}

        async def stream_file(self, url, path):
            path.write_bytes(b"x")

    client = Client()
    spec = {
        "urls": ["https://www.douyin.com/user/MS4wLjABtest"],
        "options": {"base-directory": str(tmp_path), "douyin": {
            "earliest_date": "2025-01-01", "media_filter": media_filter,
        }},
    }
    asyncio.run(_download_with_client(spec, client, lambda *args, **kwargs: None))

    assert client.earliest == "2025-01-01"
    assert {p.name for p in (tmp_path / "douyin" / "MS4wLjABtest").iterdir()} == expected


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


def test_single_work_download_rejects_short_link_to_user(tmp_path):
    class UserLinkClient(FakeDownloadClient):
        async def resolve(self, value):
            return "user", "MS4wLjABtest"

        async def posts(self, identifier, earliest_date=""):
            pytest.fail("单条作品下载不应请求作者作品列表")

    spec = {
        "urls": ["https://v.douyin.com/uJS3Tm5L5iI/"],
        "options": {"base-directory": str(tmp_path), "douyin": {"target_kind": "work"}},
    }
    with pytest.raises(ValueError, match="请输入单条作品链接"):
        asyncio.run(_download_with_client(spec, UserLinkClient(), lambda *args, **kwargs: None))
    assert not (tmp_path / "douyin").exists()


def test_single_work_download_resolves_short_link_and_downloads_one_video(tmp_path):
    class WorkLinkClient(FakeDownloadClient):
        async def resolve(self, value):
            assert parse_target(value) == ("short", "https://v.douyin.com/uJS3Tm5L5iI/")
            return "work", "1234567890123456789"

    client = WorkLinkClient()
    events = []
    spec = {
        "urls": ["0.71 天赋不会给你刀刻般的肌肉💪🐱 https://v.douyin.com/uJS3Tm5L5iI/ 复制此链接，打开Dou音搜索，直接观看视频！"],
        "options": {"base-directory": str(tmp_path), "douyin": {"target_kind": "work", "original_quality": True}},
    }
    asyncio.run(_download_with_client(spec, client, lambda ev, **data: events.append((ev, data))))
    assert client.transfers == 1
    assert (tmp_path / "douyin" / "_links" / "1234567890123456789_1.mp4").read_bytes() == b"abc"
    assert events[-1] == ("job-done", {"total": 1, "skipped": 0, "failed": 0})


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

        async def posts(self, identifier, earliest_date=""):
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
