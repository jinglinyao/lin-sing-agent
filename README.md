# lin-sing：让智能体驱动 3D 角色 lin 唱任何一首歌

> **当前版本：0.1.0-beta（测试版）**。可能遇到的错误和解决方法见 [docs/KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md)，安全说明见 [SECURITY.md](SECURITY.md)。

![lin-sing 说明长图](docs/lin-sing-guide.png)

给一段音频（10 分钟以内），lin 就会站到立麦前把它唱出来：
- 嘴对上每一个音节；
- 眨眼、闭眼、视线跟着乐句走；
- 手势、重心、呼吸跟着歌词情绪和音乐的拍子、小节、强弱走；
- 摄像机沿着小节线连续运镜，舞台灯光随段落变化；
- 你提供的歌词（可以附上译文）会对齐到每一句，作为字幕显示在屏幕下方。

全部在你自己的电脑上运行，不按秒收费，也不需要上传你的歌。

> English: lin-sing makes a VRM character (lin) sing any song up to 10 minutes. It separates the vocals, aligns every syllable, builds a lip-sync and face track, analyses tempo, bars and loudness, writes an editable choreography, bakes the body animation and the camera, and renders in Unity 6. Agents (Claude, ChatGPT/Codex, DeepSeek, Trae, Cursor…) drive it through `AGENTS.md`, an MCP server, or function-calling definitions.

## 不懂技术？用整合包（Windows + NVIDIA 显卡）
一个压缩包里装好了全部东西：Python、AI 模型、ffmpeg、lin 的播放器。不用装 Unity，不用联网，解压后双击就能用。
- **下载**：整合包放在网盘（链接见仓库的 Releases 页面或作者主页），分卷压缩，约 7–8 GB。
- **电脑要求**：
  - Windows 10/11 64 位；
  - NVIDIA RTX 20 系 / GTX 16 系或更新的显卡，显存 6 GB 以上；
  - 内存 16 GB 以上，磁盘留 10 GB。
  - 配置不够时，启动程序会直接说明原因，不会硬跑。暂不支持 AMD 显卡（在测试中）、Intel 显卡和 Mac。
- **用法**：
  1. 解压后双击「启动 lin-sing.bat」，浏览器会自动打开；
  2. 拖入歌曲，粘贴歌词（可选），选择这首歌的感觉，点“生成”；
  3. 一首 4 分钟左右的歌约 3–8 分钟出片。
- **两种模式**：
  - 普通模式：上面这套，一键生成；
  - 高级模式：在时间轴编辑器里逐段改动作、表情、镜头，或者让 Claude Code / Kimi Code / Codex 等智能体按 AGENTS.md 来驱动。
- **授权**：整合包里的 MMS_FA 对齐模型仅限非商用，所以整合包整体仅供非商业使用。
- **自己打包**：见 `tools/package/`（`build_package.py`，组装好以后用 `fresh_env_test.py` 模拟一台干净的电脑来验收）。

## 要解决的问题
- **花钱**：付费 AI 数字人和视频生成按秒、按次收费，一首四分钟的歌要做好几遍，费用很快就上去了。
- **没法改**：生成出来就是一段视频。手不对、表情不对，只能整段重新抽卡。
- **嘴和歌对不上**：“说话”数字人对唱歌的拖长音、换气、嘶吼处理得不好。
- **动作不懂音乐**：动作不会落在拍子和小节上，副歌和主歌也没有区别。

## 工作原理（九步，每步一个文件）
1. 规范化音频。
2. Demucs 分离人声和伴奏。
3. 歌词分句：用你提供的歌词（LRC、纯文本、`原文 | 译文`）；不提供就用 Whisper 听写。
4. MMS_FA 把每个音节对齐到人声上。支持日语、中文、英语。
5. librosa 分析速度、小节和每小节的响度。
6. 生成面部轨道：口型、眨眼、视线。
7. 自动编舞：生成编舞表 `choreo.json`，再烘焙成 60 帧/秒的身体动作和表情。
8. 自动运镜与灯光。
9. 生成歌词字幕。
10. Unity 实时播放，或渲染成带字幕的 mp4。

每一步的细节和替换方法见 [docs/DESIGN.md](docs/DESIGN.md)。

## 和付费 AI 数字人比
| | lin-sing | 付费 AI 数字人 / 视频生成 |
|---|---|---|
| 费用 | 免费，本地显卡 | 按秒或按次计费 |
| 能不能改 | 编舞表的每一条都能改，改完几秒重烘 | 只能重新生成 |
| 口型 | 逐音节对齐，唱歌专用的长音和闭唇处理 | 多为说话口型 |
| 和音乐的关系 | 动作落在拍子和小节上，按强弱分档 | 基本无关 |
| 一致性 | 同一个 3D 角色，每次都一样 | 换一次镜头脸可能就变了 |
| 时长 | 一次最长 10 分钟 | 常见单段几秒到一分钟 |
| 隐私 | 歌曲不出本机 | 要上传 |
| 画面 | 3D 卡通渲染，舞台和立麦 | 可以是写实 |

## 快速开始
```bash
git clone https://github.com/jinglinyao/lin-sing-agent.git
cd lin-sing-agent
```
0. 最省事的方式：Windows 双击 `install.bat`，macOS / Linux 运行 `bash install.sh`。它会建好环境、装好依赖、下载模型并自检。装完后跳到第 3 步。
1. 安装依赖：
   - Python 3.10+、ffmpeg。
   - PyTorch：按你的显卡版本安装。
   - 然后运行 `pip install -r pipeline/requirements.txt`。
2. 安装 **Unity 6000.3.28f1**（本工程用的版本，详见下面的“下载 Unity”）。装好后运行 `python pipeline/linsing.py doctor`，它会自动找到 Unity 并记住路径。
3. 第一次使用：用 Unity 打开 `unity/`，点菜单 **LinSing / First-time Setup**。它会重新导入 lin 并量好尺寸。
4. 做一首歌：
   ```bash
   cd pipeline
   python linsing.py make 我的歌.mp3 --lyrics 歌词.txt --mood sad
   ```
   - 歌词一行一句；要显示译文就写成 `原文 | 译文`。
   - `--mood` 可选 `neutral` / `sad` / `bright`，决定表情和舞台的冷暖。
5. 在 Unity 里点 **LinSing / Load Song Folder**，选中 `songs/我的歌`，再点 **LinSing / Play**，就能带声音实时看。
6. 先看一段预览：`python linsing.py render ../songs/我的歌 --from 60 --to 90`（720p，只渲染 30 秒）。
7. 想用鼠标改编舞：运行 `python linsing.py studio ../songs/我的歌`，会在浏览器里打开时间轴编辑器。可以拖动、换动作、换表情，保存后自动烘焙并质检，还能直接渲染选中的一段来预览。
8. 出整首视频：`python linsing.py render ../songs/我的歌`，会生成 `songs/我的歌/我的歌.mp4`，字幕已经烧在画面里。

**时长限制**：
- 音频最长 10 分钟。
- 一次输入 5 分钟以上的音频，电脑可能会卡顿：人声分离、对齐、动作烘焙的耗时和显存都随长度增长。程序能跑完，只是慢；也可以先把歌切成几段。

## 下载 Unity（工程版本 6000.3.28f1）
| 方式 | 链接 |
|---|---|
| 先装 Unity Hub | https://unity.com/download |
| 用 Unity Hub 一键安装这个版本 | `unityhub://6000.3.28f1/8f082d2fe476`（复制到浏览器地址栏打开，会自动唤起 Unity Hub） |
| 这个版本的发布页（可手动下载安装包） | https://unity.com/releases/editor/whats-new/6000.3.28f1 |
| 历史版本总览 | https://unity.com/releases/editor/archive |
| 中国大陆（Unity 中国官网） | https://unity.cn/releases （中国版编辑器的版本号和国际版不同，选 6000.3 系列最接近的版本；**未经测试**） |

- 只需要 Windows 或 macOS 的编辑器本体，不用勾选 Android / iOS 等平台模块。
- Unity Personal 对个人和小团队免费，具体以 Unity 官方条款为准。
- 版本不一致时，Unity 会提示升级或重新导入工程；其他 6000.3.x 版本一般也能用，但没有测试过。

## 中国大陆用户
- 一键安装加参数 `cn`：`install.bat cn` 或 `bash install.sh cn`。pip 会改用清华源，模型从 hf-mirror.com 下载。
- 访问 GitHub 需要代理时，克隆前先设置 `HTTPS_PROXY`。

## 工具
| 命令 | 作用 |
|---|---|
| `doctor [--fix]` | 环境自检（Python、ffmpeg、显卡、模型、Unity、字体、磁盘），`--fix` 自动下载缺少的模型 |
| `qa <歌> [--deep]` | 质检报告：动作平顺度、握麦比例、歌词置信度、字幕、镜头、口型；`--deep` 让 Unity 逐帧做真实网格穿模扫描 |
| `studio <歌>` | 网页时间轴编辑器 |
| `render <歌> [--from --to]` | 分段渲染，直接用显卡编码。改了编舞只重渲变化的段；实测 3 分 35 秒的歌 1080p 整首约 1.5 分钟，改一处约 50 秒 |

## 用智能体驱动
| 智能体 | 接入方式 |
|---|---|
| Claude Code / Claude Desktop | 读 `CLAUDE.md`，它指向 `AGENTS.md`；也可以注册 MCP 服务 `python pipeline/mcp_server.py` |
| ChatGPT / Codex | 读 `AGENTS.md`；支持 MCP 的客户端注册同一个服务；只用 API 时用 `pipeline/agent_tools.json` 做函数调用 |
| DeepSeek | 在任何能读 `AGENTS.md` 或接 MCP 的工具里用（OpenCode、Cline、兼容 Claude Code 的接口等）；直接用 API 时同样用 `agent_tools.json` |
| Trae | 读 `.trae/rules/project_rules.md`，它指向 `AGENTS.md`；在 Trae 里添加 MCP 服务 |
| Kimi（中国版桌面端 Kimi Code） | 用 Kimi Code 桌面版打开仓库根目录，信任这个文件夹后，会自动读取 `AGENTS.md`、加载项目里的 MCP 服务 `.kimi-code/mcp.json` 和技能 `.agents/skills/lin-sing`；在输入框里输入 `/skill:lin-sing 歌曲路径` 就能直接开始 |
| Cursor / Copilot | 读 `.cursor/rules/`、`.github/copilot-instructions.md` |

- Kimi Code 桌面版下载：https://www.kimi.com/code/docs/kimi-code-desktop/getting-started.html（Windows / macOS / Linux 都有；中国大陆用 kimi.com 账号登录。没有订阅 Kimi 会员的账号用不了官方模型）。
- Claude Code 同样会读取项目里的 `.mcp.json`，自动接入 lin-sing 的 MCP 服务。

你可以直接对智能体说：“用 `songs/我的歌` 的歌词改一下编舞，副歌第二句要双手打开，然后渲染。”

## 打包内容
- **lin**：VRoid 制作的 VRM 1.0 模型，作者允许再分发和修改。
- **舞台和立麦**：代码生成的预制体。
- **动作包**：
  - 24 个编舞动作和 17 个手部姿势（Python）；
  - 43 个代码手势（Unity）；
  - Quaternius CC0 动画库；
  - 14 段 BEAT2 动捕片段。

每项的授权见 [ASSETS.md](ASSETS.md)。Mixamo 动作因为条款原因不包含，需要的话请自己下载。

## 目录
```
pipeline/       Python 管线：linsing.py（总命令）、mcp_server.py、lin_sing/（各步骤）
unity/          Unity 6 工程：lin、舞台、驱动脚本（Assets/MV）、菜单（LinSingTools.cs）
songs/          你的歌（不进 git）
docs/           设计步骤、说明长图
AGENTS.md       给所有智能体的说明
```

## 安全与已知问题
- [SECURITY.md](SECURITY.md)：lin-sing 在你的电脑上做什么、哪些内容会联网、本地服务怎么防护、怎么报告安全问题。
- [docs/KNOWN_ISSUES.md](docs/KNOWN_ISSUES.md)：Beta 版的已知问题和解决方法，按安装、Unity、歌词、动作、性能、智能体客户端分类。

## 许可
- 代码：MIT。
- 模型和动作：见 ASSETS.md。
- 注意：对齐用的 MMS_FA 模型仅限非商用。商用时请按 DESIGN.md 换掉这个对齐器。
