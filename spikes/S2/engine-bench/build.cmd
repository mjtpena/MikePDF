@echo off
rem S2.1 bench (Release): cl /O2 /MT /std:c++20 ... Output: spikes\S2\out\engine-bench
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set HERE=%~dp0
set PDFIUM=%HERE%..\..\_shared\pdfium\pdfium-win-x64
set OUT=%HERE%..\out\engine-bench
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /std:c++20 /O2 /DNDEBUG /D_CRT_SECURE_NO_WARNINGS /MT /EHsc /guard:cf /sdl /W4 /I"%PDFIUM%\include" "%HERE%bench.cpp" /Fo"%OUT%\bench.obj" /Fe"%OUT%\bench.exe" /link /CETCOMPAT /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT "%PDFIUM%\lib\pdfium.dll.lib" || exit /b 1
copy /y "%PDFIUM%\bin\pdfium.dll" "%OUT%\pdfium.dll" >nul
echo build ok
