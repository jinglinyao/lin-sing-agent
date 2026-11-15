"""What 启动 lin-sing.bat runs: checks this computer, then opens lin-sing in the browser.

lin-sing serves Windows 10/11 64-bit computers with an NVIDIA graphics card (RTX 20 / GTX 16 series or newer, 6 GB video memory or more),
16 GB memory. A computer below that is told so plainly - there is no slow fallback.
The graphics card is judged by PyTorch itself (it is what has to run on it): model, video memory, generation, and that CUDA works at all.
"""
import os, sys, subprocess, shutil, ctypes, socket, platform, json
HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import linsing                                  # sets up the package's offline environment (models, ffmpeg, caches)

OK, NOTE = "  [通过]", "  [提示]"
MIN_VRAM_GB, MIN_RAM_GB, MIN_CC = 5.7, 15.0, (7, 5)
GPU_PROBE = ("import json, torch\n"
             "if torch.cuda.is_available():\n"
             "    p = torch.cuda.get_device_properties(0)\n"
             "    print(json.dumps(dict(ok=True, name=p.name, vram=p.total_memory / 2**30, cc=[p.major, p.minor], cuda=torch.version.cuda)))\n"
             "else: print(json.dumps(dict(ok=False)))\n")


def say(*a): print(*a, flush=True)


def stop(msg):
    say("\n" + "=" * 60 + "\n" + msg + "\n" + "=" * 60)
    say("\n按回车键关闭这个窗口。"); sys.exit(1)


def video_controllers():
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-CimInstance Win32_VideoController).Name"], capture_output=True, text=True, timeout=30)
        return [x.strip() for x in r.stdout.splitlines() if x.strip()]
    except Exception: return []


def check():
    say("lin-sing 正在检查这台电脑……\n")
    if platform.system() != "Windows" or sys.maxsize < 2 ** 32: stop("lin-sing 只支持 64 位的 Windows 10 / 11。")
    say(OK, "Windows 64 位")
    # Microsoft Visual C++ runtime (PyTorch cannot even load without it)
    sysdir = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
    if not all(os.path.exists(os.path.join(sysdir, f)) for f in ("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll")):
        vc = os.path.join(ROOT, "runtime", "vc_redist.x64.exe")
        say(NOTE, "缺少 Microsoft Visual C++ 运行库，正在打开安装程序（会弹出一个需要你点“是”的窗口）……")
        if os.path.exists(vc): subprocess.run([vc, "/install", "/passive", "/norestart"])
        if not os.path.exists(os.path.join(sysdir, "vcruntime140_1.dll")): stop("Visual C++ 运行库没有装上。请双击 runtime 文件夹里的 vc_redist.x64.exe 安装后再启动。")
    say(OK, "Visual C++ 运行库")
    # the graphics card, as PyTorch sees it
    say("    正在检查显卡（第一次大约要 10–60 秒）……")
    r = subprocess.run([sys.executable, "-s", "-c", GPU_PROBE], capture_output=True, text=True)
    try: g = json.loads(r.stdout.strip().splitlines()[-1])
    except Exception: g = dict(ok=False)
    if not g.get("ok"):
        cards = video_controllers(); nv = any("NVIDIA" in c.upper() for c in cards); amd = any(k in c.upper() for c in cards for k in ("AMD", "RADEON"))
        if nv: stop("检测到 NVIDIA 显卡，但它现在不能运行 AI 模型，通常是驱动太旧。请到 nvidia.cn 下载安装最新的显卡驱动，重启电脑后再试。"
                    + (f"\n（技术信息：{(r.stderr or '').strip()[-300:]}）" if r.stderr.strip() else ""))
        stop("没有检测到 NVIDIA 显卡。" + (f"\n这台电脑的显卡：{'、'.join(cards)}" if cards else "") +
             ("\nAMD 显卡版本还在测试中，暂时不能用。" if amd else "") +
             "\nlin-sing 需要 NVIDIA RTX 20 系 / GTX 16 系或更新的显卡，显存 6 GB 以上。")
    if tuple(g["cc"]) < MIN_CC: stop(f"显卡 {g['name']} 太老了。lin-sing 需要 NVIDIA RTX 20 系 / GTX 16 系或更新的显卡。")
    if g["vram"] < MIN_VRAM_GB: stop(f"显卡 {g['name']} 只有 {g['vram']:.0f} GB 显存，lin-sing 至少需要 6 GB。")
    say(OK, f"显卡 {g['name']}，显存 {g['vram']:.0f} GB，可以运行 AI 模型")
    # memory and disk
    class MS(ctypes.Structure):
        _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong), ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong),
                    ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
    ms = MS(); ms.dwLength = ctypes.sizeof(MS); ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(ms)); ram = ms.ullTotalPhys / 2 ** 30
    if ram < MIN_RAM_GB: stop(f"这台电脑内存只有 {ram:.0f} GB，lin-sing 至少需要 16 GB。")
    say(OK, f"内存 {ram:.0f} GB")
    free = shutil.disk_usage(ROOT).free / 2 ** 30
    if free < 5: stop(f"lin-sing 所在的磁盘只剩 {free:.1f} GB，至少要留 5 GB（建议 10 GB 以上）给作品和临时文件。")
    say(OK if free >= 10 else NOTE, f"磁盘剩余 {free:.0f} GB" + ("" if free >= 10 else "（建议 10 GB 以上）"))
    if not os.path.exists(os.path.join(ROOT, "player", "LinSing.exe")): stop("缺少 player\\LinSing.exe：程序包不完整，请重新解压整个压缩包。")
    if not os.path.isdir(os.path.join(ROOT, "models")): stop("缺少 models 文件夹：程序包不完整，请重新解压整个压缩包。")


def free_port(start=8765):
    for p in range(start, start + 40):
        with socket.socket() as s:
            try: s.bind(("127.0.0.1", p)); return p
            except OSError: continue
    return start


if __name__ == "__main__":
    check()
    port = free_port()
    say(f"\n一切就绪。lin-sing 已经在浏览器里打开：http://127.0.0.1:{port}/")
    say("用完以后直接关掉这个黑色窗口就行。处理歌曲时请不要关闭它。\n")
    from lin_sing import studio
    studio.serve(os.path.join(ROOT, "songs"), port, True)
