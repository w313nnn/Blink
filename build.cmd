@echo off
setlocal
cd /d "%~dp0"
"%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /out:ReminderDot.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll ReminderDot.cs
exit /b %errorlevel%
