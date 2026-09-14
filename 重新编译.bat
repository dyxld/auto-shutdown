@echo off
chcp 65001 >nul
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [错误] 没找到系统自带的 C# 编译器 csc.exe，请确认已安装 .NET Framework 4.x。
    pause
    exit /b 1
)
echo 正在编译...
"%CSC%" -nologo -target:winexe -optimize+ -codepage:65001 -out:自动关机助手.exe -reference:System.dll -reference:System.Windows.Forms.dll -reference:System.Drawing.dll AutoShutdown.cs
if errorlevel 1 (
    echo 编译失败，请把上面的报错发出来。
) else (
    echo 编译完成：自动关机助手.exe
)
pause
