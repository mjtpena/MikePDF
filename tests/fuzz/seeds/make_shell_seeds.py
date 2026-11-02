"""Writes the seed corpus of the two shell fuzz targets (SHELL-18) into the folders next to this script.

    python tests/fuzz/seeds/make_shell_seeds.py

shell-reply/   inputs of `fuzz_shell_reply` (tests/fuzz/shell_reply_fuzz.cpp): 8 bytes of context (max width u16, max height u16, pixel section bytes u32, little endian), then a
               ShellHost response body (the handler's reply decoder and checks). Includes the SHELL-11 cases: width 0, width 5000, stride not 4 x width, a section smaller than the
               pixels, and overflow-sized values.
shell-request/ inputs of the C# harness `MikePDF.ShellHost.Fuzz` (ShellHost's request decoder and rules): whole request frames (4-byte little-endian length, then the body).

The files are checked in so that a run needs no Python; this script is how they were made and how to change them. MessagePack is written by hand (no library): only the few forms
the protocol uses.
"""
import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
SHELL_OPEN, SHELL_RENDER, SHELL_CLOSE, HELLO, PING = 2048, 2049, 2050, 1, 2
OK, PASSWORD_REQUIRED, DAMAGED, TIMEOUT = 0, 5, 8, 10


def uint(v):
    if v < 0x80:
        return bytes([v])
    if v < 0x100:
        return bytes([0xCC, v])
    if v < 0x10000:
        return b"\xCD" + struct.pack(">H", v)
    if v < 0x100000000:
        return b"\xCE" + struct.pack(">I", v)
    return b"\xCF" + struct.pack(">Q", v)


def f32(v):
    return b"\xCA" + struct.pack(">f", v)


def f32_bits(bits):
    return b"\xCA" + struct.pack(">I", bits)


def array(*items):
    assert len(items) < 16
    return bytes([0x90 | len(items)]) + b"".join(items)


NIL = b"\xC0"


def string(text):
    data = text.encode()
    return bytes([0xA0 | len(data)]) + data


def response(request_id, status, message_type, payload):
    return array(uint(request_id), uint(status), uint(message_type), payload)


def request(request_id, document_id, deadline_ms, message_type, payload):
    return array(uint(request_id), uint(document_id), uint(deadline_ms), uint(message_type), payload)


def frame(body):
    return struct.pack("<I", len(body)) + body


def context(max_w, max_h, section):
    return struct.pack("<HHI", max_w, max_h, section)


def write(folder, name, data):
    path = os.path.join(HERE, folder)
    os.makedirs(path, exist_ok=True)
    with open(os.path.join(path, name + ".bin"), "wb") as f:
        f.write(data)


def render_reply(w, h, stride):
    return response(1, OK, SHELL_RENDER, array(uint(w), uint(h), uint(stride)))


# ---- the handler's reply decoder -------------------------------------------------------------------------------------------------------------------
reply = "shell-reply"
write(reply, "render-ok-256", context(256, 256, 262144) + render_reply(198, 256, 792))
write(reply, "render-ok-exact-section", context(256, 256, 198 * 4 * 256) + render_reply(198, 256, 792))
write(reply, "render-largest", context(1024, 1024, 4194304) + render_reply(1024, 1024, 4096))
write(reply, "shell11-width-0", context(256, 256, 262144) + render_reply(0, 10, 0))
write(reply, "shell11-height-0", context(256, 256, 262144) + render_reply(10, 0, 40))
write(reply, "shell11-width-5000", context(1024, 1024, 4194304) + render_reply(5000, 10, 20000))
write(reply, "shell11-stride-not-4w", context(256, 256, 262144) + render_reply(100, 100, 399))
write(reply, "shell11-stride-too-big", context(256, 256, 262144) + render_reply(100, 100, 800))
write(reply, "shell11-bigger-than-request", context(64, 64, 4194304) + render_reply(100, 100, 400))
write(reply, "shell11-section-too-small", context(256, 256, 1000) + render_reply(100, 100, 400))
write(reply, "shell11-section-one-byte-short", context(256, 256, 100 * 4 * 100 - 1) + render_reply(100, 100, 400))
write(reply, "shell11-overflow-u32-max", context(1024, 1024, 0xFFFFFFFF) + render_reply(0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF))
write(reply, "shell11-overflow-wrapping-width", context(1024, 1024, 4194304) + render_reply(0x40000001, 1, 4))
write(reply, "shell11-overflow-wrapping-height", context(1024, 1024, 4194304) + render_reply(1000, 0x40000, 4000))
write(reply, "open-ok", context(256, 256, 262144) + response(1, OK, SHELL_OPEN, array(uint(1), uint(3), f32(612), f32(792))))
write(reply, "open-zero-pages", context(256, 256, 262144) + response(1, OK, SHELL_OPEN, array(uint(1), uint(0), f32(612), f32(792))))
write(reply, "open-no-session", context(256, 256, 262144) + response(1, OK, SHELL_OPEN, array(uint(0), uint(3), f32(612), f32(792))))
write(reply, "open-nan-height", context(256, 256, 262144) + response(1, OK, SHELL_OPEN, array(uint(1), uint(3), f32(612), f32_bits(0x7FC00000))))
write(reply, "open-infinite-width", context(256, 256, 262144) + response(1, OK, SHELL_OPEN, array(uint(1), uint(3), f32_bits(0x7F800000), f32(792))))
write(reply, "open-password", context(256, 256, 262144) + response(1, PASSWORD_REQUIRED, SHELL_OPEN, NIL))
write(reply, "open-damaged", context(256, 256, 262144) + response(1, DAMAGED, SHELL_OPEN, NIL))
write(reply, "render-timeout", context(256, 256, 262144) + response(1, TIMEOUT, SHELL_RENDER, NIL))
write(reply, "close-ok", context(256, 256, 262144) + response(1, OK, SHELL_CLOSE, array()))
write(reply, "type-confusion-open-shape-as-render", context(256, 256, 262144) + response(1, OK, SHELL_RENDER, array(uint(1), uint(3), f32(612), f32(792))))
write(reply, "payload-with-status-error", context(256, 256, 262144) + response(1, DAMAGED, SHELL_RENDER, array(uint(10), uint(10), uint(40))))
write(reply, "hello-reply", context(256, 256, 262144) + response(1, OK, HELLO, array(uint(5), string(""))))
write(reply, "truncated", context(256, 256, 262144) + response(1, OK, SHELL_RENDER, array(uint(10)))[:-1])

# ---- ShellHost's request decoder ---------------------------------------------------------------------------------------------------------------
req = "shell-request"
write(req, "hello-shell-handler", frame(request(1, 0, 1000, HELLO, array(uint(5), uint(3), uint(4321)))))
write(req, "hello-broker", frame(request(1, 0, 1000, HELLO, array(uint(5), uint(0), uint(4321)))))
write(req, "hello-engine", frame(request(1, 0, 1000, HELLO, array(uint(5), uint(1), uint(4321)))))
write(req, "hello-other-schema", frame(request(1, 0, 1000, HELLO, array(uint(6), uint(3), uint(4321)))))
write(req, "ping", frame(request(2, 0, 1000, PING, array(uint(77)))))
write(req, "open-small", frame(request(3, 0, 1700, SHELL_OPEN, array(uint(4660), uint(3564)))))
write(req, "open-one-byte", frame(request(3, 0, 1700, SHELL_OPEN, array(uint(1), uint(1)))))
write(req, "open-32mib", frame(request(3, 0, 1700, SHELL_OPEN, array(uint(4660), uint(33554432)))))
write(req, "open-over-32mib", frame(request(3, 0, 1700, SHELL_OPEN, array(uint(4660), uint(33554433)))))
write(req, "open-zero-bytes", frame(request(3, 0, 1700, SHELL_OPEN, array(uint(4660), uint(0)))))
write(req, "open-section-0", frame(request(3, 0, 1700, SHELL_OPEN, array(uint(0), uint(100)))))
write(req, "open-handle-64-bit", frame(request(3, 0, 1700, SHELL_OPEN, array(uint(0xFFFFFFFFFFFFFFFF), uint(100)))))
write(req, "open-with-a-document-id", frame(request(3, 7, 1700, SHELL_OPEN, array(uint(4660), uint(100)))))
write(req, "open-deadline-0", frame(request(3, 0, 0, SHELL_OPEN, array(uint(4660), uint(100)))))
write(req, "open-deadline-max", frame(request(3, 0, 86400000, SHELL_OPEN, array(uint(4660), uint(100)))))
write(req, "render-256", frame(request(4, 1, 1700, SHELL_RENDER, array(uint(0), uint(256), uint(256), uint(1), uint(4660), uint(262144)))))
write(req, "render-1024", frame(request(4, 1, 5000, SHELL_RENDER, array(uint(0), uint(1024), uint(1024), uint(1), uint(4660), uint(4194304)))))
write(req, "render-pixels-too-small", frame(request(4, 1, 1700, SHELL_RENDER, array(uint(0), uint(1024), uint(1024), uint(1), uint(4660), uint(4)))))
write(req, "render-pixels-one-short", frame(request(4, 1, 1700, SHELL_RENDER, array(uint(0), uint(256), uint(256), uint(1), uint(4660), uint(262143)))))
write(req, "render-edge-below-16", frame(request(4, 1, 1700, SHELL_RENDER, array(uint(0), uint(15), uint(256), uint(1), uint(4660), uint(262144)))))
write(req, "render-edge-above-1024", frame(request(4, 1, 1700, SHELL_RENDER, array(uint(0), uint(1025), uint(256), uint(1), uint(4660), uint(4194304)))))
write(req, "render-flags-2", frame(request(4, 1, 1700, SHELL_RENDER, array(uint(0), uint(256), uint(256), uint(2), uint(4660), uint(262144)))))
write(req, "render-no-document", frame(request(4, 0, 1700, SHELL_RENDER, array(uint(0), uint(256), uint(256), uint(1), uint(4660), uint(262144)))))
write(req, "render-last-page", frame(request(4, 1, 1700, SHELL_RENDER, array(uint(9999999), uint(256), uint(256), uint(0), uint(4660), uint(262144)))))
write(req, "close", frame(request(5, 1, 500, SHELL_CLOSE, array())))
write(req, "close-no-document", frame(request(5, 0, 500, SHELL_CLOSE, array())))
write(req, "shutdown-not-served", frame(request(6, 0, 500, 3, array())))
write(req, "engine-open-document-not-served", frame(request(7, 0, 500, 16, array(uint(4), NIL))))
print("wrote", sum(len(files) for _, _, files in os.walk(HERE) if True) - 1, "files under", HERE)
