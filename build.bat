@echo off
REM Builds PZMemoryTool.exe from PZMemoryTool.cs using the C# compiler built into Windows.
REM No downloads or extra software needed.
REM PZMemoryTool.manifest (plain text) is embedded so Windows knows the tool runs
REM with normal user rights and supports Windows 10/11.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find csc.exe. Install .NET Framework 4.x.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:PZMemoryTool.exe ^
    /win32manifest:PZMemoryTool.manifest ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll PZMemoryTool.cs

if errorlevel 1 (
    echo Build failed.
) else (
    echo Built PZMemoryTool.exe
)
pause
