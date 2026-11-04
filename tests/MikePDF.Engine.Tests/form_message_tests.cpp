// The form messages of schema 7 (docs/features/forms.md 10.3): GetFormFields, GetFormFieldDetail, SetFormFieldValue, FormAction, and the field highlight of RenderTile.
// Criteria FM-01 (the engine half; the codec half is in protocol_tests.cpp), FM-02 (document order only: the pinned PDFium cannot read /Tabs), FM-06, FM-10, FM-12, FM-16, FM-18.
//
// The form (one 400 x 400 pt page and a second page with one more widget). Widget ids are positions in document order:
//   0 name (text, /MaxLen 8, /DV (Bob), value Alice)   1 agree (check box, off)   2 color red (radio, on)   3 color blue (radio, off)   4 country (combo box, France, /DV Spain)
//   5 free (editable combo box)   6 langs (list box, multiple selection, D and E selected)   7 locked (read-only text)   8 pw (password text)   9 reset (push button, ResetForm)
//   10 send (push button, SubmitForm)   11 amount (text with a format script)   12 dup (text, page 1)   13 dup (the same field, page 2)
#include <gtest/gtest.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <deque>
#include <string>
#include <vector>

#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using pdfium::Document;
using pdfium::RenderInterrupt;
using pdfium::TileRequest;
using protocol::Status;
using support::PdfBuilder;
using support::pixel;

constexpr auto kNever = [] { return RenderInterrupt::None; };
constexpr auto kNotExpired = [] { return false; };

std::vector<std::uint8_t> build_form() {
    PdfBuilder b;
    const int catalog = b.reserve();
    const int pages = b.reserve();
    const int page1 = b.reserve();
    const int page2 = b.reserve();
    const int font = b.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    const int content1 = b.add_stream("0.95 g 0 0 400 12 re f");
    const int content2 = b.add_stream("q Q");
    const int on = b.add_stream("0 0.6 0 rg 0 0 20 20 re f", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]");
    const int off = b.add_stream("q Q", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]");
    const std::string da = "/DA (/Helv 12 Tf 0 g)";
    const auto ref = [](int n) { return std::to_string(n) + " 0 R"; };

    const int name = b.add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Alice) /DV (Bob) /TU (Full name) /MaxLen 8 /Rect [20 340 180 370] /F 4 " + da + " >>");
    const int agree = b.add("<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Off /AS /Off /Rect [20 300 40 320] /F 4 /AP << /N << /Yes " + ref(on) + " /Off " + ref(off) + " >> >> >>");
    const int radio = b.reserve();
    const int red = b.add("<< /Type /Annot /Subtype /Widget /Parent " + ref(radio) + " /Rect [20 260 40 280] /F 4 /AS /Red /AP << /N << /Red " + ref(on) + " /Off " + ref(off) + " >> >> >>");
    const int blue = b.add("<< /Type /Annot /Subtype /Widget /Parent " + ref(radio) + " /Rect [60 260 80 280] /F 4 /AS /Off /AP << /N << /Blue " + ref(on) + " /Off " + ref(off) + " >> >> >>");
    b.set(radio, "<< /FT /Btn /Ff 32768 /T (color) /V /Red /Kids [" + ref(red) + " " + ref(blue) + "] >>");
    const int country = b.add("<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (country) /Opt [(France) (Germany) (Spain)] /V (France) /DV (Spain) /Rect [20 220 180 245] /F 4 " + da + " >>");
    const int free_combo = b.add("<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 393216 /T (free) /Opt [(Aa) (Bb)] /V (Aa) /Rect [20 190 180 215] /F 4 " + da + " >>");
    const int langs = b.add("<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 2097152 /T (langs) /Opt [(C) (D) (E) (F)] /V [(D) (E)] /I [1 2] /Rect [20 110 180 180] /F 4 " + da + " >>");
    const int locked = b.add("<< /Type /Annot /Subtype /Widget /FT /Tx /Ff 1 /T (locked) /V (fixed) /Rect [200 340 380 370] /F 4 " + da + " >>");
    const int password = b.add("<< /Type /Annot /Subtype /Widget /FT /Tx /Ff 8192 /T (pw) /V (secret) /Rect [200 300 380 330] /F 4 " + da + " >>");
    const int reset = b.add("<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (reset) /A << /S /ResetForm >> /Rect [200 260 260 285] /F 4 >>");
    const int send = b.add("<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (send) /A << /S /SubmitForm /F << /FS /URL /F (http://example.org/submit) >> >> /Rect [280 260 340 285] /F 4 >>");
    const int amount = b.add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (amount) /V (1) /AA << /F << /S /JavaScript /JS (AFNumber_Format\\(2, 0, 0, 0, \"\", true\\);) >> >> /Rect [200 220 380 245] /F 4 " + da + " >>");
    const int dup_parent = b.reserve();
    const int dup1 = b.add("<< /Type /Annot /Subtype /Widget /Parent " + ref(dup_parent) + " /Rect [200 180 380 205] /F 4 " + da + " >>");
    const int dup2 = b.add("<< /Type /Annot /Subtype /Widget /Parent " + ref(dup_parent) + " /Rect [20 20 200 45] /F 4 " + da + " >>");
    b.set(dup_parent, "<< /FT /Tx /T (dup) /V (shared) /Kids [" + ref(dup1) + " " + ref(dup2) + "] >>");

    const int acroform = b.add("<< /Fields [" + ref(name) + " " + ref(agree) + " " + ref(radio) + " " + ref(country) + " " + ref(free_combo) + " " + ref(langs) + " " + ref(locked) + " " + ref(password) + " " +
                               ref(reset) + " " + ref(send) + " " + ref(amount) + " " + ref(dup_parent) + "] /NeedAppearances false /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv " + ref(font) + " >> >> >>");
    b.set(catalog, "<< /Type /Catalog /Pages " + ref(pages) + " /AcroForm " + ref(acroform) + " >>");
    b.set(pages, "<< /Type /Pages /Kids [" + ref(page1) + " " + ref(page2) + "] /Count 2 /MediaBox [0 0 400 400] >>");
    b.set(page1, "<< /Type /Page /Parent " + ref(pages) + " /Contents " + ref(content1) + " /Resources << /Font << /Helv " + ref(font) + " >> >> /Annots [" + ref(name) + " " + ref(agree) + " " +
                     ref(red) + " " + ref(blue) + " " + ref(country) + " " + ref(free_combo) + " " + ref(langs) + " " + ref(locked) + " " + ref(password) + " " + ref(reset) + " " + ref(send) + " " +
                     ref(amount) + " " + ref(dup1) + "] >>");
    b.set(page2, "<< /Type /Page /Parent " + ref(pages) + " /Contents " + ref(content2) + " /Resources << /Font << /Helv " + ref(font) + " >> >> /Annots [" + ref(dup2) + "] >>");
    return support::bytes_of(b.build(catalog));
}

constexpr std::uint32_t kName = 0, kAgree = 1, kRed = 2, kBlue = 3, kCountry = 4, kFree = 5, kLangs = 6, kLocked = 7, kPassword = 8, kReset = 9, kSend = 10, kAmount = 11, kDup1 = 12, kDup2 = 13;

class FormMessageTest : public ::testing::Test {
protected:
    pdfium::Library library_;
    std::vector<std::uint8_t> bytes_ = build_form();

    // PDFium reads the bytes in place, so they have to outlive the document: they are kept here until the test ends.
    Document open(std::vector<std::uint8_t> bytes) {
        kept_.push_back(std::move(bytes));
        auto opened = Document::open(kept_.back(), {});
        EXPECT_EQ(opened.status, Status::Ok);
        return std::move(opened.value);
    }
    std::deque<std::vector<std::uint8_t>> kept_;

    static std::string value(Document& doc, std::uint32_t id) {
        auto detail = doc.form_field_detail(id, 0, kNotExpired);
        EXPECT_EQ(detail.status, Status::Ok) << id;
        return detail.value.value;
    }

    static bool checked(Document& doc, std::uint32_t id) {
        auto detail = doc.form_field_detail(id, 0, kNotExpired);
        EXPECT_EQ(detail.status, Status::Ok) << id;
        return detail.value.checked;
    }

    static protocol::SetFormFieldValue text(std::uint32_t id, const std::string& v) {
        protocol::SetFormFieldValue r;
        r.field_id = id;
        r.kind = 0;
        r.text = v;
        return r;
    }

    static protocol::SetFormFieldValue toggle(std::uint32_t id, bool on) {
        protocol::SetFormFieldValue r;
        r.field_id = id;
        r.kind = 1;
        r.checked = on;
        return r;
    }

    static protocol::SetFormFieldValue choice(std::uint32_t id, std::vector<std::uint32_t> options) {
        protocol::SetFormFieldValue r;
        r.field_id = id;
        r.kind = 2;
        r.selected = std::move(options);
        return r;
    }

    static std::vector<std::uint8_t> save(Document& doc, bool incremental) {
        std::vector<std::uint8_t> out;
        std::uint64_t written = 0;
        EXPECT_EQ(doc.save(incremental, [&out](const void* data, unsigned long size) {
            const auto* p = static_cast<const std::uint8_t*>(data);
            out.insert(out.end(), p, p + size);
            return true;
        }, written), Status::Ok);
        return out;
    }
};

TEST_F(FormMessageTest, TheWidgetsAreListedInDocumentOrderWithTheirTypesNamesAndStates) {
    Document doc = open(bytes_);
    auto fields = doc.form_fields(0, 64, kNotExpired);
    ASSERT_EQ(fields.status, Status::Ok);
    const auto& f = fields.value;
    EXPECT_EQ(f.total, 14u);
    ASSERT_EQ(f.field_ids.size(), 14u);
    for (std::uint32_t i = 0; i < 14; ++i) {
        EXPECT_EQ(f.field_ids[i], i);
    }
    EXPECT_EQ((std::vector<std::uint32_t>(f.pages.begin(), f.pages.end())), (std::vector<std::uint32_t>{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1}));
    // FPDF_FORMFIELD_*: 6 text, 2 check box, 3 radio, 4 combo, 5 list, 1 push button
    EXPECT_EQ((std::vector<std::uint16_t>(f.types.begin(), f.types.end())), (std::vector<std::uint16_t>{6, 2, 3, 3, 4, 4, 5, 6, 6, 1, 1, 6, 6, 6}));
    EXPECT_EQ(f.names[kName], "name");
    EXPECT_EQ(f.names[kRed], "color");
    EXPECT_EQ(f.names[kBlue], "color");
    EXPECT_EQ(f.names[kDup1], "dup");
    EXPECT_EQ(f.names[kDup2], "dup");
    EXPECT_EQ(f.alternate_names[kName], "Full name");
    EXPECT_FLOAT_EQ(f.lefts[kName], 20.f);
    EXPECT_FLOAT_EQ(f.tops[kName], 370.f);
    EXPECT_EQ(f.max_lens[kName], 8);
    EXPECT_EQ(f.max_lens[kAgree], 0);
    EXPECT_NE(f.flags[kLocked] & 1u, 0u);          // read-only
    EXPECT_NE(f.flags[kPassword] & (1u << 13), 0u);
    EXPECT_EQ(f.values[kName], "Alice");
    EXPECT_EQ(f.values[kCountry], "France");
    EXPECT_EQ(f.values[kPassword], "");            // never sent for a password field
    EXPECT_NE(f.states[kRed] & 1u, 0u);            // the selected radio button
    EXPECT_EQ(f.states[kBlue] & 1u, 0u);
    EXPECT_EQ(f.states[kAgree] & 1u, 0u);
    EXPECT_NE(f.states[kLocked] & 8u, 0u);         // skipped by Tab: read-only
    EXPECT_EQ(f.states[kName] & 8u, 0u);
    EXPECT_NE(f.states[kAmount] & 2u, 0u);         // has a script
    EXPECT_EQ(f.states[kName] & 2u, 0u);
}

TEST_F(FormMessageTest, TheListIsReadInWindows) {
    Document doc = open(bytes_);
    auto first = doc.form_fields(0, 5, kNotExpired);
    ASSERT_EQ(first.status, Status::Ok);
    EXPECT_EQ(first.value.total, 14u);
    EXPECT_EQ(first.value.field_ids.size(), 5u);
    auto last = doc.form_fields(12, 64, kNotExpired);
    ASSERT_EQ(last.status, Status::Ok);
    EXPECT_EQ(last.value.field_ids.size(), 2u);
    EXPECT_EQ(last.value.pages[1], 1u);
    auto past = doc.form_fields(50, 5, kNotExpired);
    ASSERT_EQ(past.status, Status::Ok);
    EXPECT_TRUE(past.value.field_ids.empty());
    EXPECT_EQ(doc.form_fields(0, 0, kNotExpired).status, Status::InvalidArgument);
    EXPECT_EQ(doc.form_fields(0, 65, kNotExpired).status, Status::InvalidArgument);
    EXPECT_EQ(doc.form_fields(100'000, 1, kNotExpired).status, Status::InvalidArgument);
}

TEST_F(FormMessageTest, ADocumentWithoutAFormHasNoFields) {
    Document doc = open(support::read_fixture("hello_world.pdf"));
    auto fields = doc.form_fields(0, 64, kNotExpired);
    ASSERT_EQ(fields.status, Status::Ok);
    EXPECT_EQ(fields.value.total, 0u);
    EXPECT_TRUE(fields.value.field_ids.empty());
    EXPECT_EQ(doc.form_field_detail(0, 0, kNotExpired).status, Status::InvalidArgument);
}

TEST_F(FormMessageTest, ListingTheFieldsTimesOutAndCarriesOnFromWhereItGot) {
    Document doc = open(bytes_);
    int polls = 0;
    // Expired at the second page: the first page's widgets are in, the list is not finished.
    EXPECT_EQ(doc.form_fields(0, 64, [&polls] { return ++polls > 1; }).status, Status::Timeout);
    auto fields = doc.form_fields(0, 64, kNotExpired);
    ASSERT_EQ(fields.status, Status::Ok);
    EXPECT_EQ(fields.value.total, 14u);  // nothing doubled, nothing lost
}

TEST_F(FormMessageTest, OneFieldIsReadInFull) {
    Document doc = open(bytes_);
    auto name = doc.form_field_detail(kName, 0, kNotExpired);
    ASSERT_EQ(name.status, Status::Ok);
    EXPECT_EQ(name.value.type, 6);
    EXPECT_EQ(name.value.name, "name");
    EXPECT_EQ(name.value.alternate_name, "Full name");
    EXPECT_EQ(name.value.value, "Alice");
    EXPECT_FALSE(name.value.value_truncated);
    EXPECT_EQ(name.value.default_value, "Bob");
    EXPECT_EQ(name.value.max_len, 8);
    EXPECT_EQ(name.value.widget_count, 1u);
    EXPECT_EQ(name.value.options_total, 0u);

    auto country = doc.form_field_detail(kCountry, 0, kNotExpired);
    ASSERT_EQ(country.status, Status::Ok);
    EXPECT_EQ(country.value.type, 4);
    EXPECT_EQ(country.value.options_total, 3u);
    EXPECT_EQ(country.value.option_labels, (std::vector<std::string>{"France", "Germany", "Spain"}));
    EXPECT_EQ(country.value.option_values, country.value.option_labels);
    EXPECT_EQ(country.value.selected_indices, (std::vector<std::uint32_t>{0}));
    EXPECT_EQ(country.value.default_value, "Spain");
    auto window = doc.form_field_detail(kCountry, 2, kNotExpired);
    ASSERT_EQ(window.status, Status::Ok);
    EXPECT_EQ(window.value.option_labels, (std::vector<std::string>{"Spain"}));

    auto langs = doc.form_field_detail(kLangs, 0, kNotExpired);
    ASSERT_EQ(langs.status, Status::Ok);
    EXPECT_EQ(langs.value.type, 5);
    EXPECT_EQ(langs.value.selected_indices, (std::vector<std::uint32_t>{1, 2}));

    auto radio = doc.form_field_detail(kRed, 0, kNotExpired);
    ASSERT_EQ(radio.status, Status::Ok);
    EXPECT_EQ(radio.value.type, 3);
    EXPECT_TRUE(radio.value.checked);
    EXPECT_EQ(radio.value.export_value, "Red");
    EXPECT_EQ(radio.value.widget_count, 2u);

    auto amount = doc.form_field_detail(kAmount, 0, kNotExpired);
    ASSERT_EQ(amount.status, Status::Ok);
    EXPECT_TRUE(amount.value.has_format_script);
    EXPECT_FALSE(amount.value.has_validate_script);
    EXPECT_FALSE(amount.value.has_calculate_script);

    EXPECT_EQ(doc.form_field_detail(14, 0, kNotExpired).status, Status::InvalidArgument);
    EXPECT_EQ(doc.form_field_detail(0, 10'000, kNotExpired).status, Status::InvalidArgument);
}

TEST_F(FormMessageTest, TextGoesInThroughTheFormEventsAndTheFieldEnforcesItsLimit) {
    Document doc = open(bytes_);
    auto set = doc.set_form_field_value(text(kName, "Carol"), kNotExpired);
    ASSERT_EQ(set.status, Status::Ok);
    EXPECT_TRUE(set.value.accepted);
    EXPECT_EQ(set.value.value, "Carol");
    EXPECT_EQ(set.value.affected_pages, (std::vector<std::uint32_t>{0}));
    EXPECT_EQ(value(doc, kName), "Carol");

    // /MaxLen 8: PDFium cuts the text; the reply says what the field holds and that it is not what was asked for.
    auto too_long = doc.set_form_field_value(text(kName, "Bartholomew"), kNotExpired);
    ASSERT_EQ(too_long.status, Status::Ok);
    EXPECT_FALSE(too_long.value.accepted);
    EXPECT_EQ(too_long.value.value, "Bartholo");

    auto cleared = doc.set_form_field_value(text(kName, ""), kNotExpired);
    ASSERT_EQ(cleared.status, Status::Ok);
    EXPECT_TRUE(cleared.value.accepted);
    EXPECT_EQ(value(doc, kName), "");

    auto unicode = doc.set_form_field_value(text(kFree, "Caf\xC3\xA9"), kNotExpired);  // an editable combo box takes text too
    ASSERT_EQ(unicode.status, Status::Ok);
    EXPECT_EQ(value(doc, kFree), "Caf\xC3\xA9");
    EXPECT_EQ(unicode.value.value, "Caf\xC3\xA9");

    auto password = doc.set_form_field_value(text(kPassword, "hunter2"), kNotExpired);
    ASSERT_EQ(password.status, Status::Ok);
    EXPECT_EQ(password.value.value, "hunter2");

    EXPECT_EQ(doc.set_form_field_value(text(kLocked, "x"), kNotExpired).status, Status::PermissionDenied);  // read-only
    EXPECT_EQ(value(doc, kLocked), "fixed");
    EXPECT_EQ(doc.set_form_field_value(text(kAgree, "x"), kNotExpired).status, Status::InvalidArgument);    // a check box takes no text
    EXPECT_EQ(doc.set_form_field_value(text(kCountry, "x"), kNotExpired).status, Status::InvalidArgument);  // nor does a combo box that cannot be edited
    EXPECT_EQ(doc.set_form_field_value(text(99, "x"), kNotExpired).status, Status::InvalidArgument);
}

TEST_F(FormMessageTest, CheckBoxesAndRadioButtonsAreClicked) {
    Document doc = open(bytes_);
    auto on = doc.set_form_field_value(toggle(kAgree, true), kNotExpired);
    ASSERT_EQ(on.status, Status::Ok);
    EXPECT_TRUE(on.value.accepted);
    EXPECT_TRUE(checked(doc, kAgree));
    auto again = doc.set_form_field_value(toggle(kAgree, true), kNotExpired);  // already on: nothing to click
    ASSERT_EQ(again.status, Status::Ok);
    EXPECT_TRUE(again.value.accepted);
    EXPECT_TRUE(checked(doc, kAgree));
    auto off = doc.set_form_field_value(toggle(kAgree, false), kNotExpired);
    ASSERT_EQ(off.status, Status::Ok);
    EXPECT_TRUE(off.value.accepted);
    EXPECT_FALSE(checked(doc, kAgree));

    // FM-12: choosing the blue button turns the red one off.
    auto blue = doc.set_form_field_value(toggle(kBlue, true), kNotExpired);
    ASSERT_EQ(blue.status, Status::Ok);
    EXPECT_TRUE(blue.value.accepted);
    EXPECT_TRUE(checked(doc, kBlue));
    EXPECT_FALSE(checked(doc, kRed));
    EXPECT_EQ(blue.value.affected_pages, (std::vector<std::uint32_t>{0}));

    EXPECT_EQ(doc.set_form_field_value(toggle(kName, true), kNotExpired).status, Status::InvalidArgument);
}

TEST_F(FormMessageTest, ChoicesAreSelectedByPosition) {
    Document doc = open(bytes_);
    auto germany = doc.set_form_field_value(choice(kCountry, {1}), kNotExpired);
    ASSERT_EQ(germany.status, Status::Ok);
    EXPECT_TRUE(germany.value.accepted);
    EXPECT_EQ(germany.value.value, "Germany");
    EXPECT_EQ(doc.form_field_detail(kCountry, 0, kNotExpired).value.selected_indices, (std::vector<std::uint32_t>{1}));

    auto langs = doc.set_form_field_value(choice(kLangs, {0, 3}), kNotExpired);
    ASSERT_EQ(langs.status, Status::Ok);
    EXPECT_TRUE(langs.value.accepted);
    EXPECT_EQ(doc.form_field_detail(kLangs, 0, kNotExpired).value.selected_indices, (std::vector<std::uint32_t>{0, 3}));
    auto none = doc.set_form_field_value(choice(kLangs, {}), kNotExpired);
    ASSERT_EQ(none.status, Status::Ok);
    EXPECT_TRUE(none.value.accepted);
    EXPECT_TRUE(doc.form_field_detail(kLangs, 0, kNotExpired).value.selected_indices.empty());

    EXPECT_EQ(doc.set_form_field_value(choice(kCountry, {3}), kNotExpired).status, Status::InvalidArgument);     // no fourth option
    EXPECT_EQ(doc.set_form_field_value(choice(kCountry, {0, 1}), kNotExpired).status, Status::InvalidArgument);  // a combo box holds one
    EXPECT_EQ(doc.set_form_field_value(choice(kCountry, {}), kNotExpired).status, Status::InvalidArgument);      // and cannot be left without
    EXPECT_EQ(doc.set_form_field_value(choice(kLangs, {2, 1}), kNotExpired).status, Status::InvalidArgument);    // ascending order
    EXPECT_EQ(doc.set_form_field_value(choice(kName, {0}), kNotExpired).status, Status::InvalidArgument);
}

TEST_F(FormMessageTest, WidgetsThatShareAFieldShareItsValueAndItsPages) {
    Document doc = open(bytes_);
    auto set = doc.set_form_field_value(text(kDup1, "both"), kNotExpired);
    ASSERT_EQ(set.status, Status::Ok);
    EXPECT_TRUE(set.value.accepted);
    EXPECT_EQ(set.value.affected_pages, (std::vector<std::uint32_t>{0, 1}));
    EXPECT_FALSE(set.value.truncated);
    EXPECT_EQ(value(doc, kDup2), "both");
    // From the other widget the pages come the other way round: its own first.
    auto back = doc.set_form_field_value(text(kDup2, "again"), kNotExpired);
    ASSERT_EQ(back.status, Status::Ok);
    EXPECT_EQ(back.value.affected_pages, (std::vector<std::uint32_t>{1, 0}));
    EXPECT_EQ(value(doc, kDup1), "again");
}

TEST_F(FormMessageTest, AFieldIsResetToItsDefault) {
    Document doc = open(bytes_);
    ASSERT_EQ(doc.set_form_field_value(text(kName, "Zed"), kNotExpired).status, Status::Ok);
    auto reset = doc.form_action(kName, 0, kNotExpired);
    ASSERT_EQ(reset.status, Status::Ok);
    EXPECT_EQ(reset.value.performed, 1);
    EXPECT_EQ(reset.value.affected_pages, (std::vector<std::uint32_t>{0}));
    EXPECT_EQ(value(doc, kName), "Bob");  // /DV
    auto again = doc.form_action(kName, 0, kNotExpired);  // already at its default
    ASSERT_EQ(again.status, Status::Ok);
    EXPECT_EQ(again.value.performed, 0);
    EXPECT_TRUE(again.value.affected_pages.empty());

    ASSERT_EQ(doc.set_form_field_value(toggle(kAgree, true), kNotExpired).status, Status::Ok);
    ASSERT_EQ(doc.form_action(kAgree, 0, kNotExpired).status, Status::Ok);
    EXPECT_FALSE(checked(doc, kAgree));

    EXPECT_EQ(doc.form_action(kReset, 0, kNotExpired).status, Status::InvalidArgument);   // a button has no value
    EXPECT_EQ(doc.form_action(99, 0, kNotExpired).status, Status::InvalidArgument);
}

TEST_F(FormMessageTest, EveryFieldIsResetAtOnceAndAReadOnlyOneIsLeftAlone) {
    Document doc = open(bytes_);
    ASSERT_EQ(doc.set_form_field_value(text(kName, "Zed"), kNotExpired).status, Status::Ok);
    ASSERT_EQ(doc.set_form_field_value(toggle(kAgree, true), kNotExpired).status, Status::Ok);
    ASSERT_EQ(doc.set_form_field_value(choice(kCountry, {1}), kNotExpired).status, Status::Ok);
    ASSERT_EQ(doc.set_form_field_value(text(kDup2, "two"), kNotExpired).status, Status::Ok);
    auto all = doc.form_action(0, 1, kNotExpired);
    ASSERT_EQ(all.status, Status::Ok);
    EXPECT_EQ(all.value.performed, 1);
    EXPECT_NE(std::find(all.value.affected_pages.begin(), all.value.affected_pages.end(), 0u), all.value.affected_pages.end());
    EXPECT_NE(std::find(all.value.affected_pages.begin(), all.value.affected_pages.end(), 1u), all.value.affected_pages.end());
    EXPECT_EQ(value(doc, kName), "Bob");
    EXPECT_FALSE(checked(doc, kAgree));
    EXPECT_EQ(value(doc, kCountry), "Spain");  // its /DV
    EXPECT_EQ(value(doc, kDup1), "");
    EXPECT_EQ(value(doc, kLocked), "fixed");
    EXPECT_EQ(doc.form_action(0, 1, [] { return true; }).status, Status::Timeout);
}

TEST_F(FormMessageTest, ThePushButtonThatResetsResetsAndTheOneThatSubmitsDoesNothing) {
    // FM-18: pressing a button runs no script and sends nothing; a reset is all there is.
    Document doc = open(bytes_);
    ASSERT_EQ(doc.set_form_field_value(text(kName, "Zed"), kNotExpired).status, Status::Ok);
    auto send = doc.form_action(kSend, 2, kNotExpired);
    ASSERT_EQ(send.status, Status::Ok);
    EXPECT_EQ(send.value.performed, 2);   // refused as unsupported
    EXPECT_TRUE(send.value.affected_pages.empty());
    EXPECT_EQ(value(doc, kName), "Zed");

    auto reset = doc.form_action(kReset, 2, kNotExpired);
    ASSERT_EQ(reset.status, Status::Ok);
    EXPECT_EQ(reset.value.performed, 1);
    EXPECT_EQ(reset.value.affected_pages, (std::vector<std::uint32_t>{0, 1}));  // the form's other field, dup, is on page 2 and goes back to empty as well
    EXPECT_EQ(value(doc, kName), "Bob");
    EXPECT_EQ(value(doc, kDup2), "");

    EXPECT_EQ(doc.form_action(kName, 2, kNotExpired).status, Status::InvalidArgument);  // not a button
}

TEST_F(FormMessageTest, TheValuesComeBackFromASavedFile) {
    // FM-06: what was entered through the events is in the file, and a fresh PDFium reads it.
    for (const bool incremental : {true, false}) {
        Document doc = open(bytes_);
        ASSERT_EQ(doc.set_form_field_value(text(kName, "Dave"), kNotExpired).status, Status::Ok);
        ASSERT_EQ(doc.set_form_field_value(toggle(kAgree, true), kNotExpired).status, Status::Ok);
        ASSERT_EQ(doc.set_form_field_value(choice(kCountry, {2}), kNotExpired).status, Status::Ok);
        ASSERT_EQ(doc.set_form_field_value(choice(kLangs, {0}), kNotExpired).status, Status::Ok);
        ASSERT_EQ(doc.set_form_field_value(text(kDup1, "shared too"), kNotExpired).status, Status::Ok);
        const auto saved = save(doc, incremental);
        ASSERT_GT(saved.size(), 0u);
        if (incremental) {
            ASSERT_GT(saved.size(), bytes_.size());
            EXPECT_EQ(std::memcmp(saved.data(), bytes_.data(), bytes_.size()), 0) << "the original bytes are a prefix of an incremental save";
        }
        Document reopened = open(saved);
        EXPECT_EQ(value(reopened, kName), "Dave") << incremental;
        EXPECT_TRUE(checked(reopened, kAgree)) << incremental;
        EXPECT_EQ(value(reopened, kCountry), "Spain") << incremental;
        EXPECT_EQ(reopened.form_field_detail(kLangs, 0, kNotExpired).value.selected_indices, (std::vector<std::uint32_t>{0})) << incremental;
        EXPECT_EQ(value(reopened, kDup2), "shared too") << incremental;
        EXPECT_EQ(value(reopened, kLocked), "fixed") << incremental;
    }
}

TEST_F(FormMessageTest, ATypedValueNotYetCommittedIsKeptBySaving) {
    // The focus is still on a field when the save comes: saving takes it away, which commits what was typed.
    Document doc = open(bytes_);
    ASSERT_EQ(doc.set_form_field_value(text(kName, "Eve"), kNotExpired).status, Status::Ok);
    const auto saved = save(doc, true);
    Document reopened = open(saved);
    EXPECT_EQ(value(reopened, kName), "Eve");
}

TEST_F(FormMessageTest, TheFieldHighlightIsDrawnOnlyWhenAskedForAndNeverAfterwards) {
    // FM-16. acroform_filled.pdf has a text field with no appearance at x 200..380, y 20..60 of the 1x picture; the tint covers the whole widget.
    auto bytes = support::read_fixture("acroform_filled.pdf");
    Document doc = open(bytes);
    const auto tile = [&doc](std::uint32_t flags, Status expected = Status::Ok) {
        TileRequest request;
        request.scale = 1.0f;
        request.tile_width = 400;
        request.tile_height = 300;
        request.flags = flags;
        std::vector<std::uint8_t> buffer(std::size_t{400} * 300 * 4, 0x55);
        EXPECT_EQ(doc.render_tile(request, buffer, 400 * 4, kNever), expected);
        return buffer;
    };
    const auto is_white = [](const support::Bgra& p) { return p.b == 255 && p.g == 255 && p.r == 255; };
    const std::uint32_t annots_and_forms = pdfium::kRenderAnnotations | pdfium::kRenderFormFields;
    const auto plain = tile(annots_and_forms);
    EXPECT_TRUE(is_white(pixel(plain, 400 * 4, 372, 55)));  // empty corner of the widget
    const auto tinted = tile(annots_and_forms | pdfium::kRenderFieldHighlight);
    const auto tint = pixel(tinted, 400 * 4, 372, 55);
    EXPECT_FALSE(is_white(tint)) << "the highlight tints the widget";
    EXPECT_TRUE(is_white(pixel(tinted, 400 * 4, 395, 150)));  // and nothing else on the page
    const auto after = tile(annots_and_forms);
    EXPECT_TRUE(is_white(pixel(after, 400 * 4, 372, 55)));  // switched off again for the next tile

    // The highlight is a part of the form layer: alone it is a mistake, and bit 4 is not defined.
    tile(pdfium::kRenderAnnotations | pdfium::kRenderFieldHighlight, Status::InvalidArgument);
    tile(annots_and_forms | 16, Status::InvalidArgument);
}

}  // namespace
