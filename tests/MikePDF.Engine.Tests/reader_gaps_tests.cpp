// Tests for the reader gaps (docs/features/m2-reader-gaps.md): named destinations (N1 to N7), and the engine side of the properties dialog, the
// GetDocumentDetails message of schema 4 (P2, P3). Every reply is also checked against its schema limits.
#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

#include "engine_core.h"
#include "mikepdf/pdfium/document.h"
#include "mikepdf/pdfium/library.h"
#include "test_support.h"

namespace {

using namespace mikepdf;
using engine::Clock;
using engine::EngineCore;
using engine::EngineKind;
using pdfium::Document;
using protocol::MessageType;
using protocol::Status;
using support::PdfBuilder;

std::vector<std::uint8_t> bytes(const std::string& text) { return {text.begin(), text.end()}; }

const auto kNever = [] { return false; };

// Three pages. Named destinations live in the name tree (two kids with /Limits, a value that is a dictionary with /D) and in the old catalog
// /Dests dictionary. Page 1 has a link of every kind the work package names; the outline has one bookmark per form.
//   alpha   -> page 2, [/XYZ 50 600 0]            (name tree, array value)
//   beta    -> page 3, [/FitH 123]                (name tree, dictionary value with /D)
//   OldName -> page 2, [/Fit]                     (catalog /Dests dictionary)
std::string named_pdf() {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int page_a = pdf.reserve();
    const int page_b = pdf.reserve();
    const int page_c = pdf.reserve();
    const int tree_root = pdf.reserve();
    const int kid_one = pdf.reserve();
    const int kid_two = pdf.reserve();
    const std::string b = std::to_string(page_b) + " 0 R";
    const std::string c = std::to_string(page_c) + " 0 R";

    pdf.set(kid_one, "<< /Limits [(alpha) (alpha)] /Names [(alpha) [" + b + " /XYZ 50 600 0]] >>");
    pdf.set(kid_two, "<< /Limits [(beta) (beta)] /Names [(beta) << /D [" + c + " /FitH 123] >>] >>");
    pdf.set(tree_root, "<< /Kids [" + std::to_string(kid_one) + " 0 R " + std::to_string(kid_two) + " 0 R] >>");

    const auto link = [&](const std::string& rect, const std::string& rest) {
        return pdf.add("<< /Type /Annot /Subtype /Link /Rect [" + rect + "] /Border [0 0 0] " + rest + " >>");
    };
    const int l0 = link("10 10 40 30", "/Dest (alpha)");
    const int l1 = link("50 10 80 30", "/A << /S /GoTo /D (beta) >>");
    const int l2 = link("90 10 120 30", "/Dest /OldName");
    const int l3 = link("130 10 160 30", "/Dest (nonesuch)");
    const int l4 = link("170 10 200 30", "/A << /S /GoToR /F (other.pdf) /D (alpha) >>");
    const int l5 = link("210 10 240 30", "/A << /S /Launch /F (calc.exe) >>");
    const int l6 = link("250 10 280 30", "/A << /S /Named /N /NextPage >>");
    const int l7 = link("290 10 320 30", "/A << /S /GoTo /D /OldName >>");
    std::string annots;
    for (const int l : {l0, l1, l2, l3, l4, l5, l6, l7}) {
        annots += std::to_string(l) + " 0 R ";
    }

    const int outlines = pdf.reserve();
    const int by_name = pdf.reserve();
    const int by_action = pdf.reserve();
    const int unknown = pdf.reserve();
    const int remote = pdf.reserve();
    pdf.set(outlines, "<< /Type /Outlines /First " + std::to_string(by_name) + " 0 R /Last " + std::to_string(remote) + " 0 R /Count 4 >>");
    pdf.set(by_name, "<< /Title (By name) /Parent " + std::to_string(outlines) + " 0 R /Next " + std::to_string(by_action) + " 0 R /Dest (alpha) >>");
    pdf.set(by_action, "<< /Title (By action) /Parent " + std::to_string(outlines) + " 0 R /Prev " + std::to_string(by_name) + " 0 R /Next " +
                           std::to_string(unknown) + " 0 R /A << /S /GoTo /D (beta) >> >>");
    pdf.set(unknown, "<< /Title (Unknown) /Parent " + std::to_string(outlines) + " 0 R /Prev " + std::to_string(by_action) + " 0 R /Next " +
                         std::to_string(remote) + " 0 R /Dest (nope) >>");
    pdf.set(remote, "<< /Title (Remote) /Parent " + std::to_string(outlines) + " 0 R /Prev " + std::to_string(unknown) +
                        " 0 R /A << /S /GoToR /F (other.pdf) /D (alpha) >> >>");

    pdf.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages) + " 0 R /Outlines " + std::to_string(outlines) + " 0 R /Names << /Dests " +
                         std::to_string(tree_root) + " 0 R >> /Dests << /OldName [" + b + " /Fit] >> >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page_a) + " 0 R " + b + " " + c + "] /Count 3 >>");
    pdf.set(page_a, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /MediaBox [0 0 612 792] /Annots [" + annots + "] >>");
    pdf.set(page_b, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /MediaBox [0 0 300 400] >>");
    pdf.set(page_c, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /MediaBox [0 0 500 500] >>");
    return pdf.build(catalog);
}

class NamedDestinationTest : public ::testing::Test {
protected:
    NamedDestinationTest() : storage_(bytes(named_pdf())) {
        auto opened = Document::open(storage_, {});
        EXPECT_EQ(opened.status, Status::Ok);
        doc_ = std::move(opened.value);
    }

    pdfium::Library library_;
    std::vector<std::uint8_t> storage_;
    Document doc_;
};

TEST_F(NamedDestinationTest, ALinkToANameInTheNameTreeGoesToItsPageAndPoint) {
    const auto r = doc_.page_links(0, 0, 512);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    ASSERT_EQ(r.value.kinds.size(), 8u);
    EXPECT_EQ(r.value.kinds[0], 3);  // [/XYZ 50 600 0]: a page, x and y
    EXPECT_EQ(r.value.pages[0], 1u);
    EXPECT_FLOAT_EQ(r.value.xs[0], 50.f);
    EXPECT_FLOAT_EQ(r.value.ys[0], 600.f);
}

TEST_F(NamedDestinationTest, AGoToActionAndADictionaryValueResolve) {
    const auto r = doc_.page_links(0, 0, 512);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.kinds[1], 2);  // << /D [/FitH 123] >> reached through /A /GoTo /D (beta)
    EXPECT_EQ(r.value.pages[1], 2u);
    EXPECT_FLOAT_EQ(r.value.ys[1], 123.f);
}

TEST_F(NamedDestinationTest, ANameInTheOldCatalogDestsDictionaryResolvesForALinkAndForAnAction) {
    const auto r = doc_.page_links(0, 0, 512);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.kinds[2], 1);  // /Dest /OldName -> [/Fit]
    EXPECT_EQ(r.value.pages[2], 1u);
    EXPECT_EQ(r.value.kinds[7], 1);  // /A << /S /GoTo /D /OldName >>
    EXPECT_EQ(r.value.pages[7], 1u);
}

TEST_F(NamedDestinationTest, BookmarksResolveANameAndAGoToActionAndAnUnknownNameGoesNowhere) {
    const auto r = doc_.outline({}, 0, 64);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    ASSERT_EQ(r.value.titles.size(), 4u);
    EXPECT_EQ(r.value.kinds[0], 3);  // /Dest (alpha)
    EXPECT_EQ(r.value.pages[0], 1u);
    EXPECT_FLOAT_EQ(r.value.xs[0], 50.f);
    EXPECT_FLOAT_EQ(r.value.ys[0], 600.f);
    EXPECT_EQ(r.value.kinds[1], 2);  // /A << /S /GoTo /D (beta) >>
    EXPECT_EQ(r.value.pages[1], 2u);
    EXPECT_EQ(r.value.kinds[2], 0);  // /Dest (nope)
    EXPECT_EQ(r.value.kinds[3], 5);  // /A /GoToR: a name in another file
    EXPECT_EQ(r.value.pages[3], 0u);
}

TEST_F(NamedDestinationTest, AnUnknownNameGivesNoDestinationAndIsNotMistakenForAnAction) {
    const auto r = doc_.page_links(0, 0, 512);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.kinds[3], 0);
    EXPECT_EQ(r.value.pages[3], 0u);
}

TEST_F(NamedDestinationTest, RemoteLaunchAndNamedActionsStayActionsMikePDFDoesNotFollow) {
    const auto r = doc_.page_links(0, 0, 512);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.kinds[4], 5);  // GoToR: the name would be looked up in another file, never in this one
    EXPECT_EQ(r.value.pages[4], 0u);
    EXPECT_EQ(r.value.kinds[5], 5);  // Launch
    EXPECT_EQ(r.value.kinds[6], 5);  // Named action
    EXPECT_FALSE(doc_.link_uri(0, 4).ok());
    EXPECT_FALSE(doc_.link_uri(0, 5).ok());
}

TEST(NamedDestinationHostile, ANameTreeThatPointsBackToItselfEndsAndGivesNoDestination) {
    pdfium::Library library;
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int page = pdf.reserve();
    const int loop = pdf.reserve();
    const int a = pdf.reserve();
    const int b = pdf.reserve();
    pdf.set(loop, "<< /Limits [(a) (z)] /Kids [" + std::to_string(loop) + " 0 R " + std::to_string(a) + " 0 R] >>");
    pdf.set(a, "<< /Limits [(a) (z)] /Kids [" + std::to_string(b) + " 0 R] >>");
    pdf.set(b, "<< /Limits [(a) (z)] /Kids [" + std::to_string(a) + " 0 R] >>");
    const int l0 = pdf.add("<< /Type /Annot /Subtype /Link /Rect [10 10 40 30] /Dest (mmm) >>");
    pdf.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages) + " 0 R /Names << /Dests " + std::to_string(loop) + " 0 R >> >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    pdf.set(page, "<< /Type /Page /Parent " + std::to_string(pages) + " 0 R /MediaBox [0 0 100 100] /Annots [" + std::to_string(l0) + " 0 R] >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto started = Clock::now();
    const auto r = opened.value.page_links(0, 0, 512);
    ASSERT_EQ(r.status, Status::Ok);
    ASSERT_EQ(r.value.kinds.size(), 1u);
    EXPECT_EQ(r.value.kinds[0], 0);
    EXPECT_LT(Clock::now() - started, std::chrono::seconds(5));
}

// ---- GetDocumentDetails -----------------------------------------------------------------------------------------------------

std::string text_pdf(const std::string& info, const std::string& fonts_and_content, int pages = 1) {
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages_node = pdf.reserve();
    const int info_object = info.empty() ? 0 : pdf.add("<< " + info + " >>");
    std::vector<int> page_numbers;
    for (int i = 0; i < pages; ++i) {
        page_numbers.push_back(pdf.reserve());
    }
    std::string kids;
    for (const int page : page_numbers) {
        kids += std::to_string(page) + " 0 R ";
    }
    pdf.set(catalog, "<< /Type /Catalog /Pages " + std::to_string(pages_node) + " 0 R >>");
    pdf.set(pages_node, "<< /Type /Pages /Kids [" + kids + "] /Count " + std::to_string(pages) + " >>");
    for (const int page : page_numbers) {
        const int content = pdf.add_stream("BT /F1 12 Tf 10 50 Td (Hello) Tj ET\nBT /F2 12 Tf 10 30 Td (World) Tj ET");
        pdf.set(page, "<< /Type /Page /Parent " + std::to_string(pages_node) + " 0 R /MediaBox [0 0 200 100] /Resources << /Font << " + fonts_and_content +
                          " >> >> /Contents " + std::to_string(content) + " 0 R >>");
    }
    return pdf.build(catalog, info_object == 0 ? "" : "/Info " + std::to_string(info_object) + " 0 R");
}

const std::string kTwoFonts = "/F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> /F2 << /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>";

TEST(DocumentDetails, DatesFormTypeAndFontsAreReportedWithinTheSchemaLimits) {
    pdfium::Library library;
    const auto data = bytes(text_pdf("/CreationDate (D:20240115103000+10'00') /ModDate (D:20240201)", kTwoFonts));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.details(100, kNever);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    EXPECT_EQ(r.value.creation_date, "D:20240115103000+10'00'");
    EXPECT_EQ(r.value.modification_date, "D:20240201");
    EXPECT_EQ(r.value.security_revision, 0u);
    EXPECT_EQ(r.value.user_permissions, 0xFFFFFFFFu);
    EXPECT_EQ(r.value.form_type, 0u);
    ASSERT_EQ(r.value.font_names.size(), 2u);
    EXPECT_EQ(r.value.font_names[0], "Helvetica");
    EXPECT_EQ(r.value.font_names[1], "Times-Roman");
    EXPECT_EQ((std::vector<std::uint16_t>{0, 0}), r.value.font_flags);  // the standard 14 are not embedded
    EXPECT_TRUE(r.value.fonts_complete);
    EXPECT_EQ(r.value.font_pages_scanned, 1u);
}

// PDFium's encrypted.pdf has a user password ("1234") and an owner password ("5678"); its /P (-3392) does not allow copying.
TEST(DocumentDetails, AnEncryptedDocumentGivesItsRevisionAndTheUserPermissions) {
    pdfium::Library library;
    const auto file = support::read_fixture("encrypted.pdf");
    auto opened = Document::open(file, "1234");
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.details(100, kNever);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    EXPECT_EQ(r.value.security_revision, 4u);
    EXPECT_EQ(r.value.user_permissions & 0x10u, 0u);  // bit 5, copy, is not set in the file's /P
    const auto info = opened.value.info();
    ASSERT_EQ(info.status, Status::Ok);
    EXPECT_EQ(info.value.permissions & 0x10u, 0u);  // opened with the user password, the session is restricted too
}

TEST(DocumentDetails, OpenedWithTheOwnerPasswordTheSessionIsNotRestrictedButTheUserPermissionsStillAre) {
    pdfium::Library library;
    const auto file = support::read_fixture("encrypted.pdf");
    auto opened = Document::open(file, "5678");
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.details(100, kNever);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.user_permissions & 0x10u, 0u);
    const auto info = opened.value.info();
    ASSERT_EQ(info.status, Status::Ok);
    EXPECT_EQ(info.value.permissions & 0x10u, 0x10u);  // the owner may do anything
}

TEST(DocumentDetails, OverLongDatesAreCutAtTheirLimit) {
    pdfium::Library library;
    const auto data = bytes(text_pdf("/CreationDate (" + std::string(300, '7') + ") /ModDate (" + std::string(70, '8') + ") /Title (" + std::string(5000, 't') + ")", kTwoFonts));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.details(100, kNever);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    EXPECT_EQ(r.value.creation_date.size(), 64u);
    EXPECT_EQ(r.value.modification_date.size(), 64u);
    const auto info = opened.value.info();
    ASSERT_EQ(info.status, Status::Ok);
    EXPECT_LE(info.value.title.size(), 1024u);
    EXPECT_EQ(info.value.validate(), Status::Ok);
}

TEST(DocumentDetails, AFontNameIsPrintableAsciiAndAtMostTheLimit) {
    pdfium::Library library;
    // PDF names can hold any byte through #xx escapes, and be long.
    const std::string hostile = "/F1 << /Type /Font /Subtype /Type1 /BaseFont /Bad#01#FF#7FName >> /F2 << /Type /Font /Subtype /Type1 /BaseFont /" + std::string(300, 'L') + " >>";
    const auto data = bytes(text_pdf("", hostile));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.details(100, kNever);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    ASSERT_EQ(r.value.font_names.size(), 2u);
    for (const auto& name : r.value.font_names) {
        EXPECT_LE(name.size(), 128u);
        for (const unsigned char c : name) {
            EXPECT_TRUE(c >= 0x20 && c < 0x7F) << name;
        }
    }
    EXPECT_EQ(r.value.font_names[0], "Bad???Name");
    EXPECT_EQ(r.value.font_names[1].size(), 128u);
}

TEST(DocumentDetails, AShortBudgetOrAPageCapMakesTheFontListIncompleteAndSaysHowFarItGot) {
    pdfium::Library library;
    const auto data = bytes(text_pdf("", kTwoFonts, 5));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);

    const auto capped = opened.value.details(2, kNever);
    ASSERT_EQ(capped.status, Status::Ok);
    EXPECT_FALSE(capped.value.fonts_complete);
    EXPECT_EQ(capped.value.font_pages_scanned, 2u);
    EXPECT_EQ(capped.value.font_names.size(), 2u);

    const auto expired = opened.value.details(100, [] { return true; });
    ASSERT_EQ(expired.status, Status::Ok);
    EXPECT_FALSE(expired.value.fonts_complete);
    EXPECT_EQ(expired.value.font_pages_scanned, 0u);
    EXPECT_TRUE(expired.value.font_names.empty());
    EXPECT_EQ(expired.value.validate(), Status::Ok);

    const auto whole = opened.value.details(100, kNever);
    ASSERT_EQ(whole.status, Status::Ok);
    EXPECT_TRUE(whole.value.fonts_complete);
    EXPECT_EQ(whole.value.font_pages_scanned, 5u);
}

TEST(DocumentDetails, MoreFontsThanTheReplyHoldsAreCutAndMarkedIncomplete) {
    pdfium::Library library;
    std::string fonts;
    std::string content;
    for (int i = 0; i < 70; ++i) {
        fonts += "/F" + std::to_string(i) + " << /Type /Font /Subtype /Type1 /BaseFont /Font" + std::to_string(i) + " >> ";
        content += "BT /F" + std::to_string(i) + " 12 Tf 10 50 Td (x) Tj ET\n";
    }
    PdfBuilder pdf;
    const int catalog = pdf.reserve();
    const int pages = pdf.reserve();
    const int page = pdf.reserve();
    const int stream = pdf.add_stream(content);
    pdf.set(catalog, "<< /Type /Catalog /Pages 2 0 R >>");
    pdf.set(pages, "<< /Type /Pages /Kids [" + std::to_string(page) + " 0 R] /Count 1 >>");
    pdf.set(page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << " + fonts + " >> >> /Contents " + std::to_string(stream) + " 0 R >>");
    const auto data = bytes(pdf.build(catalog));
    auto opened = Document::open(data, {});
    ASSERT_EQ(opened.status, Status::Ok);
    const auto r = opened.value.details(100, kNever);
    ASSERT_EQ(r.status, Status::Ok);
    EXPECT_EQ(r.value.validate(), Status::Ok);
    EXPECT_EQ(r.value.font_names.size(), 64u);
    EXPECT_FALSE(r.value.fonts_complete);
}

TEST(DocumentDetails, TheCoreServesItAndRefusesWhatIsNotAsked) {
    pdfium::Library library;
    EngineCore core(EngineKind::Document);
    protocol::RequestEnvelope open;
    open.request_id = 1;
    open.message_type = MessageType::OpenDocument;
    protocol::OpenDocument payload;
    payload.handle = support::open_handle(support::fixture_path("hello_world.pdf"));
    open.payload = payload;
    auto opened = core.execute(open, Clock::time_point::max());
    ASSERT_EQ(opened.status, Status::Ok);
    const auto id = std::get<protocol::OpenDocumentReply>(*opened.payload).document_id;

    protocol::RequestEnvelope ask;
    ask.request_id = 2;
    ask.message_type = MessageType::GetDocumentDetails;
    ask.document_id = id;
    ask.payload = protocol::GetDocumentDetails{};
    const auto reply = core.execute(ask, Clock::now() + std::chrono::seconds(10));
    ASSERT_EQ(reply.status, Status::Ok);
    ASSERT_TRUE(reply.payload.has_value());
    const auto& details = std::get<protocol::GetDocumentDetailsReply>(*reply.payload);
    EXPECT_EQ(details.validate(), Status::Ok);

    EXPECT_EQ(core.execute(ask, Clock::now() - std::chrono::seconds(1)).status, Status::Timeout);
    ask.document_id = id + 100;
    EXPECT_EQ(core.execute(ask, Clock::now() + std::chrono::seconds(10)).status, Status::InvalidArgument);
    core.close_all();
}

}  // namespace
