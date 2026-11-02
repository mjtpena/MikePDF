@echo off
rem Builds the S3b C++ engine stub (Release). Output: spikes\S3\out\engine-cpp
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set HERE=%~dp0
set PDFIUM=%HERE%..\..\_shared\pdfium\pdfium-win-x64
set OUT=%HERE%..\out\engine-cpp
if not exist "%OUT%" mkdir "%OUT%"
set OBJ=%HERE%..\out\obj\engine-cpp
if not exist "%OBJ%" mkdir "%OBJ%"
set CFLAGS=/nologo /std:c++20 /O2 /DNDEBUG /MT /EHsc /guard:cf /sdl /W4 /Zi /I"%PDFIUM%\include" /DUNICODE /D_UNICODE
set LFLAGS=/link /CETCOMPAT /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /guard:cf /DEBUG:NONE /OPT:REF /OPT:ICF advapi32.lib "%PDFIUM%\lib\pdfium.dll.lib"

cl %CFLAGS% /Fo"%OBJ%\static.obj" /Fd"%OBJ%\static.pdb" "%HERE%engine.cpp" /Fe"%OUT%\engine-static.exe" %LFLAGS% || exit /b 1
cl %CFLAGS% /DDELAYLOAD_BUILD /Fo"%OBJ%\delay.obj" /Fd"%OBJ%\delay.pdb" "%HERE%engine.cpp" /Fe"%OUT%\engine-delay.exe" %LFLAGS% /DELAYLOAD:pdfium.dll delayimp.lib || exit /b 1
copy /y "%PDFIUM%\bin\pdfium.dll" "%OUT%\pdfium.dll" >nul
copy /y "%PDFIUM%\bin\pdfium.dll" "%OUT%\rogue-copy.dll" >nul
echo build ok
