# 安全性声明

适用版本：0.1.0-beta。lin-sing 是开源的本地工具，按 MIT 协议“按原样”提供，不附带任何担保。使用前请读完下面几条，了解它在你的电脑上会做什么。

## 1. 数据去向
- **歌曲、歌词、渲染结果都只留在你的电脑上**。lin-sing 自己不上传任何文件，没有统计、遥测或在线账号。
- **会联网的只有下载**：
  - 安装依赖（pip、PyTorch）；
  - 首次使用时下载模型（Hugging Face 上的 Whisper 和 Demucs，PyTorch 官方服务器上的 MMS_FA）；
  - 克隆本仓库。
- **你用的 AI 智能体另当别论**：用 Claude、ChatGPT、DeepSeek、Kimi、Trae 等驱动 lin-sing 时，你在对话里发的内容（包括歌词、文件路径、命令输出）会按各家服务的隐私政策发送到它们的服务器。不想外传的内容，不要贴进对话。

## 2. 会在本机运行的东西
| 组件 | 做什么 | 注意 |
|---|---|---|
| `install.bat` / `install.sh` | 创建 `.venv`，用 pip 安装依赖，下载模型 | 运行前可以先打开看一眼，脚本很短 |
| `pipeline/linsing.py` | 调用 ffmpeg、Demucs、Whisper、Unity 处理你指定的音频 | 只写入 `songs/<名字>/`、`unity/Assets/MV/Songs/`、`unity/Capture/`、`pipeline/config.json` 和模型缓存目录 |
| Unity 工程 | 打开工程时，Unity 会自动编译并运行 `unity/Assets/MV/` 下的编辑器脚本（Unity 的正常行为） | 只从可信来源获取本工程 |
| MCP 服务 `pipeline/mcp_server.py` | 通过标准输入输出与智能体通信，不开网络端口；把智能体的请求转成 `linsing.py` 命令（参数以列表传递，不经过 shell） | 智能体可以让它读取你电脑上任意路径的音频，并写入 `songs/`。请在客户端里保留工具调用的审批，不要用“全部自动批准”模式 |
| 项目级 MCP 配置 `.mcp.json`、`.kimi-code/mcp.json` | 你信任这个文件夹后，Claude Code / Kimi Code 会在会话开始时运行 `python pipeline/mcp_server.py` | 客户端弹出信任提示时，核对这条命令再确认 |
| 网页编辑器 `linsing.py studio` | 本地 HTTP 服务 | 见下一节 |

## 3. 网页编辑器（studio）的防护
- **只监听本机**：只监听 `127.0.0.1`，局域网和外网都连不上。不要用端口转发、内网穿透之类的方式把它暴露出去。
- **校验 Host**：所有请求的 Host 都必须是 `127.0.0.1:端口` 或 `localhost:端口`，别的网站借 DNS 重绑定也打不进来。
- **令牌校验**：每次启动都会随机生成一个令牌，只写进它自己发给浏览器的页面里。所有 `/api` 请求都必须带上这个令牌，所以你在浏览器里打开的其他网站无法借你的浏览器让它烘焙、渲染或改写编舞表。
- **只读歌曲文件夹**：媒体接口只能读取当前歌曲文件夹里的 `.wav`、`.mp3`、`.mp4`。
- **没有登录**：它没有用户登录，默认只有你本人在本机使用。

## 4. 模型与第三方文件
- PyTorch 模型文件用的是可以执行代码的格式。只从官方地址（或 `hf-mirror.com` 这类可信镜像）下载，不要换成来历不明的权重文件。
- 第三方资产和模型的授权见 [ASSETS.md](ASSETS.md)。MMS_FA 对齐模型仅限非商用。
- 歌曲的版权归原作者所有，用 lin 翻唱、发布作品前请自行确认授权。

## 5. 报告安全问题
- 发现安全问题，请在 GitHub 仓库开 issue。只写影响范围和复现条件，不要公开可以直接利用的细节；我们会联系你私下沟通。
- 也可以通过 GitHub 仓库的 Security 页面私下提交（仓库开启了私密漏洞报告时可用）。

## 6. Beta 版说明
当前是测试版，已知问题和处理方法见 [docs/KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md)。
