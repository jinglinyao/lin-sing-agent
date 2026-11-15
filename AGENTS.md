# AGENTS.md — 给智能体看的说明（Claude、ChatGPT/Codex、DeepSeek、Trae、Cursor 等通用）

这个仓库让 3D 角色 lin 唱任意一首歌：口型对上歌词音节，身体动作跟上音乐。你（智能体）的工作是**调用管线、读结果、改编舞表、验收**。不要手写动画曲线，也不要改 Unity 场景里的数值，除非用户明确要求。

## 环境
- Python 3.10+，`pip install -r pipeline/requirements.txt`；系统里要有 `ffmpeg` / `ffprobe`。
- 有 NVIDIA 显卡最好（Demucs、Whisper、MMS_FA 都会用 CUDA）；没有也能跑，只是慢。
- Unity 6（6000.3.x）。环境变量 `LINSING_UNITY` 指向 `Unity.exe`。
- 第一次用：在 Unity 里打开 `unity/`，执行菜单 **LinSing / First-time Setup**；或者在命令行运行：
  `Unity.exe -batchmode -quit -projectPath unity -executeMethod LinSingTools.BatchSetup`。

## 常用命令（都在 `pipeline/` 下运行）
| 目的 | 命令 |
|---|---|
| 检查音频长度 | `python linsing.py check <音频>` |
| 一首歌从头做到可播放 | `python linsing.py make <音频> [--lyrics 歌词.lrc/.txt] [--lang ja/zh/en] [--name 名字]` |
| 改完编舞表后重烘 | `python linsing.py bake ../songs/<名字>` |
| 载入 Unity（编辑器需关闭） | `python linsing.py load ../songs/<名字>` |
| 渲染成 mp4 | `python linsing.py render ../songs/<名字>` |

**音频限制**：最长 10 分钟，超过会被拒绝。超过 5 分钟时要提醒用户：一次处理五分钟以上的音频，电脑可能会卡顿（分离、对齐、烘焙的耗时和显存都随长度增长）；可以先切成几段。

## 编舞表 `songs/<名字>/choreo.json`
`sheet` 是一串动作：`{"t": 起始秒, "end": 结束秒, "move": 动作名, "level": -1 无人声 / 0 安静 / 1 中等 / 2 饱满}`。
可用的动作名在 `moves` 里，含义见 `pipeline/lin_sing/choreo.py` 的 `MOVES`：

- 安静：`mic`（右手握麦、左手扶杆、看麦）、`mic_chest`、`mic_hand`（看左手掌心）、`mic_bow`（俯身贴麦）。
- 中等：`explain`、`sweep`、`offer`、`point`、`press`、`shrug`、`reachlow`、`open_down`、`fists`。
- 饱满：`wide`、`reach`、`fist_open`、`offer2`、`mic_wide`。
- 无人声：`rest`、`rest_open`。

改编舞的规则：
- 一个乐句只放一个动作，相邻两条不要用同一个动作。
- 两条动作之间至少隔 1.6 秒。
- 右手握麦的时间约占唱歌时间的三分之一。
- 动作要贴合歌词情绪。歌词在 `lines.json`，每句的响度在 `structure.json` 的 `bars[].total`。

改完运行 `bake`。MCP 客户端可以直接用 `linsing_set_choreo`。

## 验收（交付前必须做）
1. 看 `make` / `bake` 的输出：
   - `SQUEEZED` 表示两个动作挤得太近，要拉开时间。
   - `low confidence` 表示那几句对齐不准，多半是嘶吼、假声或歌词不对。
2. 渲染后按正常速度看成片，重点看：嘴是否和字对上、手有没有穿进身体或话筒、镜头有没有跳。
3. 如实向用户报告没检查到的部分，不要只看日志就说“完成”。

## 不要做的事
- 不要把歌曲音频、渲染出的帧或视频提交进仓库（`songs/` 已在 .gitignore 里）。
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
