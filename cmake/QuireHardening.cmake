# Compiler and linker hardening for all Quire-owned native code (master prompt §13.2, ADR-0002).

# /Qspectre needs the Spectre-mitigated runtime libraries, an optional VS component.
# CI sets QUIRE_REQUIRE_SPECTRE=ON so a missing component fails the build there;
# developer machines without it get a warning instead.
option(QUIRE_REQUIRE_SPECTRE "Fail if Spectre-mitigated libraries are missing" OFF)
set(QUIRE_SPECTRE OFF)
if(MSVC)
  get_filename_component(_quire_msvc_bin "${CMAKE_CXX_COMPILER}" DIRECTORY)
  get_filename_component(_quire_msvc_root "${_quire_msvc_bin}/../../.." ABSOLUTE)
  if(CMAKE_GENERATOR_PLATFORM MATCHES "ARM64")
    set(_quire_spectre_dir "${_quire_msvc_root}/lib/spectre/arm64")
  else()
    set(_quire_spectre_dir "${_quire_msvc_root}/lib/spectre/x64")
  endif()
  if(EXISTS "${_quire_spectre_dir}")
    set(QUIRE_SPECTRE ON)
  elseif(QUIRE_REQUIRE_SPECTRE)
    message(FATAL_ERROR "Spectre-mitigated libraries not found at ${_quire_spectre_dir}")
  else()
    message(WARNING "Spectre-mitigated libraries not found; building WITHOUT /Qspectre (not for release)")
  endif()
endif()

function(quire_harden target)
  target_compile_features(${target} PRIVATE cxx_std_20)
  if(MSVC)
    target_compile_options(${target} PRIVATE
      /W4 /WX /permissive- /utf-8 /Zc:__cplusplus
      /sdl /guard:cf /GS /Zi)
    if(QUIRE_SPECTRE)
      target_compile_options(${target} PRIVATE /Qspectre)
    endif()
    target_link_options(${target} PRIVATE
      /guard:cf /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /DEBUG /OPT:REF /OPT:ICF)
    if(NOT CMAKE_GENERATOR_PLATFORM MATCHES "ARM64")
      target_link_options(${target} PRIVATE /CETCOMPAT)
    endif()
    target_compile_definitions(${target} PRIVATE
      UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
  endif()
endfunction()
