"""发行引擎依赖闭包自检：只导入与构造，不发起任何网络请求。

开发虚拟环境通常安装了 fastapi/uvicorn/pydantic 等冗余包，只跑测试无法发现
发行包缺少真实依赖。构建脚本在嵌入解释器中执行本模块，配合
`tests/test_runtime_dependencies.py` 的禁用导入进程，共同确认精简后的闭包完整。
"""
import asyncio
import sys
from pathlib import Path


def _ensure_import_paths() -> None:
    """开发环境下补齐仓库路径；发行包已把 src 与引擎模块放入 site-packages。"""
    root = Path(__file__).resolve().parent
    for path in (root, root.parent / "third_party" / "TikTokDownloader"):
        if path.is_dir() and str(path) not in sys.path:
            sys.path.insert(0, str(path))


class _NoNetworkSession:
    """占位会话：构造客户端期间若被访问即视为自检失败，避免意外联网。"""

    def __getattr__(self, name):
        raise AssertionError(f"自检不应访问网络会话属性：{name}")


def verify_runtime() -> None:
    _ensure_import_paths()

    import curl_cffi
    from curl_cffi import CurlOpt
    from curl_cffi.requests import AsyncSession

    from gallery_dl import extractor
    from gallery_dl.extractor.common import Extractor as ExtractorBase
    from gallery_dl.extractor.pixiv import PixivExtractor, PixivWorkExtractor
    from gallery_dl.extractor.twitter import TwitterAPI, TwitterExtractor

    from sites import douyin, pixiv, twitter
    from src.extract import Extractor
    from src.interface.account import Account
    from src.interface.detail import Detail
    from src.interface.template import API
    from src.interface.user import User
    from src.link import Extractor as LinkExtractor

    # 三站适配器与 gallery-dl 提取器：发行包必须能按 URL 找到对应实现。
    assert issubclass(TwitterExtractor, ExtractorBase)
    assert issubclass(PixivExtractor, ExtractorBase)
    assert isinstance(extractor.find("https://x.com/ggdown"), TwitterExtractor)
    assert isinstance(extractor.find("https://www.pixiv.net/artworks/1"), PixivWorkExtractor)
    assert callable(TwitterAPI)
    for site, names in (
        (twitter, ("whoami", "list_following", "user_info")),
        (pixiv, ("whoami", "list_following", "user_info")),
        (douyin, ("whoami", "user_info", "download")),
    ):
        assert all(callable(getattr(site, name, None)) for name in names), site.__name__

    # 抖音：链接解析、接口对象与客户端构造均不联网。
    work_id = "1" * 19
    assert douyin.parse_target(f"https://www.douyin.com/video/{work_id}") == ("work", work_id)
    client = douyin.DouyinClient(_NoNetworkSession(), "sessionid=smoke; msToken=smoke")
    assert asyncio.run(client.resolve(f"https://www.douyin.com/video/{work_id}")) == ("work", work_id)
    for api in (
        client._prepare_api(User(client.params, cookie=client.cookies, sec_user_id="smoke")),
        client._prepare_api(Account(client.params, cookie=client.cookies, sec_user_id="smoke")),
        client._prepare_api(Detail(client.params, cookie=client.cookies, detail_id=work_id)),
    ):
        assert isinstance(api, API)
        assert api.params["msToken"] == "smoke"

    assert callable(LinkExtractor) and callable(Extractor)
    assert CurlOpt.CONNECTTIMEOUT_MS and AsyncSession and curl_cffi

    print("runtime smoke ok: sites, gallery-dl extractors, douyin client, curl_cffi")


if __name__ == "__main__":
    verify_runtime()
