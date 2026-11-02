@echo off
rem S2 engine (Release): MSVC /O2 /MT /guard:cf, CET compat. Output: spikes\S2\out\engine-s2 (engine.exe + pdfium.dll)
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set HERE=%~dp0
set PDFIUM=%HERE%..\..\..\_shared\pdfium\pdfium-win-x64
set OUT=%HERE%..\..\out\engine-s2
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /std:c++20 /O2 /DNDEBUG /D_CRT_SECURE_NO_WARNINGS /MT /EHsc /guard:cf /sdl /W4 /I"%PDFIUM%\include" "%HERE%engine.cpp" /Fo"%OUT%\engine.obj" /Fe"%OUT%\engine.exe" /link /CETCOMPAT /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /guard:cf advapi32.lib "%PDFIUM%\lib\pdfium.dll.lib" || exit /b 1
copy /y "%PDFIUM%\bin\pdfium.dll" "%OUT%\pdfium.dll" >nul
del "%OUT%\engine.obj" 2>nul
echo build ok
