@echo off
chcp 65001 >nul
echo.
echo  PCMig L3 - ISO Image List Confirmation
echo  ======================================
echo  This requests administrator rights (UAC).
echo  It only: mounts the ISO, lists install.wim images, dismounts it.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"I:\deepseek work\PCMig\tools\confirm-iso-images.ps1\"' -Verb RunAs"
echo  Elevated window launched. You may close this window.
timeout /t 6 >nul