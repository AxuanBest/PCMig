@echo off
chcp 65001 >nul
echo.
echo  PCMig L3 - FULLY AUTOMATIC offline deploy
echo  ==========================================
echo  NO key presses needed. NO CD boot. NO manual install.
echo.
echo  It will (all automatic):
echo    1. mount the official ISO (read-only; SHA256 untouched)
echo    2. partition a new VHDX (EFI+MSR+NTFS)
echo    3. expand install.wim index 2 directly into the VHDX
echo    4. write UEFI boot files (bcdboot)
echo    5. inject unattend.xml + SetupComplete.cmd + setup scripts
echo    6. start the VMs - first boot configures AD/DNS/SMB by itself
echo.
echo  Takes ~10-20 min per VM (image expansion). Runs unattended.
echo  Requests administrator rights (UAC).
echo.
timeout /t 6 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"I:\deepseek work\PCMig\tools\l3-inject.ps1\"','-Execute' -Verb RunAs"
echo  Elevated window launched. You may close this window.
timeout /t 8 >nul