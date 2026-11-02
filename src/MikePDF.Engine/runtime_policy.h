// Mitigations the engine applies to itself at runtime (ADR-0004 decision 2).
//
// The Microsoft-signed-only image policy cannot be set at process creation: pdfium.dll is not Microsoft-signed and the
// loader would refuse it (S3b: error 577). So the engine loads its fixed DLL set first, then locks the process, and only
// then accepts a document.
#pragma once

namespace mikepdf::engine {

/// SetProcessMitigationPolicy(ProcessSignaturePolicy, MicrosoftSignedOnly). Returns 0 on success, else the Win32 error.
/// After this, LoadLibrary of an image that is not Microsoft-signed fails with ERROR_INVALID_IMAGE_HASH (577).
[[nodiscard]] unsigned long apply_runtime_signature_policy() noexcept;

}  // namespace mikepdf::engine
