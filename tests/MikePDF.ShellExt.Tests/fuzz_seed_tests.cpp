// SHELL-18: the seed corpus of the handler-reply fuzz target (tests/fuzz/seeds/shell-reply, made by tests/fuzz/seeds/make_shell_seeds.py) is replayed through the very function
// the libFuzzer target runs, on every build, together with a deterministic mutation of each seed. A seed that crashes the decoder or breaks an invariant fails here before
// the fuzz workflow ever starts.
#include <gtest/gtest.h>

#include <filesystem>
#include <fstream>
#include <iterator>
#include <vector>

#include "shell_reply_fuzz.h"

#ifndef MIKEPDF_FUZZ_SEED_DIR
#error "MIKEPDF_FUZZ_SEED_DIR must point at tests/fuzz/seeds"
#endif

namespace {

std::vector<std::vector<std::uint8_t>> seeds() {
    std::vector<std::vector<std::uint8_t>> out;
    for (const auto& entry : std::filesystem::directory_iterator(std::filesystem::path(MIKEPDF_FUZZ_SEED_DIR) / "shell-reply")) {
        std::ifstream in(entry.path(), std::ios::binary);
        out.emplace_back(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
    }
    return out;
}

TEST(ShellReplyFuzzSeeds, ThereAreSeedsIncludingTheShell11Cases) {
    EXPECT_GE(seeds().size(), 25u);
    std::size_t shell11 = 0;
    for (const auto& entry : std::filesystem::directory_iterator(std::filesystem::path(MIKEPDF_FUZZ_SEED_DIR) / "shell-reply")) {
        shell11 += entry.path().filename().string().rfind("shell11-", 0) == 0 ? 1 : 0;
    }
    EXPECT_GE(shell11, 9u) << "width 0, width 5000, stride, section too small and the overflow cases are seeds";
}

TEST(ShellReplyFuzzSeeds, EverySeedAndEveryPrefixAndByteFlipPassesTheTargetsInvariants) {
    for (const auto& seed : seeds()) {
        mikepdf::fuzz::shell_reply_one_input(seed.data(), seed.size());
        for (std::size_t cut = 0; cut < seed.size(); ++cut) {
            mikepdf::fuzz::shell_reply_one_input(seed.data(), cut);  // every truncation
        }
        for (std::size_t at = 0; at < seed.size(); ++at) {
            auto mutated = seed;
            mutated[at] ^= 0xFF;
            mikepdf::fuzz::shell_reply_one_input(mutated.data(), mutated.size());
            mutated[at] = static_cast<std::uint8_t>(seed[at] + 1);
            mikepdf::fuzz::shell_reply_one_input(mutated.data(), mutated.size());
        }
    }
    SUCCEED();
}

}  // namespace
