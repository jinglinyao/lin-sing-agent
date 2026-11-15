---
name: lin-sing
description: 让 3D 角色 lin 唱一首歌：分离人声、对齐歌词、自动编舞运镜、出带字幕的视频，并按质检报告修改
whenToUse: 用户给了一首歌（音频文件）或歌词，想让 lin 唱、改 lin 的动作 / 表情 / 镜头、渲染预览或成片、查看质检结果时
arguments: audio
---

# lin-sing 工作流程

完整说明在仓库根目录的 `AGENTS.md`，命令都在 `pipeline/` 下运行。支持 MCP 时优先调用 `lin-sing` 服务的工具（`linsing_check`、`linsing_make`、`linsing_get_choreo`、`linsing_set_choreo`、`linsing_render`）；不支持时用下面的命令。

1. **检查长度**：`python linsing.py check $audio`。
   - 超过 10 分钟会被拒绝。
   - 超过 5 分钟时告诉用户：一次输入五分钟以上的音频可能会卡顿，建议切成几段。
2. **歌词**：用户给了歌词，就原样存成 `songs/<名字>/lyrics.txt`（一行一句，要显示译文就写成 `原文 | 译文`）。没有歌词就不加 `--lyrics`。
3. **生成**：`python linsing.py make $audio --name <名字> [--lyrics ../songs/<名字>/lyrics.txt] [--mood neutral|sad|bright]`。
4. **看质检**：打开 `songs/<名字>/qa.md`，有 ✗ 就按建议修改 `choreo.json`，再运行 `python linsing.py bake ../songs/<名字>`，直到没有 ✗。
5. **预览**：`python linsing.py render ../songs/<名字> --from 60 --to 90`（720p，30 秒），请用户看过再继续。
6. **成片**：`python linsing.py render ../songs/<名字>`。交付前跑一次 `python linsing.py qa ../songs/<名字> --deep`。
7. **汇报**：如实告诉用户哪些检查没过、哪些地方只是近似（低置信度的歌词、只能均匀铺开的句子）。

不要提交 `songs/` 里的任何文件；提交代码前先运行 `python linsing.py clean-scene`。
