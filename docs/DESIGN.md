# lin-sing 设计步骤：每一步做什么、产出什么、怎么换掉

整条管线是“一步一个文件”。每一步只读前面步骤的文件，只写自己的文件。所以任何一步都可以单独重做，或者换成别的模型和工具，只要产出的文件格式不变。

```
音频 ─① 规范化 → song.wav
     ─② 人声分离 → vocals.wav / no_vocals.wav
     ─③ 歌词分句 → lines.json
     ─④ 音节对齐 → morae.json
     ─⑤ 音乐结构 → structure.json
     ─⑥ 面部轨道 → face.json
     ─⑦ 自动编舞 → choreo.json → key.bin + look.txt
     ─⑧ 自动运镜 → show.txt
     ─⑨ Unity 播放 / 渲染 → mp4
```

## ① 规范化：song.wav
- 现在用 ffmpeg 转成 44.1 kHz 双声道 WAV。
- 先检查时长：超过 10 分钟拒绝；超过 5 分钟提示“可能卡顿”。

## ② 人声分离：Demucs htdemucs
- 产出 `sep/htdemucs/song/vocals.wav` 和 `sep/htdemucs/song/no_vocals.wav`。
- 为什么要分离：口型只看人声，律动只看伴奏，混在一起两边都会判断错。
- 怎么换：任何能输出这两个文件的分离器都可以，比如 UVR / MDX、Spleeter、商业 API。只要文件名和路径不变。

## ③ 歌词分句：lines.json
- 格式：`{"lang": "ja", "lines": [{"t": 起始秒, "end": 结束秒, "text": "歌词"}]}`。
- 现在的做法分三种情况：
  - 用户给了带时间的 LRC：直接用。
  - 用户给了纯文本：先用 Whisper 听一遍，再按字母对齐把每句歌词对到时间上（difflib）。
  - 什么都没给：Whisper 自己分句。
- 坑 1：对整首歌跑 Whisper 会跳过整段主歌。所以先按人声能量切出“在唱”的区间，每段不超过 28 秒，逐段识别。
- 坑 2：歌曲的安静处会出现幻觉句，比如“ご視聴ありがとうございました”“字幕由…”，要过滤掉。
- 坑 3：模型已经缓存时用 `local_files_only`，否则在代理环境下联网检查会卡死。
- 怎么换：换成别的识别器（SenseVoice、Paraformer、云端 ASR），或者直接手写 lines.json。

## ④ 音节对齐：morae.json
- 每个音节一条：`[辅音, 元音(a i u e o), 起音时刻, 元音起点, 结束, 置信度]`。
- 现在的做法：
  1. 把每句转成音节：日语用 pykakasi 转假名再拆音拍；中文用 pypinyin 取声母和主元音；英语按元音组切。
  2. 用 MMS_FA（torchaudio）在人声上做强制对齐，一次最多 30 秒的窗口。窗口越长显存越大，这就是长歌会卡的原因。
  3. 低分句子再单独开窗口重对一遍。
  4. 元音持续多久对齐器给不了，改从音量判断。
- 加一种新语言：在 `lyrics.py` 里写一个 `units_xx(text)`，返回 `[(字母串, 辅音, 元音, 元音位置)]`，再登记到 `UNITS`。
- 换对齐器：MMS_FA 只许非商用。可以换成 Montreal Forced Aligner、WhisperX 的对齐、NeMo 等，只要输出同样格式的 morae.json。

## ⑤ 音乐结构：structure.json
- 内容：速度、每一拍的时刻、小节线（按 4/4 拍、低频重音定位第一拍），以及每小节伴奏的低/中/高频响度、鼓、人声。
- 用 librosa 计算（`song_structure.py`）。
- 编舞和运镜都只读这个文件。换成别的节拍检测（madmom、BeatNet、All-In-One 的段落检测）时，按同样的字段输出就行。

## ⑥ 面部轨道：face.json（60 帧/秒）
- 内容：
  - 口型：VRoid 自带的 A I U E O 形变，辅音会压扁元音（m/b/p 闭唇，f/v 咬唇……）。
  - 眨眼。
  - 长音时闭眼。
  - 视线微动。
  - 节拍律动强度。
- 两条实测出来的修正：
  - 嘴提前 67 毫秒，看起来才是“准的”。
  - 眼睛高光永远不隐藏，否则眼神发空。
- 换角色：任何 VRoid 导出的 VRM 都有同名形变，可以直接用。别的模型要在 `make_face.py` 的 `VOWEL`/`CONS` 里把形变名改成它自己的。

## ⑦ 自动编舞：choreo.json → key.bin + look.txt
- `choreo.json` 是编舞表，人和智能体都能直接改：`[{t, end, move, level}]`。
- 从音乐到动作的规则（来自之前人工手 K 的整首歌）：
  - 乐句：紧挨着的几句歌词合成一个乐句，一个乐句只给一个动作。
  - 强度：按伴奏响度在全曲里的分位分档，然后选动作。
    - 安静：贴麦，右手握麦，眼睛看麦。
    - 中等：一只手讲述，另一只手扶麦或扶杆。
    - 饱满：双手打开，挺胸，看观众。
  - 变化：相邻不重复，最近用过的少用；右手握麦约占唱歌时间的三分之一。
  - 时间：动作在乐句的第一个音节到位。每句之前吸气，小节线上换重心，每拍膝盖微沉（伴奏越响越明显）。
- 烘焙：每次换手势都由 `key_trans.bake_sequence` 生成。手的路径和速度：
  - 时长按路程和翻腕角度算。
  - 起得快、落得缓。
  - 走弧线。
  - 先有一个预备动作。
  - 到位后像减震器一样小幅回弹。
  - 手肘先走，手掌和手指后到。
  - 停住的时候仍然有呼吸和细微晃动（`add_life`）。
- 握麦和扶杆的抓、放时间，以及离开时先往哪个方向退，都按实测避免穿模来定。
- 换动作风格：改 `MOVES`（动作库）和 `pose_library`（姿势坐标，单位米，以 lin 的身体为原点）。
- 换角色：在 Unity 里跑 **LinSing / Measure Rig**，重新量握麦点和扶杆点，生成 `data/lin_rig.json`。

## ⑧ 自动运镜：show.txt
- 一台连续移动的摄像机，关键帧落在小节线上。
- 只在强度档位变化的地方换一条路径，两条路径之间用两拍叠化衔接，不用硬切。
- 景别按档位选：
  - 安静：侧面中近景，缓慢推进。
  - 中等：全景到中景，横向弧线移动。
  - 饱满：落拍时拉成远景，再沿弧线移动，每拍轻推一下。
- 换法：写成同样格式的 `cam2/xfade/beat/col` 行就行。

## ⑨ Unity：播放与渲染
- Unity 6 + URP + UniVRM 0.131。所有动作都在 Python 里烘好，Unity 只负责播放，不生成任何动作。
- 菜单：
  - **LinSing / Load Song Folder**：载入一首歌。
  - **Play**：带声音实时播放，用 Unity 自己的播放按钮也可以。
  - **Render Frames**：逐帧渲染。
- 命令行：
  - `-executeMethod LinSingTools.BatchLoad`
  - `-executeMethod LinSingTools.BatchRender`
  - `-executeMethod LinSingTools.BatchSetup`
- 换引擎：key.bin 是 60 帧/秒 × 57 个控制量的 float32 数组，布局见 `keyanim.py` 的 `LAYOUT`。Blender、UE、three.js 都可以写个播放器来读。

## 智能体在哪里
- 管线本身不需要大模型，任何人都能一条命令跑完。
- 智能体（Claude、ChatGPT、DeepSeek、Trae……）做三件事：
  1. 调用管线。
  2. 读 lines.json 和 structure.json，理解歌词情绪，改写 choreo.json。比如“这句歌词说‘我哭了’，要双拳抵胸”。
  3. 按验收清单检查结果，如实汇报。
- 接入方式有三种：AGENTS.md（各家通用的说明文件）、MCP 服务（`pipeline/mcp_server.py`）、函数调用定义（`pipeline/agent_tools.json`）。
