@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Останавливаю панель задач, чтобы вернуть Windows...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop_panel.ps1"
echo Останавливаю LiveWallpaper...
taskkill /IM LiveWallpaper.App.exe /F >nul 2>&1
ping -n 3 127.0.0.1 >nul
echo Чищу оставшиеся процессы WebView2 (иначе профиль останется занят и обои не стартуют)...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process -Filter \"Name='msedgewebview2.exe'\" | Where-Object { $_.CommandLine -like '*LiveWallpaper*WebView2_Data*' -or $_.CommandLine -like '*TaskbarSpike_WebView2*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
rem /MIR зеркалит publish-new в publish: устаревшие файлы (старые хешированные ассеты) удаляются
robocopy "publish-new" "publish" /MIR /NFL /NDL /NJH /NJS >nul
if errorlevel 8 (
  echo Ошибка при копировании — publish может быть неполным.
  pause
  exit /b 1
)
echo Готово, запускаю новую версию.
start "" "publish\LiveWallpaper.App.exe"
exit /b 0
