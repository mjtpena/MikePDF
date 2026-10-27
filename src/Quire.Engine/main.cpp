// Quire.Engine entry point. WP4 replaces this with the pipe client, I/O lane and engine thread.
#include <Windows.h>

#include "quire/pdfium/library.h"

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    quire::pdfium::Library library;
    return 0;
}
