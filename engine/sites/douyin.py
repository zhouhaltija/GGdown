"""抖音站点的协议适配与安全的输入/响应映射。"""

import asyncio
import http.cookiejar
import json
import os
import re
import sys
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from types import SimpleNamespace
from urllib.parse import urlsplit

from sites import AuthError

# 确保在开发环境直接调用 runner 或 sites 模块时可导入 TikTokDownloader 的 src 包
_third_party = Path(__file__).resolve().parents[2] / "third_party" / "TikTokDownloader"
if _third_party.is_dir() and str(_third_party) not in sys.path:
    sys.path.insert(0, str(_third_party))


_URL = re.compile(r"https?://[^\s，。；！？、【】《》]+", re.IGNORECASE)
_SEC_USER_ID = re.compile(r"[A-Za-z0-9_-]{8,}\Z")
_WORK_ID = re.compile(r"\d{19}\Z")
_ROUTER_DATA = re.compile(r"window\._ROUTER_DATA\s*=\s*")
_MOBILE_USER_AGENT = (
    "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) "
    "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1"
)


def read_netscape_cookies(path: str) -> str:
    try:
        jar = http.cookiejar.MozillaCookieJar(path)
        jar.load(ignore_discard=True, ignore_expires=True)
    except Exception:
        raise AuthError("Cookie 文件解析失败")
    cookies = [
        cookie for cookie in jar
        if (cookie.domain.lstrip(".").lower() == "douyin.com" or cookie.domain.lstrip(".").lower().endswith(".douyin.com"))
        and not (cookie.expires and cookie.expires > 0 and cookie.is_expired())
    ]
    if not cookies:
        raise AuthError("Cookie 文件不含可用的抖音 Cookie")
    return "; ".join(f"{cookie.name}={cookie.value}" for cookie in cookies)


def parse_target(value: str) -> tuple[str, str]:
    text = (value or "").strip()
    if _SEC_USER_ID.fullmatch(text) and text.lower() != "self" and not _WORK_ID.fullmatch(text):
        return "user", text
    match = _URL.search(text)
    if not match:
        raise ValueError("无法识别抖音用户或作品链接")
    url = urlsplit(match.group().rstrip("\"'),.]}"))
    if url.scheme not in {"http", "https"}:
        raise ValueError("链接协议无效")
    if url.hostname == "v.douyin.com" and url.path.strip("/"):
        return "short", f"https://v.douyin.com{url.path}"
    if url.hostname == "www.iesdouyin.com":
        path_parts = url.path.strip("/").split("/")
        if len(path_parts) >= 2 and path_parts[0] == "share":
            if path_parts[1] == "user" and len(path_parts) >= 3 and _SEC_USER_ID.fullmatch(path_parts[2]):
                return "user", path_parts[2]
            if path_parts[1] in {"video", "note", "slides"} and len(path_parts) >= 3 and _WORK_ID.fullmatch(path_parts[2]):
                return "work", path_parts[2]
        raise ValueError("请输入抖音用户主页或单条作品链接")
    if url.hostname not in {"douyin.com", "www.douyin.com"}:
        raise ValueError("请输入抖音网站链接")
    parts = url.path.strip("/").split("/")
    if len(parts) != 2:
        raise ValueError("请输入抖音用户主页或单条作品链接")
    kind, identifier = parts
    if kind == "user" and _SEC_USER_ID.fullmatch(identifier) and identifier.lower() != "self":
        return "user", identifier
    if kind in {"video", "note", "slides"} and _WORK_ID.fullmatch(identifier):
        return "work", identifier
    raise ValueError("请输入抖音用户主页或单条作品链接")


def map_user(raw: dict) -> dict:
    sec_uid = raw.get("sec_uid") or raw.get("secUid")
    if not sec_uid:
        raise ValueError("抖音用户响应缺少 sec_uid")
    avatar = raw.get("avatar_larger") or raw.get("avatar_medium") or {}
    avatar_urls = avatar.get("url_list") or []
    cover = raw.get("cover_url") or []
    cover_urls = cover[0].get("url_list") or [] if cover and isinstance(cover[0], dict) else []
    return {
        "rest_id": sec_uid,
        "screen_name": raw.get("unique_id") or raw.get("short_id") or sec_uid,
        "display_name": raw.get("nickname"),
        "avatar_url": avatar_urls[0] if avatar_urls else None,
        "banner_url": cover_urls[0] if cover_urls else None,
        "bio": raw.get("signature"),
        "followers_count": raw.get("follower_count"),
        "media_count": raw.get("aweme_count"),
    }


def media_files(item: dict) -> list[dict]:
    item_id = str(item.get("id") or "")
    if not _WORK_ID.fullmatch(item_id):
        raise ValueError("作品缺少有效 ID")
    media = item.get("downloads")
    if isinstance(media, str):
        urls = [media]
        suffix = "mp4"
    elif isinstance(media, list):
        urls = media
        suffix = "jpg"
    else:
        raise ValueError("作品缺少媒体地址")
    files = []
    for index, url in enumerate(urls, start=1):
        if not isinstance(url, str) or urlsplit(url).scheme not in {"http", "https"}:
            raise ValueError("作品媒体地址无效")
        extension = Path(urlsplit(url).path).suffix.lower().lstrip(".")
        chosen_suffix = extension if extension in {"jpg", "jpeg", "png", "webp", "mp4"} else suffix
        files.append({"item_id": item_id, "index": index, "url": url, "suffix": chosen_suffix})
    return files


def require_authenticated_user(response: dict) -> dict:
    status = response.get("status_code")
    if status == 8:
        raise AuthError("抖音 Cookie 未登录或已失效")
    if status != 0:
        raise ValueError(f"抖音账号验证失败，状态码 {status}")
    user = response.get("user")
    if not isinstance(user, dict) or not user.get("sec_uid"):
        raise ValueError("抖音账号响应缺少用户身份")
    return user


class _QuietLogger:
    """第三方库的日志可能包含带签名的媒体链接，不向协议或 stderr 转发。"""

    def info(self, *args, **kwargs):
        pass

    def warning(self, *args, **kwargs):
        pass

    def error(self, *args, **kwargs):
        pass

    def debug(self, *args, **kwargs):
        pass


class _NullRecorder:
    field_keys = ()

    async def save(self, values):
        pass


async def fetch_current_user(session, cookies: str) -> dict:
    from src.custom import DATA_HEADERS, USERAGENT
    from src.encrypt import DouYinParams
    from src.interface.template import API
    from src.tools import cookie_str_to_dict

    cookie_dict = cookie_str_to_dict(cookies)
    params = dict(API.params)
    params["msToken"] = next((v for k, v in cookie_dict.items() if k.lower() == "mstoken"), "")
    params["msToken"] = params.pop("msToken")
    signed_query = DouYinParams().sign_url(
        "https://www.douyin.com/aweme/v1/web/user/profile/self/",
        params, None, "GET", user_agent=USERAGENT,
    )
    headers = dict(DATA_HEADERS)
    headers["Cookie"] = cookies
    uifid = next((v for k, v in cookie_dict.items() if k.lower() == "uifid"), "")
    if uifid:
        headers["uifid"] = uifid

    url = (
        f"https://www.douyin.com/aweme/v1/web/user/profile/self/?{signed_query}"
        if signed_query
        else "https://www.douyin.com/aweme/v1/web/user/profile/self/"
    )
    response = await session.get(url, headers=headers)
    response.raise_for_status()
    return map_user(require_authenticated_user(response.json()))


class DouyinClient:
    def __init__(self, session, cookies: str):
        from src.custom import DATA_HEADERS, IMPERSONATE, USERAGENT
        from src.encrypt import DouYinParams
        from src.tools import Cleaner

        self.session = session
        self.cookies = cookies
        self.rate_limiter = None
        self.params = SimpleNamespace(
            headers=DATA_HEADERS | {"Cookie": cookies},
            logger=_QuietLogger(), douyin_params=DouYinParams(),
            console=None, client=session, max_retry=1, timeout=20,
            impersonate=IMPERSONATE, user_agent=USERAGENT, max_pages=99999,
            date_format="%Y-%m-%d %H:%M:%S", CLEANER=Cleaner(),
            download=True, original_quality=False,
        )

    async def resolve(self, value: str) -> tuple[str, str]:
        kind, identifier = parse_target(value)
        if kind != "short":
            return kind, identifier
        from src.link import Extractor as LinkExtractor

        expanded = await LinkExtractor(self.params).run(identifier, type_="")
        return parse_target(expanded)

    async def user(self, identifier: str) -> dict:
        from src.interface.user import User

        raw = await User(self.params, cookie=self.cookies, sec_user_id=identifier).run()
        if not isinstance(raw, dict) or not raw:
            raise ValueError("未取得抖音用户资料")
        return map_user(raw)

    async def posts(self, identifier: str, earliest_date: str = "") -> list[dict]:
        from src.interface.account import Account

        # 第三方库仅接受 yyyy/MM/dd，且按运行机器的本地时区判断翻页截止日期。
        # 提前一天停止，随后由下载流程按北京时间精确过滤边界日期。
        earliest_for_pages = ""
        if earliest_date:
            since = date.fromisoformat(earliest_date)
            page_since = since - timedelta(days=1) if since > date.min else since
            earliest_for_pages = f"{page_since.year:04d}/{page_since.month:02d}/{page_since.day:02d}"
        raw, _, _ = await Account(
            self.params, cookie=self.cookies, sec_user_id=identifier,
            tab="post", earliest=earliest_for_pages,
        ).run()
        if not isinstance(raw, list):
            raise ValueError("未取得抖音发布作品")
        if not raw:
            raise ValueError("未取得抖音发布作品")
        return raw

    async def detail(self, identifier: str) -> dict:
        from src.interface.detail import Detail

        raw = await Detail(self.params, cookie=self.cookies, detail_id=identifier).run()
        if isinstance(raw, dict) and raw:
            return raw
        return await self._share_detail(identifier)

    async def _share_detail(self, identifier: str) -> dict:
        from curl_cffi.requests.exceptions import RequestException

        # Web 接口有时拒绝单条详情请求；移动分享页仍可能包含同一作品的公开数据。
        # 该域名不需要登录 Cookie，只读取 JSON，不执行页面脚本。
        url = (
            f"https://www.iesdouyin.com/share/video/{identifier}/"
            "?region=CN&titleType=title&from_aid=1128&from=web_code_link"
        )
        try:
            response = await self.session.get(
                url, headers={"User-Agent": _MOBILE_USER_AGENT}, impersonate="safari17_0",
            )
            response.raise_for_status()
            match = _ROUTER_DATA.search(response.text)
            if match:
                data, _ = json.JSONDecoder().raw_decode(response.text[match.end():])
                loaders = data.get("loaderData") if isinstance(data, dict) else None
                if isinstance(loaders, dict):
                    for loader in loaders.values():
                        info = loader.get("videoInfoRes") if isinstance(loader, dict) else None
                        items = info.get("item_list") if isinstance(info, dict) else None
                        if isinstance(items, list):
                            for item in items:
                                if isinstance(item, dict) and str(item.get("aweme_id")) == identifier:
                                    return item
        except (RequestException, ValueError):
            pass
        raise ValueError("未取得抖音作品详情：接口与分享页均未返回作品，请确认链接可访问或稍后重试")

    async def extract_media(self, raw: dict, original_quality: bool) -> dict:
        from src.extract import Extractor

        self.params.original_quality = original_quality
        result = await Extractor(self.params).run([raw], _NullRecorder(), type_="detail")
        if not result:
            raise ValueError("作品没有可下载的媒体")
        item = result[0]
        # 分享页可能没有码率列表；这时使用页面提供的播放地址。
        if not item.get("downloads") and not raw.get("images"):
            video = raw.get("video")
            play = video.get("play_addr") if isinstance(video, dict) else None
            urls = play.get("url_list") if isinstance(play, dict) else None
            if isinstance(urls, list):
                item["downloads"] = next((
                    url for url in reversed(urls)
                    if isinstance(url, str) and url.startswith(("https://", "http://"))
                ), "")
        return item

    async def stream_file(self, url: str, path: Path) -> None:
        from src.custom import DOWNLOAD_HEADERS

        if self.rate_limiter is not None:
            # 在 libcurl 接收回调中等待，避免异步流在后台把整个视频预先缓存下来。
            with path.open("wb") as target:
                def receive(chunk):
                    self.rate_limiter.consume(len(chunk))
                    return target.write(chunk)
                response = await self.session.get(
                    url, headers=DOWNLOAD_HEADERS, content_callback=receive, timeout=None,
                )
                response.raise_for_status()
            return

        async with self.session.stream("GET", url, headers=DOWNLOAD_HEADERS) as response:
            response.raise_for_status()
            with path.open("wb") as target:
                async for chunk in response.aiter_content():
                    target.write(chunk)


async def _with_client(cookies_path: str, operation):
    from curl_cffi.requests import AsyncSession
    from src.custom import IMPERSONATE

    cookies = read_netscape_cookies(cookies_path)
    proxy = os.environ.get("ALL_PROXY") or os.environ.get("HTTPS_PROXY") or os.environ.get("HTTP_PROXY")
    from curl_cffi import CurlOpt
    async with AsyncSession(timeout=20, impersonate=IMPERSONATE, proxy=proxy, curl_options={
        CurlOpt.CONNECTTIMEOUT_MS: 20000, CurlOpt.LOW_SPEED_LIMIT: 1, CurlOpt.LOW_SPEED_TIME: 60,
    }) as session:
        return await operation(DouyinClient(session, cookies))


def whoami(cookies_path: str) -> dict:
    return asyncio.run(_with_client(
        cookies_path,
        lambda client: fetch_current_user(client.session, client.cookies),
    ))


def user_info(cookies_path: str, value: str) -> dict:
    async def get(client):
        kind, identifier = await client.resolve(value)
        if kind != "user":
            raise ValueError("请输入抖音用户主页或 sec_user_id")
        return await client.user(identifier)

    return asyncio.run(_with_client(cookies_path, get))


def download(cookies_path: str, spec: dict, emit_event) -> None:
    from rate_limit import SharedRateLimiter

    async def run(client, limiter):
        client.rate_limiter = limiter if limiter.config else None
        await _download_with_client(spec, client, emit_event)

    with SharedRateLimiter((spec.get("options") or {}).get("ggdown-rate-limit")) as limiter:
        return asyncio.run(_with_client(cookies_path, lambda client: run(client, limiter)))


async def _download_with_client(spec: dict, client, emit_event) -> None:
    options = spec.get("options") or {}
    site_options = options.get("douyin") or {}
    base_directory = options.get("base-directory") or ""
    if not base_directory:
        raise ValueError("未设置下载目录")
    archive_name = site_options.get("archive") or ""
    archive_path = Path(archive_name) if archive_name else None
    archived = set()
    if archive_path and archive_path.exists():
        archived = set(archive_path.read_text(encoding="utf-8").splitlines())
    quality = site_options.get("original_quality") is True
    media_filter = site_options.get("media_filter") or "all"
    if media_filter not in {"all", "videos", "galleries"}:
        raise ValueError("无效的抖音作品类型")
    earliest_date = site_options.get("earliest_date") or ""
    since = date.fromisoformat(earliest_date) if earliest_date else None
    china_time = timezone(timedelta(hours=8))
    done = skipped = failed = 0

    for input_url in spec.get("urls") or []:
        kind, identifier = await client.resolve(input_url)
        if site_options.get("target_kind") == "work" and kind != "work":
            raise ValueError("请输入单条作品链接，不能使用用户主页链接")
        if kind == "user":
            canonical = f"https://www.douyin.com/user/{identifier}"
            raw_items = await client.posts(identifier, earliest_date)
            output_dir = Path(base_directory) / "douyin" / identifier
        elif kind == "work":
            canonical = f"https://www.douyin.com/video/{identifier}"
            raw_items = [await client.detail(identifier)]
            output_dir = Path(base_directory) / "douyin" / "_links"
        else:
            raise ValueError("仅支持用户发布作品和单条作品")
        if not raw_items:
            raise ValueError("未取得抖音发布作品")
        emit_event("url-start", url=canonical)
        output_dir.mkdir(parents=True, exist_ok=True)

        for raw in raw_items:
            if since is not None:
                created = raw.get("create_time")
                if created is None or datetime.fromtimestamp(int(created), china_time).date() < since:
                    continue
            normalized = await client.extract_media(raw, quality)
            downloads = normalized.get("downloads")
            if media_filter == "videos" and not isinstance(downloads, str):
                continue
            if media_filter == "galleries" and not isinstance(downloads, list):
                continue
            for media in media_files(normalized):
                item_id = media["item_id"]
                file_key = f"{item_id}:{media['index']}"
                target = output_dir / f"{item_id}_{media['index']}.{media['suffix']}"
                if file_key in archived or target.exists():
                    skipped += 1
                    emit_event("file-skip", path=str(target), item_id=item_id)
                    continue

                part = target.with_name(target.name + ".part")
                if part.exists():
                    part.unlink()
                emit_event("file-start", path=str(target), item_id=item_id)
                try:
                    await client.stream_file(media["url"], part)
                    if not part.is_file():
                        raise OSError("未生成下载文件")
                    part.replace(target)
                    size = target.stat().st_size
                    done += 1
                    emit_event("file-done", path=str(target), item_id=item_id, size=size)
                    if archive_path:
                        archive_path.parent.mkdir(parents=True, exist_ok=True)
                        with archive_path.open("a", encoding="utf-8") as archive:
                            archive.write(file_key + "\n")
                        archived.add(file_key)
                except asyncio.CancelledError:
                    part.unlink(missing_ok=True)
                    raise
                except Exception as error:
                    part.unlink(missing_ok=True)
                    failed += 1
                    emit_event("log", level="error", msg=f"作品 {item_id} 的文件 {media['index']} 下载失败：{type(error).__name__}")

    emit_event("job-done", total=done + skipped + failed, skipped=skipped, failed=failed)
