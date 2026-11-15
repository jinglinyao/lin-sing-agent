# lin-sing：让智能体驱动 3D 角色 lin 唱任何一首歌

![lin-sing 说明长图](docs/lin-sing-guide.png)

给一段音频（10 分钟以内），lin 就会站到立麦前把它唱出来：
- 嘴对上每一个音节；
- 眨眼、闭眼、视线跟着乐句走；
- 手势、重心、呼吸跟着歌词情绪和音乐的拍子、小节、强弱走；
- 摄像机沿着小节线连续运镜，舞台灯光随段落变化；
- 你提供的歌词（可以附上译文）会对齐到每一句，作为字幕显示在屏幕下方。

全部在你自己的电脑上运行，不按秒收费，也不需要上传你的歌。

> English: lin-sing makes a VRM character (lin) sing any song up to 10 minutes. It separates the vocals, aligns every syllable, builds a lip-sync and face track, analyses tempo, bars and loudness, writes an editable choreography, bakes the body animation and the camera, and renders in Unity 6. Agents (Claude, ChatGPT/Codex, DeepSeek, Trae, Cursor…) drive it through `AGENTS.md`, an MCP server, or function-calling definitions.

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
1. 安装依赖：
   - Python 3.10+、ffmpeg。
   - PyTorch：按你的显卡版本安装。
   - 然后运行 `pip install -r pipeline/requirements.txt`。
2. 安装 Unity 6（6000.3.x），设置环境变量 `LINSING_UNITY`，指向 `Unity.exe`。
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
7. 出整首视频：`python linsing.py render ../songs/我的歌`，会生成 `songs/我的歌/我的歌.mp4`，字幕已经烧在画面里。

**时长限制**：
- 音频最长 10 分钟。
- 一次输入 5 分钟以上的音频，电脑可能会卡顿：人声分离、对齐、动作烘焙的耗时和显存都随长度增长。程序能跑完，只是慢；也可以先把歌切成几段。

## 用智能体驱动
| 智能体 | 接入方式 |
|---|---|
| Claude Code / Claude Desktop | 读 `CLAUDE.md`，它指向 `AGENTS.md`；也可以注册 MCP 服务 `python pipeline/mcp_server.py` |
| ChatGPT / Codex | 读 `AGENTS.md`；支持 MCP 的客户端注册同一个服务；只用 API 时用 `pipeline/agent_tools.json` 做函数调用 |
| DeepSeek | 在任何能读 `AGENTS.md` 或接 MCP 的工具里用（OpenCode、Cline、兼容 Claude Code 的接口等）；直接用 API 时同样用 `agent_tools.json` |
| Trae | 读 `.trae/rules/project_rules.md`，它指向 `AGENTS.md`；在 Trae 里添加 MCP 服务 |
| Cursor / Copilot | 读 `.cursor/rules/`、`.github/copilot-instructions.md` |

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

## 许可
- 代码：MIT。
- 模型和动作：见 ASSETS.md。
- 注意：对齐用的 MMS_FA 模型仅限非商用。商用时请按 DESIGN.md 换掉这个对齐器。
