@echo off
setlocal
cd /d "%~dp0"
set "compiler=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%compiler%" (
    echo Install .NET Framework 4.8 to build Blink on Windows.
    exit /b 1
)
"%compiler%" /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:Blink.ico /out:Blink.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll ReminderDot.cs Tasks.cs Language.cs PetProfile.cs
exit /b %errorlevel%
