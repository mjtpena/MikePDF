#pragma once

namespace quire::pdfium {

// Owns PDFium's process-wide initialisation. Exactly one instance may exist,
// and it must live on the engine thread: PDFium is not thread-safe (§5.2 rule 1).
class Library {
public:
    Library();
    ~Library();

    Library(const Library&) = delete;
    Library& operator=(const Library&) = delete;
};

}  // namespace quire::pdfium
