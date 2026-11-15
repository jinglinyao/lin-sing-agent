@echo off
chcp 65001 >nul
title lin-sing
cd /d "%~dp0"
"%~dp0runtime\python\python.exe" -s -X utf8 "%~dp0pipeline\launcher.py"
if errorlevel 1 pause
