@echo off
chcp 65001 >nul
echo.
echo  PCMig L3 - VM Boot Diagnostics
echo  ==============================
echo  Checks: boot order, DVD attachment, ISO presence, disk state.
echo  Writes a report to G:\PCMigLab\l3-diag-*.txt
echo  Requests administrator rights (UAC).
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"I:\deepseek work\PCMig\tools\l3-diag.ps1\"' -Verb RunAs"
echo  Elevated window launched. Send the report back.
timeout /t 6 >nul