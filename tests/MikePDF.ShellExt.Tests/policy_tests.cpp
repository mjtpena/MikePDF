// SHELL-15 (the handler side): the policy gate, with a fake registry. The rules are the app's (docs/admin/policies.md): machine wins, user can only tighten,
// invalid and unreadable values fail closed.
#include <gtest/gtest.h>

#include <map>

#include "policy_gate.h"

namespace {

using namespace mikepdf::shellext;
using Kind = DwordRead::Kind;

struct FakeRegistry {
    std::map<std::pair<HKEY, std::wstring>, DwordRead> values;
    mutable int reads = 0;

    PolicyReader reader() const {
        return [this](HKEY hive, const wchar_t* name) {
            ++reads;
            const auto it = values.find({hive, name});
            return it == values.end() ? DwordRead{Kind::Absent, 0} : it->second;
        };
    }
};

PolicyState state(const FakeRegistry& registry, ShellFeature feature = ShellFeature::Thumbnails) { return evaluate_policy(feature, registry.reader()); }

TEST(PolicyGate, NothingConfiguredAllows) {
    FakeRegistry registry;
    EXPECT_EQ(state(registry), PolicyState::Allowed);
    EXPECT_EQ(state(registry, ShellFeature::Preview), PolicyState::Allowed);
}

TEST(PolicyGate, TheMachinePolicyOffSwitchesTheFeatureOff) {
    FakeRegistry registry;
    registry.values[{HKEY_LOCAL_MACHINE, L"ShellThumbnailsEnabled"}] = {Kind::Valid, 0};
    EXPECT_EQ(state(registry), PolicyState::OffByPolicy);
    EXPECT_EQ(state(registry, ShellFeature::Preview), PolicyState::Allowed) << "the two policies are separate";
}

TEST(PolicyGate, EachFeatureReadsItsOwnValue) {
    EXPECT_STREQ(policy_value_name(ShellFeature::Thumbnails), L"ShellThumbnailsEnabled");
    EXPECT_STREQ(policy_value_name(ShellFeature::Preview), L"ShellPreviewEnabled");
    FakeRegistry registry;
    registry.values[{HKEY_LOCAL_MACHINE, L"ShellPreviewEnabled"}] = {Kind::Valid, 0};
    EXPECT_EQ(state(registry), PolicyState::Allowed);
    EXPECT_EQ(state(registry, ShellFeature::Preview), PolicyState::OffByPolicy);
}

TEST(PolicyGate, OneIsExplicitlyOn) {
    FakeRegistry registry;
    registry.values[{HKEY_LOCAL_MACHINE, L"ShellThumbnailsEnabled"}] = {Kind::Valid, 1};
    EXPECT_EQ(state(registry), PolicyState::Allowed);
}

TEST(PolicyGate, TheUserHiveCanOnlyTighten) {
    FakeRegistry registry;
    registry.values[{HKEY_CURRENT_USER, L"ShellThumbnailsEnabled"}] = {Kind::Valid, 0};
    EXPECT_EQ(state(registry), PolicyState::OffByPolicy) << "a person may turn their own copy off";

    FakeRegistry loosened;
    loosened.values[{HKEY_LOCAL_MACHINE, L"ShellThumbnailsEnabled"}] = {Kind::Valid, 0};
    loosened.values[{HKEY_CURRENT_USER, L"ShellThumbnailsEnabled"}] = {Kind::Valid, 1};
    EXPECT_EQ(state(loosened), PolicyState::OffByPolicy) << "a person may never turn on what the organisation turned off";
}

TEST(PolicyGate, InvalidValuesFailClosed) {
    FakeRegistry out_of_range;
    out_of_range.values[{HKEY_LOCAL_MACHINE, L"ShellThumbnailsEnabled"}] = {Kind::Valid, 2};
    EXPECT_EQ(state(out_of_range), PolicyState::OffByPolicy);

    FakeRegistry wrong_type;
    wrong_type.values[{HKEY_LOCAL_MACHINE, L"ShellThumbnailsEnabled"}] = {Kind::Invalid, 0};
    EXPECT_EQ(state(wrong_type), PolicyState::OffByPolicy);

    FakeRegistry user_wrong_type;
    user_wrong_type.values[{HKEY_CURRENT_USER, L"ShellThumbnailsEnabled"}] = {Kind::Invalid, 0};
    EXPECT_EQ(state(user_wrong_type), PolicyState::OffByPolicy);
}

TEST(PolicyGate, AnUnreadableRegistryFailsClosed) {
    FakeRegistry registry;
    registry.values[{HKEY_LOCAL_MACHINE, L"ShellPreviewEnabled"}] = {Kind::Failed, 0};
    EXPECT_EQ(state(registry, ShellFeature::Preview), PolicyState::OffByPolicy);
}

TEST(PolicyGate, TheRealRegistryReaderIsQuietWhenNothingIsConfigured) {
    // Read-only: this test never writes a registry value. On a machine without the policy it must say Absent (or whatever the machine has configured, never Failed).
    const DwordRead machine = read_policy_from_registry(HKEY_LOCAL_MACHINE, L"ShellNoSuchPolicyForTests");
    EXPECT_EQ(machine.kind, Kind::Absent);
    const DwordRead user = read_policy_from_registry(HKEY_CURRENT_USER, L"ShellNoSuchPolicyForTests");
    EXPECT_EQ(user.kind, Kind::Absent);
}

}  // namespace
