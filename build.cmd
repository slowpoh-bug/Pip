@echo off
rem Builds Pip.exe with the C# compiler included in Windows.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /win32icon:"%~dp0assets\Pip.ico" /out:"%~dp0Pip.exe" "%~dp0Pip.cs"
