@echo off
chcp 65001 >nul
echo.
echo  PCMig L3 - One-click Deploy (create 3 VMs + auto-answer + attach ISO)
echo  ======================================================================
echo  This requests administrator rights (UAC). Click YES.
echo  It creates ONLY objects named "PCMigLab-*" and does not touch other VMs.
echo.
echo  Dry run first? Press Ctrl+C now and run:
echo    powershell -NoProfile -ExecutionPolicy Bypass -File "I:\deepseek work\PCMig\tools\l3-deploy.ps1"
echo.
timeout /t 5 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"I:\deepseek work\PCMig\tools\l3-deploy.ps1\"','-Execute' -Verb RunAs"
echo  Elevated window launched with -Execute. You may close this window.
timeout /t 8 >nul