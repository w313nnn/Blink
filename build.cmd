@echo off
setlocal
set "binary=%~dp0Blink.exe"
if not "%~1"=="" set "binary=%~f1"
cd /d "%~dp0"
set "compiler=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%compiler%" (
    echo Install .NET Framework 4.8 to build Blink on Windows.
    exit /b 1
)
"%compiler%" /nologo /codepage:65001 /target:winexe /optimize+ /win32manifest:Blink.manifest /win32icon:Blink.ico "/out:%binary%" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll ReminderDot.cs Tasks.cs Checklist.cs Language.cs TaskCommands.cs PetProfile.cs PetArtwork.cs ConversationMotion.cs
exit /b %errorlevel%
