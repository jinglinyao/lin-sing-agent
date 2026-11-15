# AGENTS.md — 给智能体看的说明（Claude、ChatGPT/Codex、DeepSeek、Trae、Cursor 等通用）

这个仓库让 3D 角色 lin 唱任意一首歌：口型对上歌词音节，身体动作跟上音乐。你（智能体）的工作是**调用管线、读结果、改编舞表、验收**。不要手写动画曲线，也不要改 Unity 场景里的数值，除非用户明确要求。

## 环境
- Python 3.10+，`pip install -r pipeline/requirements.txt`；系统里要有 `ffmpeg` / `ffprobe`。
- 有 NVIDIA 显卡最好（Demucs、Whisper、MMS_FA 都会用 CUDA）；没有也能跑，只是慢。
- Unity 6（6000.3.x）。环境变量 `LINSING_UNITY` 指向 `Unity.exe`。
- 一键安装：Windows 运行 `install.bat`，macOS / Linux 运行 `install.sh`。装完会自动执行 `doctor --fix`。
- 第一次用：在 Unity 里打开 `unity/`，执行菜单 **LinSing / First-time Setup**；或者在命令行运行：
  `Unity.exe -batchmode -quit -projectPath unity -executeMethod LinSingTools.BatchSetup`。

## 常用命令（都在 `pipeline/` 下运行）
| 目的 | 命令 |
|---|---|
| 检查音频长度 | `python linsing.py check <音频>` |
| 一首歌从头做到可播放 | `python linsing.py make <音频> [--lyrics 歌词.txt/.lrc] [--lang ja/zh/en] [--name 名字] [--mood neutral/sad/bright]` |
| 改完编舞表后重烘 | `python linsing.py bake ../songs/<名字>` |
| 载入 Unity（编辑器需关闭） | `python linsing.py load ../songs/<名字>` |
| 渲染成 mp4（带字幕） | `python linsing.py render ../songs/<名字>` |
| 只渲染一段预览（720p） | `python linsing.py render ../songs/<名字> --from 60 --to 90` |
| 质检（几秒） | `python linsing.py qa ../songs/<名字>`（`bake` 结束时也会自动跑） |
| 深度质检（Unity 逐帧穿模扫描，几分钟） | `python linsing.py qa ../songs/<名字> --deep` |
| 网页时间轴编辑器 | `python linsing.py studio ../songs/<名字>` |
| 环境自检 / 下载模型 | `python linsing.py doctor [--fix]` |

## 用户给歌词时（推荐）
用户把歌词发给你时，原样存成 `songs/<名字>/lyrics.txt`（UTF-8，一行一句，保持原来的分行），再运行 `make --lyrics`。
- 有译文就写成 `原文 | 译文`，字幕会在原文下面多显示一行小字译文。双语 LRC（同一时间戳写两行）也能识别。
- 有带时间的 LRC 就直接用 LRC。
- 不给歌词时，程序会用 Whisper 去听，但同音字、断句难免出错。
- 字幕：渲染出的视频会把字幕烧在画面下方；Unity 实时播放时也会在下方显示。
- 跑完要看 `make` 的输出：
  - `lines placed (N not heard clearly ...)`：有 N 句没听清，是按前后句插进去的；
  - `could not place`：这些句子的音节只是均匀铺开。
  
  出现这两种情况，常见原因是歌词和这一版录音对不上（翻唱改词、漏了一段），要告诉用户。

**音频限制**：最长 10 分钟，超过会被拒绝。超过 5 分钟时要提醒用户：一次处理五分钟以上的音频，电脑可能会卡顿（分离、对齐、烘焙的耗时和显存都随长度增长）；可以先切成几段。

## 编舞表 `songs/<名字>/choreo.json`
`sheet` 是一串动作：`{"t": 起始秒, "end": 结束秒, "move": 动作名, "level": -1 无人声 / 0 安静 / 1 中等 / 2 饱满}`。
可用的动作名在 `moves` 里，含义见 `pipeline/lin_sing/choreo.py` 的 `MOVES`：

- 安静：`mic`（右手握麦、左手扶杆、看麦）、`mic_chest`、`mic_hand`（看左手掌心）、`mic_bow`（俯身贴麦）、`hand_chest`（右手抚胸）、`hand_look`（看右手掌心）。
- 中等：`explain`、`sweep`、`offer`、`point`、`press`、`shrug`、`reachlow`、`open_down`、`fists`。
- 饱满：`wide`、`reach`、`fist_open`、`offer2`、`mic_wide`。
- 无人声：`rest`、`rest_open`、`rest_far`、`rest_lean`。

每条动作还可以加 `"face"` 指定表情（`calm` `tender` `sad` `plead` `pain` `anger` `fear` `shut` `joy` `bright`），以及 `"face_strength"`（0–1）。整首歌的基调在顶层 `mood`：`neutral`、`sad` 或 `bright`。

改编舞的规则：
- 一个乐句只放一个动作，相邻两条不要用同一个动作。
- 两条动作之间至少隔 1.8 秒（更短的会被合并）。
- 右手握麦的时间约占唱歌时间的三分之一。
- 动作要贴合歌词情绪。歌词在 `lines.json`，每句的响度在 `structure.json` 的 `bars[].total`。

改完运行 `bake`。MCP 客户端可以直接用 `linsing_set_choreo`。乐句里的小拍子、长停留时的漂移、点头和呼吸，都是烘焙时自动加的，不用写进编舞表。

## 镜头与灯光 `songs/<名字>/show.txt`
`bake` 自动生成，也可以手改，改完不用重烘，直接重新渲染：
- `cam2 t 偏航 距离 抬高 仰俯 拍子推镜`：摄像机关键帧。整首歌只有一条连续路径，不要加 `xfade`：两个景别不同的镜头叠化，人物会出现重影。
- `col t r g b 主光`：背景色。
- `tint t r g b`：轮廓光颜色。
- `lit t 主光 轮廓光 顶光 补光`：各盏灯的强度。
- `beat t ...`：副歌里随拍子轻推镜头、灯光呼吸。

## 验收（交付前必须做）
0. 质检报告在 `qa.md` / `qa.json`。每一项都有结果、时间点和建议：先按建议改，`bake` 后再看，直到没有 ✗。交付前至少跑一次 `qa --deep`（检查真实网格穿模）。
1. 看 `make` / `bake` 的输出：
   - `SQUEEZED` 表示两个动作挤得太近，要拉开时间。
   - `low confidence` 表示那几句对齐不准，多半是嘶吼、假声或歌词不对。
2. 先用 `render --from --to` 渲染 20–30 秒预览检查，再出整首。按正常速度看成片，重点看：
   - 嘴是否和字对上，字幕是否和唱的对得上；
   - 手有没有穿进身体或话筒；
   - 镜头有没有跳。
3. 动作数据：`python tools/motion_stats.py ../songs/<名字>`。
   - 参考值：手速最快不超过约 0.8 m/s，头部最快不超过约 60°/s。
   - 右手握麦约 30–35%。
4. 如实向用户报告没检查到的部分，不要只看日志就说“完成”。
5. 调参数时，必须用至少两首风格不同的歌检验，不能只看一首。

## 不要做的事
- 不要把歌曲音频、渲染出的帧或视频提交进仓库（`songs/` 已在 .gitignore 里）。
- `render` 是分段渲染的（每段 20 秒，缓存在 `songs/<名字>/chunks/`）。改了编舞再渲染时只重渲受影响的段，不要手动删缓存。
- 提交前先运行 `python linsing.py clean-scene`：载入歌曲时，场景文件会记下本机的完整路径，要先清掉。
- 不要把 Mixamo 动作文件放进仓库（Adobe 条款不允许再分发）。
- 不要删除 `unity/Library` 以外的 Unity 生成文件。

## 用 MCP 驱动（推荐给支持 MCP 的客户端）
stdio 服务：`python pipeline/mcp_server.py`，提供以下工具：
- `linsing_check`
- `linsing_make`
- `linsing_get_choreo`
- `linsing_set_choreo`
- `linsing_render`

不支持 MCP、只支持函数调用的模型（OpenAI / DeepSeek API）用 `pipeline/agent_tools.json` 里的工具定义，执行时调用对应的 `linsing.py` 命令即可。
