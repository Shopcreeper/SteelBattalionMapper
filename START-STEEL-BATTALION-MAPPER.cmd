@echo off
setlocal
title Steel Battalion Mapper

set "PATH=%~dp0;%PATH%"

echo ============================================================
echo  STEEL BATTALION MAPPER - KEYBOARD / MOUSE
echo ============================================================
echo.
echo Original Steel Battalion Controller
echo WinUSB input + keyboard/mouse output + LED control
echo.
echo Close any older SteelBattalionMapper window before starting.
echo Press Ctrl+C to stop the mapper.
echo.

dotnet run --project "%~dp0SteelBattalionMapper\SteelBattalionMapper.csproj" -c Release

echo.
echo Mapper stopped.
pause
endlocal
