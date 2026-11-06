@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Собираю интерфейс...
pushd src\LiveWallpaper.Web
call npm run build
if errorlevel 1 goto fail
popd
echo Собираю обои...
dotnet publish src\LiveWallpaper.App\LiveWallpaper.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-new
if errorlevel 1 goto fail
echo Собираю панель...
dotnet publish src\TaskbarSpike\TaskbarSpike.csproj -c Release -o publish-new\TaskbarSpike
if errorlevel 1 goto fail
call "%~dp0update_wallpaper.bat"
exit /b 0

:fail
echo.
echo Сборка не удалась. Обои не перезапускались.
pause
exit /b 1