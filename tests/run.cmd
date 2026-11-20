@echo off
setlocal
cd /d "%~dp0.."
set "compiler=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "testDir=%TEMP%\BlinkChecks-%RANDOM%-%RANDOM%"
mkdir "%testDir%" || exit /b 1
for %%T in (TaskStoreChecks LanguageChecks ProfileChecks OllamaChecks) do (
    "%compiler%" /nologo /codepage:65001 /main:%%T /out:"%testDir%\%%T.exe" /reference:System.Net.Http.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll Tasks.cs PetProfile.cs Language.cs tests\*.cs || exit /b 1
    "%testDir%\%%T.exe" "%testDir%" || exit /b 1
)
