# Beta 版已知问题与解决方法（0.1.0-beta）

先运行 `python linsing.py doctor`，它会逐项检查环境，并给出缺什么、怎么补。下面按出问题的环节分类。

## 安装与网络
| 现象 | 原因 | 解决方法 |
|---|---|---|
| 中国大陆下载模型很慢，或报 `ConnectionError` / 超时 | Hugging Face 在国内不稳定 | 用国内镜像。Windows：`install.bat cn`（pip 用清华源，模型从 hf-mirror.com 下载）；也可以在命令行里先执行 `set HF_ENDPOINT=https://hf-mirror.com`（PowerShell：`$env:HF_ENDPOINT='https://hf-mirror.com'`），再运行 `doctor --fix` 或 `make` |
| `pip install` 很慢或失败 | 访问 PyPI 慢 | `pip install -r pipeline/requirements.txt -i https://pypi.tuna.tsinghua.edu.cn/simple` |
| 克隆仓库或 gh 登录时提示超时 | 访问 GitHub 要走代理，而 git 和 gh 不会自动读取系统代理 | 先设好代理环境变量，例如 `$env:HTTPS_PROXY='http://127.0.0.1:端口'`。推送大文件时如果连接被重置，加上 `git -c http.postBuffer=1048576000 -c http.version=HTTP/1.1 push` |
| 第一次 `make` 卡在“listening to the voice”很久 | 第一次运行要下载约 3 GB 的 Whisper 模型 | 先运行 `doctor --fix` 预先下载；之后都用本地缓存，不再联网检查 |
| `torch` 装成了 CPU 版，或报 CUDA 不可用 | pip 默认装 CPU 版 | 按 https://pytorch.org 选对应的 CUDA 版本重装；`install.bat` 检测到 NVIDIA 显卡时会自动装 CUDA 12.8 版 |
| ffmpeg 报 `No such filter: 'ass'` | ffmpeg 精简版没有带 libass | 换完整版：Windows 用 `winget install Gyan.FFmpeg` |

## Unity
| 现象 | 原因 | 解决方法 |
|---|---|---|
| 打开工程时 Unity 提示版本不同 | 本工程用的是 Unity 6000.3.28f1 | 安装同一版本：[用 Unity Hub 一键安装](unityhub://6000.3.28f1/8f082d2fe476)，或从 [发布页](https://unity.com/releases/editor/whats-new/6000.3.28f1) / [历史版本页](https://unity.com/releases/editor/archive) 下载。其他 6000.3.x 版本一般也能打开，但未经测试 |
| 中国大陆从 unity.cn 装的编辑器 | Unity 中国版的编辑器版本号和国际版不同 | 未经测试。建议优先用国际版 6000.3.28f1；用中国版时选 6000.3 系列最接近的版本，遇到问题请反馈 |
| 第一次打开工程，lin 不显示，或报 `Value cannot be null. Parameter name: Shader` | UniVRM 在着色器准备好之前就导入了模型 | 执行菜单 **LinSing / First-time Setup**，或运行 `Unity.exe -batchmode -quit -projectPath unity -executeMethod LinSingTools.BatchSetup` |
| `Unity 6 not found` | 没找到 Unity | 用 Unity Hub 安装后运行 `doctor`（它会把路径记进 `pipeline/config.json`），或设置环境变量 `LINSING_UNITY` |
| `render` 报 `Unity scripts do not compile` | 某个 C# 脚本有编译错误，Unity 会停在安全模式 | 按报错里的文件和行号修好再渲染。看门狗会自动结束卡住的 Unity，不会一直等下去 |
| `Unity stopped writing` | 渲染中途卡住（多半是显存不够或驱动问题） | 看 `songs/<名字>/unity_render.log`。可以降低分辨率（`render --height 720`）再试，已经渲好的段会保留 |
| `chunk(s) were not written` | Unity 找不到 ffmpeg，或显卡编码器出错 | 运行 `doctor` 确认 ffmpeg 正常；没有 NVENC 会自动改用 libx264（较慢） |
| 提交时场景文件里出现本机路径 | 载入歌曲时 Unity 把路径存进了场景 | 提交前运行 `python linsing.py clean-scene` |

## 歌词、口型、字幕
| 现象 | 原因 | 解决方法 |
|---|---|---|
| 歌词有漏句，或出现“ご視聴ありがとうございました”“字幕由…”这类句子 | Whisper 在歌曲里会漏听，也会在安静处凭空“听”出句子 | **尽量自己提供歌词**（`--lyrics`）。程序已经按人声区间逐段识别，并过滤了常见的幻觉句，但不能保证全对 |
| 质检提示“低置信度的句子”“对齐失败、音节均匀铺开的句子” | 嘶吼、假声、叠唱，或者歌词和这一版录音对不上（翻唱改词、漏了一段） | 核对歌词；有带时间的 LRC 就用 LRC。这些句子的口型只是近似 |
| 不给歌词时，日语字幕有时把一个词拆到两行 | 日语没有空格，断句靠停顿和简单规则判断 | 提供歌词即可解决 |
| 字幕里有方块字 | 自带字体 Noto Sans SC 只覆盖简中、日文假名和汉字、拉丁字母 | 韩文等其他文字，请换一个能覆盖的字体，并修改 `subtitles.py` 里的 `FONT` |
| 中文歌 | 拼音音节的对齐流程已经写好，但还没有用真实中文歌完整测过 | 欢迎反馈问题；提供歌词会准确得多 |
| Unity 实时播放时字幕不显示或显示成方块 | 实时字幕用的是系统里的中日文字体，还没有完整验证过 | 渲染出的视频字幕是用 ffmpeg 烧进画面的，这条路已经验证可靠 |

## 动作与镜头
| 现象 | 原因 | 解决方法 |
|---|---|---|
| 深度质检（`qa --deep`）报出个别单帧手擦到话筒（几毫米到 1–2 厘米） | 手在胸口附近时离话筒尾部很近 | 在 `choreo.json` 或网页编辑器里，把报出时间点附近的动作换成别的（例如 `hand_chest` 换成 `mic_chest`），再 `bake` |
| 质检提示“动作挤在一起（SQUEEZED）” | 两条动作离得太近，前一个还没做完 | 把后一条往后挪，或删掉一条 |
| 动作和歌词情绪不太搭 | 自动编舞只看段落强弱，不懂歌词的意思 | 让智能体读歌词后改 `choreo.json`，或者用网页编辑器手动改 |
| 段落切换时镜头移动比较快 | 换段落时，镜头会在一小节内移到新景别 | 编辑 `show.txt` 里的 `cam2` 关键帧，让镜头移动得更平缓 |

## 性能
| 现象 | 原因 | 解决方法 |
|---|---|---|
| 五分钟以上的音频处理时电脑卡顿 | 人声分离、对齐、烘焙的耗时和显存都随长度增长 | 能跑完，只是慢；也可以先把歌切成几段。超过 10 分钟会被直接拒绝 |
| `CUDA out of memory` | 显存不够 | 关掉其他占显卡的程序；改用小一点的识别模型 `make ... --asr-model small`；或者把歌切段 |
| 渲染比文档里写的慢很多 | 没有 NVENC 显卡编码器，或者显卡比较弱 | 先用 `render --from --to` 渲染 720p 片段检查，最后再出整首 |

## 智能体客户端
| 现象 | 原因 | 解决方法 |
|---|---|---|
| Kimi Code 桌面版里看不到 lin-sing 的工具 | 没有信任这个文件夹，或会话是在配置之前打开的 | 用 Kimi Code 打开仓库根目录，在信任提示里核对命令后选“信任”，然后**新建会话**（已经打开的会话不会加载新的 MCP 服务） |
| Kimi / Claude 里的 MCP 服务启动失败 | `python` 不在 PATH 上，或客户端没有从仓库根目录启动它 | 把 `.kimi-code/mcp.json`（或 `.mcp.json`）里的 `command` 改成 Python 的完整路径，`args` 改成 `mcp_server.py` 的完整路径 |
| `make` / `render` 调用超时 | 一首歌要处理好几分钟 | `.kimi-code/mcp.json` 已经把单次调用超时设为 1 小时（`toolTimeoutMs`）；其他客户端要在各自的设置里调大超时 |
| Kimi Code 桌面版提示需要会员 | 没有订阅 Kimi 会员的账号不能使用官方模型 | 订阅会员，或在设置里接入自己的模型供应商 |
| 网页编辑器操作时报 403 | 用了其他地址打开页面，或服务重启后令牌变了 | 用命令行里打印出的 `http://127.0.0.1:端口/` 打开，服务重启后刷新页面 |
