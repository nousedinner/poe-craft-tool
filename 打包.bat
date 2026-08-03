@echo off
chcp 65001 >nul
echo ========================================
echo   拾刻 - Nuitka 打包脚本
echo ========================================
echo.
echo [1/2] 清理旧文件...
if exist build rmdir /s /q build
if exist dist rmdir /s /q dist
echo [2/2] 开始打包，请耐心等待...
echo.
py -m nuitka --standalone --windows-console-mode=disable --windows-icon-from-ico=poe.ico --output-filename=拾刻.exe --output-dir=dist --include-data-dir=sounds=sounds --include-data-dir=data=data --include-data-file=poe.ico=poe.ico --include-data-file=platforms/qwindows.dll=platforms/qwindows.dll --assume-yes-for-downloads main.py
echo.
echo ========================================
echo   打包完成！输出目录: dist\拾刻.dist
echo ========================================
pause