@echo off
chcp 65001 >nul
echo.
echo  PCMig L3 - Orchestrate ^& Verify
echo  ================================
echo  Waits for the 3 VMs, waits for DC01 domain, then makes FS01/CLIENT01
echo  retry domain-join (uses PowerShell Direct - no need to enter the VMs).
echo  Finally collects and prints the state of all three.
echo  Requests administrator rights (UAC).  May take 5-15 minutes.
echo.
timeout /t 5 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"I:\deepseek work\PCMig\tools\l3-orchestrate.ps1\"','-Execute' -Verb RunAs"
echo  Elevated window launched. It will wait and print progress.
timeout /t 8 >nul