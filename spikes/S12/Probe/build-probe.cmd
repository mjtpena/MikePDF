@echo off
rem Builds the native S12 probe with the same compiler flags as the S3b engine stub (static CRT, CFG, CET compatible). Output: spikes\S12\out\probe\S12ProbeCpp.exe
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set HERE=%~dp0
set OUT=%HERE%..\out\probe
set OBJ=%HERE%..\out\obj
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OBJ%" mkdir "%OBJ%"
cl /nologo /std:c++20 /O2 /DNDEBUG /MT /EHsc /guard:cf /sdl /W4 /DUNICODE /D_UNICODE /Fo"%OBJ%\probe.obj" "%HERE%probe.cpp" /Fe"%OUT%\S12ProbeCpp.exe" /link /CETCOMPAT /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /guard:cf gdi32.lib user32.lib winspool.lib advapi32.lib || exit /b 1
echo build ok
