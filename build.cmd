@echo off
setlocal
cd /d "%~dp0"
"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /win32manifest:src\app.manifest /win32icon:src\skladka.ico /out:Skladka.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll src\Archive.cs src\FolderBundle.cs src\Desktop.cs
exit /b %errorlevel%
