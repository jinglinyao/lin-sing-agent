@echo off
chcp 65001 >nul
rem lin-sing one-click install (Windows): a Python environment in .venv, PyTorch for your GPU, the pipeline's packages, then the check.
cd /d "%~dp0"
where python >nul 2>&1 || (echo [缺少] 没找到 Python。请先安装 Python 3.10+（https://www.python.org，勾选 Add to PATH）& pause & exit /b 1)
where ffmpeg >nul 2>&1 || (echo [注意] 没找到 ffmpeg，正在用 winget 安装... & winget install -e --id Gyan.FFmpeg)
if not exist .venv (python -m venv .venv || (echo 创建虚拟环境失败 & pause & exit /b 1))
call .venv\Scripts\activate.bat
python -m pip install --upgrade pip
nvidia-smi >nul 2>&1 && (
  echo 检测到 NVIDIA 显卡：安装 CUDA 版 PyTorch
  pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128
) || (
  echo 没有 NVIDIA 显卡：安装 CPU 版 PyTorch（能用，但慢）
  pip install torch torchaudio
)
pip install -r pipeline\requirements.txt || (echo 依赖安装失败 & pause & exit /b 1)
echo.
echo ===== 环境自检（会下载约 4.5 GB 模型，只需一次）=====
python -X utf8 pipeline\linsing.py doctor --fix
echo.
echo 安装完成。以后每次使用先运行：  .venv\Scripts\activate
echo 然后：  cd pipeline  ^&^&  python linsing.py make 你的歌.mp3 --lyrics 歌词.txt
pause
