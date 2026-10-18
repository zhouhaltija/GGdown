# 抖音首版支持设计

## 范围

在 GGdown 中增加抖音平台，沿用 X 的 Cookie 导入、手动添加用户、用户列表、下载队列、历史和站点设置。下载范围仅包括用户发布的作品和单条作品链接，覆盖视频与图集。抖音页面不显示关注列表、高光、喜欢、收藏、搜索和直播入口。站点设置提供“最高质量视频”布尔选项，默认 `false`，与所用 DouK-Downloader 5.8 的 `original_quality` 默认值相同。

## 技术选择

推荐直接调用本机 `third_party/TikTokDownloader` 检出的库模块，而不是启动其交互式 CLI。该库的 `src.interface.Account`、`User`、`Detail` 负责请求，`src.link.Extractor` 负责短链接和作品 ID 识别，`src.extract.Extractor` 负责媒体地址和画质选择。GGdown 自己的薄适配层负责文件保存、归档去重和 JSONL 事件，以保持现有取消、进度与历史语义。完整 CLI 自行管理配置、输出和文件记录，难以和当前队列契约对齐；改用 gallery-dl 抖音实现则不满足指定库要求。

## 数据流与边界

1. `DouyinSiteProvider` 接受 `www.douyin.com/user/<sec_user_id>` 和 `www.douyin.com/{video,note,slides}/<19 位 ID>` 等输入；短链接和带分享文案的链接交由 Python 链接解析器解析。`UserTarget.RestId` 存 `sec_user_id`，`ScreenName` 存可显示的抖音号或回退到 `sec_user_id`。站点下载计划仍使用现有 `DownloadPlan`、`ContentKind.UserMedia` 和 `ContentKind.Permalink`。
2. Python runner 根据 `--site douyin` 将 `whoami`、`user-info`、`download` 路由到 `engine/sites/douyin.py`。协议仍为 v1，不改 C# JSONL 解析。抖音输出 `hello`、`account`、`user`、`url-start`、`file-start`、`file-done`、`file-skip`、`job-done` 和 `fatal`。诊断仅写 stderr，绝不输出 Cookie、完整请求头或带认证参数的媒体 URL。
3. Cookie 导入沿用 Netscape `cookies.txt`，只把 `.douyin.com` / `www.douyin.com` 的非过期项转为请求头。账号验证必须从有认证语义的响应得到当前用户身份；单看 `sessionid` 等字段不能标记为有效。用户资料请求使用库的 `User` 接口。请求失败与私密内容不能直接判定 Cookie 失效，只有明确认证失败才发 `fatal kind=auth`。
4. 媒体下载用库解析出的候选地址，优先按作品 ID 和媒体序号生成安全文件名。存在文件或归档记录时发 `file-skip`，成功落盘后发 `file-done` 并带实际大小；失败计数不伪装成功。中断时删除未完成的临时文件，保留已有完整文件。画质开关映射到库的 `original_quality`；其正常模式已从返回的码率列表选最高档，打开后额外尝试原质量地址。图片仍采用库提供的图集地址。代理沿用 C# 注入的环境变量。
5. 抖音作为独立站点入库，复用现有站点库工厂、账号服务和下载队列。用户刷新使用 `RestId` 生成主页 URL，避免把显示用抖音号误作 `sec_user_id`。下载页显示独立“作品链接”输入和按钮，不借用 X 搜索的 URL 回退逻辑。

## 打包和许可

开发时使用项目 `engine/.venv` 中由 uv 管理的 Python 3.13.7 和 `engine/uv.lock` 锁定的依赖，从 `third_party/TikTokDownloader` 本机源码加载，固定当前检出 `473c90ff70c663cfb69310fff2b8d5192f200661`（项目版本 5.8）。该库的 `setup.py` 用 cx_Freeze 构建可执行程序，不作为 uv 路径包安装；项目锁文件直接声明其运行依赖。发行版从同一锁文件安装依赖，将内置 Python 3.13.7、必要源码和依赖一同分发并校验导入，用户电脑无需安装 uv。将该库的 GPL-3.0 文本及实际打包依赖的许可随安装包分发，更新 `THIRD-PARTY.md`。`third_party/` 和 `docs/*_cookies.txt` 均被忽略，源码取得方式及本机前置依赖必须在构建说明写明。

## 验证

- C#：输入解析、下载计划、站点注册、平台可见性、无关注列表/高光、单条链接入队。
- Python：Cookie 解析、账号与用户事件、库响应到媒体文件映射、最高质量开关、跳过/失败/取消以及协议输出；接口测试用受控响应，不依赖真实账号。
- 集成：在允许联网的环境用 `docs/www.douyin.com_cookies.txt` 本地验证账号、用户作品、单条视频和图集；不记录 Cookie、个人资料原文或带签名 URL。内置 Python 做导入和下载冒烟测试。
- 完成每次代码修改后运行 `dotnet build src/GGdown.App/GGdown.App.csproj -c Debug -p:Platform=x64`；JSONL 边界变动同时运行 C# 与 Python 测试。

## 已知验证限制

当前沙箱内未安装库依赖，在线 `/user/self` 探测返回空响应体。实现应通过受控测试明确账号验证路径；实际联网联调和发行包冒烟测试需在可用环境运行，若仍无法取得可靠当前用户身份，应保留未验证状态并报告，不能以 Cookie 字段存在代替成功验证。
