# 资产与授权

| 内容 | 位置 | 来源 | 授权 |
|---|---|---|---|
| 角色 lin | `unity/Assets/MV/Characters/lin.vrm` | 作者 lin2，用 VRoid Studio 制作 | VRM 1.0 许可：允许再分发，允许修改后再分发，允许商用（含企业），不要求署名。详见模型内的 meta 和 https://vrm.dev/licenses/1.0/ |
| 立麦、舞台（网格、材质、预制体） | `unity/Assets/MV/Props/` | 本项目代码生成（`MvPropBuilder.cs`） | MIT |
| 姿势库、手势库、过渡与“活着”层 | `pipeline/lin_sing/choreo.py`、`key_trans.py`、`unity/Assets/MV/Scripts/Gesture*.cs` | 本项目手工制作 | MIT |
| Quaternius Universal Animation Library 1、2（Standard） | `unity/Assets/MV/Motion/Quaternius/` | https://quaternius.com | CC0 1.0（公有领域） |
| BEAT2 动捕片段（14 段，已转换并清洗） | `unity/Assets/MV/Data/Clips/b2_*.json` | BEAT2 数据集（H. Liu 等，PantoMatrix / EMAGE），https://huggingface.co/datasets/H-Liu1997/BEAT2 | Hugging Face 页面标注为 Apache-2.0。**注意**：早期的 BEAT 数据集声明为非商用。商用前请自行核实，拿不准就删掉这些文件，管线不依赖它们。 |

## 没有放进仓库的东西
- **Mixamo 动作**：Adobe 的条款不允许把动作文件单独再分发。需要的话请自己登录 mixamo.com 下载 FBX，放到 `unity/Assets/MV/Motion/Mixamo/`，然后运行 `MvSetup.ImportMixamo()` 和 `MvSetup.BakeClips()`。
- **歌曲**：版权归原作者所有。你自己的音频放在 `songs/` 下，这个目录不进 git。
- **模型权重**：第一次运行时会自动下载：
  - Demucs htdemucs：MIT。
  - Whisper large-v3：MIT。
  - MMS_FA 对齐模型：Meta 发布，CC-BY-NC 4.0，**仅限非商用**。如果要商用，请在 `pipeline/lin_sing/lyrics.py` 里换成其它强制对齐器，换法见 docs/DESIGN.md。

## 用 lin 做视频时
VRM 许可不要求署名。如果你愿意，写一句“角色 lin by lin2 / lin-sing”就是对作者最好的支持。
