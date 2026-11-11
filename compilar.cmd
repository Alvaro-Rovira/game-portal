@echo off
rem Compila Portal.exe con el compilador de C# que ya viene con Windows (.NET Framework 4.8).
rem No hace falta instalar Visual Studio ni nada mas.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (echo No encuentro csc.exe de .NET Framework 4.8 & exit /b 1)
set SRC=src\Native.cs src\Surface.cs src\Gfx.cs src\Games.cs src\Portal.cs src\Brand.cs
set OPT=-nologo -target:winexe -platform:x64 -optimize+ -unsafe -win32manifest:src\app.manifest -r:System.Drawing.dll -r:System.Web.Extensions.dll

rem El icono lo dibuja el propio portal (src\Brand.cs). Si falta, se compila una vez sin icono, se genera y se sigue.
if not exist assets\portal.ico (
  "%CSC%" %OPT% -out:icono-tmp.exe %SRC%
  if errorlevel 1 (echo ERROR al compilar & exit /b 1)
  call :cfg icono-tmp.exe.config
  start "" /wait icono-tmp.exe --icono assets\portal.ico assets\icono.png
  del icono-tmp.exe icono-tmp.exe.config
)

"%CSC%" %OPT% -out:Portal.exe -win32icon:assets\portal.ico %SRC%
if errorlevel 1 (echo ERROR al compilar & exit /b 1)
call :cfg Portal.exe.config
if not exist juegos-extra.txt copy /y juegos-extra.ejemplo.txt juegos-extra.txt >nul
echo OK: Portal.exe
exit /b 0

:cfg
> %1 echo ^<?xml version="1.0"?^>^<configuration^>^<startup^>^<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/^>^</startup^>^<runtime^>^<gcConcurrent enabled="false"/^>^</runtime^>^</configuration^>
exit /b 0
