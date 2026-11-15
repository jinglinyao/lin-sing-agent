#!/usr/bin/env bash
# lin-sing one-click install (macOS / Linux): a Python environment in .venv, PyTorch, the pipeline's packages, then the check.
set -e
cd "$(dirname "$0")"
# bash install.sh cn : mainland China mirrors (pip from Tsinghua, models from hf-mirror.com)
if [ "${1:-}" = "cn" ]; then echo "使用国内镜像：pip 清华源，模型 hf-mirror.com"; export PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple HF_ENDPOINT=https://hf-mirror.com; fi
command -v python3 >/dev/null || { echo "[缺少] 没找到 python3，请先安装 Python 3.10+"; exit 1; }
command -v ffmpeg >/dev/null || echo "[注意] 没找到 ffmpeg：macOS 用 brew install ffmpeg，Ubuntu 用 sudo apt install ffmpeg"
[ -d .venv ] || python3 -m venv .venv
source .venv/bin/activate
python -m pip install --upgrade pip
if command -v nvidia-smi >/dev/null; then pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128; else pip install torch torchaudio; fi
pip install -r pipeline/requirements.txt
echo "===== 环境自检（会下载约 4.5 GB 模型，只需一次）====="
python -X utf8 pipeline/linsing.py doctor --fix || true
echo "安装完成。以后先运行: source .venv/bin/activate ；然后: cd pipeline && python linsing.py make 你的歌.mp3 --lyrics 歌词.txt"
