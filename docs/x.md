基于该文章的内容，为您整理了一份清晰、结构化的 **`gallery-dl` 下载 X (Twitter) 帖子与媒体的使用指南**。



# 使用 gallery-dl 下载 X (Twitter) 帖子/媒体完整指南

本指南支持 **Windows** 和 **macOS**，适用于下载单个帖子、用户主页、书签（Bookmarks）以及喜欢的推文（Likes）中的图片与视频。



## 一、前期准备与目录规划

建议在桌面创建一个统一的工作目录，方便管理工具、配置文件和下载的文件。



在桌面新建文件夹 `TwitterDownloader`，并在其中新建子文件夹 `downloads`：



Plaintext

```
Desktop/
└── TwitterDownloader/
    └── downloads/
```

## 二、安装 gallery-dl

### 1. Windows 用户

1. 访问 [gallery-dl GitHub Releases](https://github.com/mikf/gallery-dl/releases) 下载独立可执行文件 `gallery-dl.exe`。

2. 将 `gallery-dl.exe` 移动到前面创建的 `TwitterDownloader` 目录下。

3. 进入 `TwitterDownloader` 文件夹，按住 **Shift + 鼠标右键**，选择 **在终端中打开** 或 **在此处打开 PowerShell 窗口**。

4. 验证安装：

   PowerShell

   ```
   .\gallery-dl.exe --version
   ```

### 2. macOS 用户

1. 确保已安装 Python 3（可前往 [python.org](https://python.org) 下载安装）。

2. 打开 **终端 (Terminal)**，运行以下命令安装：

   Bash

   ```
   python3 -m pip install -U gallery-dl
   ```

3. 验证安装：

   Bash

   ```
   gallery-dl --version
   ```

## 三、导出登录 Cookie（获取书签/点赞/私密内容必选）

由于 X 限制未登录访问，抓取书签和点赞需要导入已登录状态的 Cookie。



1. **安装浏览器扩展**：
   - Chrome / Edge / Brave：[Get cookies.txt LOCALLY](https://chromewebstore.google.com/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc)
   - Firefox：[Get cookies.txt LOCALLY](https://addons.mozilla.org/en-US/firefox/addon/get-cookies-txt-locally/)
2. 打开并登录 [x.com](https://x.com)。
3. 点击扩展图标：
   - 格式选择 **Netscape**。
   - 点击 **Export As**，将文件保存到 `TwitterDownloader` 目录下，命名为 `x.com_cookies.txt`。

> ⚠️ **安全提示**：Cookie 包含您的登录凭证，请勿分享给他人；完成批量下载后建议删除该文件。

## 四、创建配置文件 (`config.json`)

在 `TwitterDownloader` 目录下新建一个文本文件，命名为 `config.json`（注意后缀不要是 `.json.txt`），写入以下配置：



JSON

```
{
  "base-directory": "downloads",
  "extractor": {
    "twitter": {
      "cookies": "x.com_cookies.txt",
      "videos": true,
      "retweets": false,
      "quoted": false,
      "replies": false,
      "directory": [],
      "filename": "{tweet_id}_{author[name]}_{num}.{extension}"
    }
  }
}
```

### 参数说明：

- `"base-directory"`：所有下载文件统一保存到 `downloads` 文件夹中。
- `"cookies"`：关联导出的 Cookie 文件。
- `"videos": true`：同时下载视频和动图。
- `"retweets" / "quoted" / "replies": false`：过滤转推、引用推文和回复推文，仅下载原推。
- `"filename"`：自定义命名规则（推文ID + 作者昵称 + 序号 + 后缀）。

此时目录结构如下：



Plaintext

```
Desktop/TwitterDownloader/
├── downloads/
├── x.com_cookies.txt
├── config.json
└── gallery-dl.exe   (Windows 独有)
```

## 五、运行下载命令

打开终端或 PowerShell，先进入 `TwitterDownloader` 目录：



Bash

```
cd 你的TwitterDownloader目录路径
```

*(技巧：输入 `cd ` 后直接将 `TwitterDownloader` 文件夹拖入终端窗口即可自动补全路径)*



### 1. 下载单个帖子 / 推文

- **Windows**:

  PowerShell

  ```
  .\gallery-dl.exe --config .\config.json "https://x.com/username/status/1234567890"
  ```

- **macOS**:

  Bash

  ```
  gallery-dl --config config.json "https://x.com/username/status/1234567890"
  ```

### 2. 批量下载「书签 (Bookmarks)」

- **Windows**:

  PowerShell

  ```
  .\gallery-dl.exe --config .\config.json "https://x.com/i/bookmarks"
  ```

- **macOS**:

  Bash

  ```
  gallery-dl --config config.json "https://x.com/i/bookmarks"
  ```

### 3. 批量下载某用户的所有「喜欢/点赞 (Likes)」

*(将 `<your-username>` 替换为实际推特用户名)*



- **Windows**:

  PowerShell

  ```
  .\gallery-dl.exe --config .\config.json "https://x.com/<your-username>/likes"
  ```

- **macOS**:

  Bash

  ```
  gallery-dl --config config.json "https://x.com/<your-username>/likes"
  ```

### 4. 批量下载某用户发布的所有媒体 (Media)

- **Windows**:

  PowerShell

  ```
  .\gallery-dl.exe --config .\config.json "https://x.com/<target-username>/media"
  ```

- **macOS**:

  Bash

  ```
  gallery-dl --config config.json "https://x.com/<target-username>/media"
  ```

## 六、文件溯源技巧

下载的文件名默认包含 `{tweet_id}`，后续若想查看原推文，只需复制文件名开头的数字 ID，在浏览器中访问：

Plaintext

```
https://x.com/i/status/<tweet_id>
```