"""Environment check for lin-sing: everything the pipeline and the Unity render need, with a fix for each problem.
`python linsing.py doctor` checks; `python linsing.py doctor --fix` also downloads the models (once, ~4.5 GB in all)."""
import os, sys, shutil, subprocess, importlib, glob, json

OK, BAD, WARN = "[ok]  ", "[缺少]", "[注意]"
HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(os.path.dirname(HERE))


def run(log=print, fix=False):
    problems = 0
    def say(state, what, hint=""):
        nonlocal problems
        if state == BAD: problems += 1
        log(f"{state} {what}" + (f"\n        -> {hint}" if hint and state != OK else ""))

    v = sys.version_info
    say(OK if v >= (3, 10) else BAD, f"Python {v.major}.{v.minor}", "需要 Python 3.10 或更新的版本")
    for tool in ("ffmpeg", "ffprobe"):
        p = shutil.which(tool); say(OK if p else BAD, f"{tool}: {p or '没找到'}", "安装 ffmpeg 并加入 PATH（Windows: winget install Gyan.FFmpeg）")
    if shutil.which("ffmpeg"):
        f = subprocess.run(["ffmpeg", "-hide_banner", "-filters"], capture_output=True, text=True).stdout
        say(OK if " ass " in f else BAD, "ffmpeg 带 libass（字幕烧录）", "换一个完整版 ffmpeg（gyan.dev full build）")
        e = subprocess.run(["ffmpeg", "-hide_banner", "-encoders"], capture_output=True, text=True).stdout
        say(OK if "h264_nvenc" in e else WARN, "ffmpeg 支持 NVENC 显卡编码" if "h264_nvenc" in e else "ffmpeg 没有 NVENC：渲染会用 CPU 编码，慢一些")
    try:
        import torch
        cuda = torch.cuda.is_available()
        say(OK if cuda else WARN, f"torch {torch.__version__}, CUDA {'可用: ' + torch.cuda.get_device_name(0) if cuda else '不可用'}",
            "没有 NVIDIA 显卡也能跑，但人声分离和对齐会慢很多；有显卡请按 https://pytorch.org 安装 CUDA 版 torch")
        if cuda:
            mem = torch.cuda.get_device_properties(0).total_memory / 2 ** 30
            say(OK if mem >= 6 else WARN, f"显存 {mem:.1f} GB", "显存少于 6 GB：Whisper 会自动换小模型，长歌建议切段")
    except Exception as ex:
        say(BAD, f"torch 导入失败: {ex}", "pip install torch torchaudio（按显卡选版本）")
    for mod, pipname in (("torchaudio", "torchaudio"), ("demucs", "demucs"), ("faster_whisper", "faster-whisper"), ("librosa", "librosa"),
                         ("soundfile", "soundfile"), ("scipy", "scipy"), ("pykakasi", "pykakasi"), ("pypinyin", "pypinyin")):
        try: importlib.import_module(mod); say(OK, f"python 包 {pipname}")
        except Exception: say(BAD, f"python 包 {pipname}", f"pip install {pipname}")
    # models
    hf = os.environ.get("HF_HOME", os.path.join(os.path.expanduser("~"), ".cache", "huggingface"))
    th = os.environ.get("TORCH_HOME", os.path.join(os.path.expanduser("~"), ".cache", "torch"))
    has_w = bool(glob.glob(os.path.join(hf, "hub", "models--Systran--faster-whisper-large-v3")))
    has_m = bool(glob.glob(os.path.join(th, "hub", "checkpoints", "model.pt")))
    has_d = bool(glob.glob(os.path.join(th, "hub", "checkpoints", "955372bf-*.th")) or glob.glob(os.path.join(hf, "hub", "models--adefossez--HTDemucs")))
    say(OK if has_w else WARN, f"Whisper large-v3 模型{'已缓存' if has_w else '未下载（约 3 GB）'}（{hf}）", "运行 doctor --fix 预先下载，或第一次 make 时自动下载")
    say(OK if has_m else WARN, f"MMS_FA 对齐模型{'已缓存' if has_m else '未下载（约 1.2 GB）'}（{th}）", "运行 doctor --fix")
    say(OK if has_d else WARN, f"Demucs htdemucs 模型{'已缓存' if has_d else '未下载（约 80 MB）'}", "运行 doctor --fix")
    if fix and not (has_w and has_m and has_d):
        log("下载模型（只需一次）...")
        try:
            if not has_m:
                import torchaudio; torchaudio.pipelines.MMS_FA.get_model(); log("  MMS_FA ok")
            if not has_d:
                from demucs.pretrained import get_model; get_model("htdemucs"); log("  htdemucs ok")
            if not has_w:
                from faster_whisper import WhisperModel; WhisperModel("large-v3", device="cpu", compute_type="int8"); log("  Whisper large-v3 ok")
        except Exception as ex: say(BAD, f"下载失败: {ex}", "检查网络 / 代理；也可以手动设 HF_ENDPOINT 镜像")
    # unity and the project
    want = open(os.path.join(ROOT, "unity", "ProjectSettings", "ProjectVersion.txt")).read().split("m_EditorVersion:")[1].split()[0]
    sys.path.insert(0, os.path.dirname(HERE)); import linsing
    u, how = linsing.find_unity()
    if u and how != "LINSING_UNITY":                                       # remember it, so no environment variable is needed later
        cfg = os.path.join(os.path.dirname(HERE), "config.json"); c = json.load(open(cfg, encoding="utf-8")) if os.path.exists(cfg) else {}
        if c.get("unity") != u: c["unity"] = u; json.dump(c, open(cfg, "w", encoding="utf-8"), indent=1); log(f"        (found through {how}; saved to pipeline/config.json)")
    say(OK if u and os.path.exists(u) else BAD, f"Unity: {u or '没找到'}（工程版本 {want}）", f"用 Unity Hub 安装 {want}，并把环境变量 LINSING_UNITY 设成 Unity.exe 的路径")
    have = None
    if u and os.path.exists(u) and os.name == "nt":                     # the editor's own file version (the path need not carry it)
        r = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Item '{u}').VersionInfo.ProductVersion"], capture_output=True, text=True)
        have = (r.stdout.strip().split("_")[0] or None) if r.returncode == 0 else None
    elif u: have = next((part for part in u.replace("\\", "/").split("/") if part.startswith("6000.")), None)
    if have and have != want: say(WARN, f"你的 Unity 是 {have}，工程是 {want}", "版本不同通常也能打开，但会重新导入；遇到问题请装同一版本")
    elif have: say(OK, f"Unity 版本一致（{have}）")
    rig = os.path.join(HERE, "data", "lin_rig.json")
    say(OK if os.path.exists(rig) else BAD, "lin 的尺寸表 data/lin_rig.json", "在 Unity 里执行菜单 LinSing / First-time Setup")
    say(OK if os.path.exists(os.path.join(ROOT, "pipeline", "fonts", "NotoSansSC.ttf")) else BAD, "字幕字体 fonts/NotoSansSC.ttf", "重新克隆仓库（字体随仓库提供）")
    if not os.path.isdir(os.path.join(ROOT, "unity", "Library")): say(WARN, "Unity 工程还没导入过", "第一次用 Unity 打开 unity/ 要几分钟，然后执行 LinSing / First-time Setup")
    free = shutil.disk_usage(ROOT).free / 2 ** 30
    say(OK if free > 15 else WARN, f"磁盘剩余 {free:.0f} GB", "整首 1080p 渲染的临时数据和视频约需 3–10 GB")
    log(("\n全部就绪。" if problems == 0 else f"\n有 {problems} 项必须先解决。") + "  （[注意] 不影响使用，只是会慢或要先下载）")
    return problems
