@echo off
title Autoplayer Control
cd /d "%~dp0"

echo ==================================================
echo   Autoplayer Control
echo ==================================================
echo.

dotnet --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 (
  echo  .NET 8 is not installed on this PC yet.
  echo.
  echo  Your browser will now open the download page.
  echo  Download the ".NET SDK 8" Windows x64 installer, install it,
  echo  then double-click "Start Autoplayer Control" again.
  echo.
  start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
  pause
  exit /b
)

rem --- Is Autoplayer Control already running? Then just open it. ---
powershell -NoProfile -Command "try{$r=Invoke-WebRequest http://localhost:5080/api/meta -UseBasicParsing -TimeoutSec 2; if($r.Content -match 'experiences'){exit 0}else{exit 1}}catch{exit 1}"
if not errorlevel 1 (
  echo  Autoplayer Control is ALREADY running - opening it in your browser.
  echo  ^(It is running from another black window. Close that window to stop it.^)
  echo.
  start "" "http://localhost:5080"
  timeout /t 6 >nul
  exit /b
)

rem --- Find a free port, starting at 5080. ---
for /f %%p in ('powershell -NoProfile -Command "$p=5080; while($true){try{$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any,$p); $l.Start(); $l.Stop(); break}catch{$p++}}; $p"') do set PORT=%%p
if "%PORT%"=="" set PORT=5080

echo  Starting on http://localhost:%PORT%
echo  The FIRST start can take 1-3 minutes.
echo  Your browser will open by itself when it is ready.
echo.
echo  KEEP THIS WINDOW OPEN while you use the tool.
echo  To stop the tool, close this window.
echo.

start "" /b powershell -NoProfile -WindowStyle Hidden -Command "for($i=0;$i -lt 180;$i++){try{Invoke-WebRequest http://localhost:%PORT%/api/meta -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process 'http://localhost:%PORT%'; break}catch{Start-Sleep 2}}"

dotnet run --project src\ApControl.Server -c Release --no-launch-profile -- --urls http://0.0.0.0:%PORT%

echo.
echo  The tool has stopped. If you see red error text above,
echo  take a screenshot of this window and send it to Claude.
pause
