@echo off
chcp 65001 >nul
echo.
echo  PCMig L3 - Diagnose ^& Fix VM boot order
echo  =========================================
echo  Symptom: "The boot loader did not load an operating system"
echo  This adds the DVD drive to the boot order (first device).
echo  Requests administrator rights (UAC).
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"I:\deepseek work\PCMig\tools\l3-fixboot.ps1\"','-StartAfterFix' -Verb RunAs"
echo  Elevated window launched. You may close this window.
timeout /t 6 >nul