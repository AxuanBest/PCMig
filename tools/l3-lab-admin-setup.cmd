@echo off
chcp 65001 >nul
echo.
echo  PCMig Lab - Install Admin Executor (RUN THIS ONLY ONCE)
echo  =======================================================
echo  Creates a scheduled task that runs as SYSTEM with highest
echo  privileges. After this single authorization, the AI can run
echo  all administrator operations by itself - no more UAC prompts.
echo.
echo  It will ask for administrator rights ONCE. Click YES.
echo.
timeout /t 6 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"I:\deepseek work\PCMig\tools\lab-admin-setup.ps1\"','-Install' -Verb RunAs"
echo  Elevated window launched. Wait for the probe result.
timeout /t 10 >nul