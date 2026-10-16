@echo off
REM Builds PZMemoryTool.exe from PZMemoryTool.cs using the C# compiler built into Windows.
REM No downloads or extra software needed.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find csc.exe. Install .NET Framework 4.x.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:PZMemoryTool.exe ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll PZMemoryTool.cs

if errorlevel 1 (
    echo Build failed.
) else (
    echo Built PZMemoryTool.exe
)
pause
