#include <gtest/gtest.h>
#include "roulin/index.h"
#include "roulin/parcel.h"
#include <filesystem>

namespace {

// Returns the Address record inside `entry` matching `address_str`. Tests use
// this to verify per-address attributes after Get/GetByHash returns the
// containing IndexEntry.
const roulin::Address* AddressOf(const roulin::IndexEntry& e,
                                    const std::string&         address_str) {
    for (const auto& a : e.addresses) {
        if (a.address_str == address_str) return &a;
    }
    return nullptr;
}

roulin::IndexEntry MakeEntry(const std::string&         addr,
                                const roulin::Hash32&      hash,
                                uint8_t                     flags = 0,
                                uint16_t                    key_id = 0) {
    roulin::IndexEntry e;
    e.blob_hash = hash;
    roulin::Address a;
    a.address_str = addr;
    a.flags       = flags;
    a.key_id      = key_id;
    e.addresses.push_back(std::move(a));
    return e;
}

} // namespace

// ---- Index tests -----------------------------------------------------------

TEST(IndexTest, BuildAndGet) {
    roulin::IndexBuilder builder;

    roulin::Hash32 ha{}; ha.fill(0xAA);
    roulin::Hash32 hb{}; hb.fill(0xBB);

    builder.Add(MakeEntry("ui/icons/player", ha));
    builder.Add(MakeEntry("audio/bgm/main", hb,
                          roulin::Flags::Compressed));

    auto index = roulin::Index::FromBytes(builder.Build());
    EXPECT_EQ(index.EntryCount(), 2u);

    auto r1 = index.Get("ui/icons/player");
    ASSERT_TRUE(r1.has_value());
    EXPECT_EQ(r1->blob_hash, ha);
    auto a1 = AddressOf(*r1, "ui/icons/player");
    ASSERT_NE(a1, nullptr);
    EXPECT_EQ(a1->address_str, "ui/icons/player");

    auto r2 = index.Get("audio/bgm/main");
    ASSERT_TRUE(r2.has_value());
    EXPECT_EQ(r2->blob_hash, hb);
    auto a2 = AddressOf(*r2, "audio/bgm/main");
    ASSERT_NE(a2, nullptr);
    EXPECT_EQ(a2->flags & roulin::Flags::Compressed, roulin::Flags::Compressed);
}

TEST(IndexTest, MissingAddressReturnsNullopt) {
    roulin::IndexBuilder builder;
    roulin::Hash32 h{}; h.fill(0x01);
    builder.Add(MakeEntry("exists/path", h));

    auto index = roulin::Index::FromBytes(builder.Build());
    EXPECT_FALSE(index.Get("does/not/exist").has_value());
}

TEST(IndexTest, InsertionOrderDoesNotMatterForLookup) {
    // Entries added in arbitrary order must all be findable after Build()
    // sorts them by blob_hash.
    roulin::IndexBuilder builder;
    for (const char* addr : {"z/last", "a/first", "m/middle"}) {
        roulin::Hash32 h{};
        h.fill(static_cast<uint8_t>(addr[0]));
        builder.Add(MakeEntry(addr, h));
    }
    auto index = roulin::Index::FromBytes(builder.Build());
    EXPECT_EQ(index.EntryCount(), 3u);
    EXPECT_TRUE(index.Get("z/last").has_value());
    EXPECT_TRUE(index.Get("a/first").has_value());
    EXPECT_TRUE(index.Get("m/middle").has_value());
}

TEST(IndexTest, KeyIdAndFlagsRoundtrip) {
    roulin::IndexBuilder builder;
    roulin::Hash32 h{}; h.fill(0x99);
    builder.Add(MakeEntry("secret/asset", h,
                          roulin::Flags::Encrypted | roulin::Flags::Compressed,
                          /*key_id*/ 42));

    auto r = roulin::Index::FromBytes(builder.Build()).Get("secret/asset");
    ASSERT_TRUE(r.has_value());
    auto a = AddressOf(*r, "secret/asset");
    ASSERT_NE(a, nullptr);
    EXPECT_EQ(a->flags,  roulin::Flags::Encrypted | roulin::Flags::Compressed);
    EXPECT_EQ(a->key_id, 42u);
}

TEST(IndexTest, SizeBytesRoundtrip) {
    // size_bytes powers Addressables.GetDownloadSizeAsync via ILocationSizeData.
    roulin::IndexBuilder builder;

    roulin::Hash32 ha{}; ha.fill(0xAA);
    roulin::IndexEntry e1 = MakeEntry("ui/icons/player", ha);
    e1.size_bytes = 12345;

    roulin::Hash32 hb{}; hb.fill(0xBB);
    roulin::IndexEntry e2 = MakeEntry("ui/icons/enemy", hb);
    e2.size_bytes = 0;  // unset → must round-trip as 0

    builder.Add(e1);
    builder.Add(e2);

    auto index = roulin::Index::FromBytes(builder.Build());

    auto r1 = index.Get("ui/icons/player");
    ASSERT_TRUE(r1.has_value());
    EXPECT_EQ(r1->size_bytes, 12345u);

    auto r2 = index.Get("ui/icons/enemy");
    ASSERT_TRUE(r2.has_value());
    EXPECT_EQ(r2->size_bytes, 0u);
}

TEST(IndexTest, MultipleAddressesShareOneBlob) {
    // 1 IndexEntry can host N Address records that all live in the same blob.
    roulin::IndexBuilder builder;
    roulin::Hash32 h{}; h.fill(0x77);

    roulin::IndexEntry e;
    e.blob_hash  = h;
    e.size_bytes = 1024;
    {
        roulin::Address a;
        a.address_str = "ui/icons/player";
        e.addresses.push_back(a);
    }
    {
        roulin::Address a;
        a.address_str = "ui/icons/enemy";
        e.addresses.push_back(a);
    }
    builder.Add(e);

    auto index = roulin::Index::FromBytes(builder.Build());
    EXPECT_EQ(index.EntryCount(), 1u);

    auto r1 = index.Get("ui/icons/player");
    auto r2 = index.Get("ui/icons/enemy");
    ASSERT_TRUE(r1.has_value());
    ASSERT_TRUE(r2.has_value());
    EXPECT_EQ(r1->blob_hash,  r2->blob_hash);
    EXPECT_EQ(r1->size_bytes, r2->size_bytes);
}

TEST(IndexTest, EmptyAddressesIsValid) {
    // An IndexEntry without any addresses (= a pure dep target).
    roulin::IndexBuilder builder;
    roulin::Hash32 h{}; h.fill(0x33);
    roulin::IndexEntry e;
    e.blob_hash  = h;
    e.size_bytes = 256;
    builder.Add(e);

    auto index = roulin::Index::FromBytes(builder.Build());
    EXPECT_EQ(index.EntryCount(), 1u);

    auto r = index.GetByHash(h);
    ASSERT_TRUE(r.has_value());
    EXPECT_TRUE(r->addresses.empty());
    EXPECT_EQ(r->size_bytes, 256u);
}

TEST(IndexTest, DepsRoundtrip) {
    // Per-blob deps round-trip via IndexEntry.deps.
    roulin::IndexBuilder builder;
    roulin::Hash32 ha{}; ha.fill(0xAA);
    roulin::Hash32 hb{}; hb.fill(0xBB);
    roulin::IndexEntry e = MakeEntry("ui/icons/player", ha);
    e.deps = {"deadbeef", "feedface"};
    builder.Add(e);
    // Empty-deps entry round-trips with deps.empty() == true.
    builder.Add(MakeEntry("audio/bgm", hb));

    auto index = roulin::Index::FromBytes(builder.Build());
    auto r1 = index.Get("ui/icons/player");
    ASSERT_TRUE(r1.has_value());
    ASSERT_EQ(r1->deps.size(), 2u);
    EXPECT_EQ(r1->deps[0], "deadbeef");
    EXPECT_EQ(r1->deps[1], "feedface");

    auto r2 = index.Get("audio/bgm");
    ASSERT_TRUE(r2.has_value());
    EXPECT_TRUE(r2->deps.empty());
}

// ---- Parcel tests ----------------------------------------------------------

class ParcelTest : public ::testing::Test {
protected:
    void SetUp() override {
        mTmpDir = std::filesystem::temp_directory_path()
                / ("roulin_parcel_" + std::to_string(reinterpret_cast<uintptr_t>(this)));
        std::filesystem::remove_all(mTmpDir);
        std::filesystem::create_directories(mTmpDir);
    }
    void TearDown() override { std::filesystem::remove_all(mTmpDir); }

    std::string TmpDir() const { return mTmpDir.string(); }

    std::filesystem::path mTmpDir;
};

TEST_F(ParcelTest, OpenAndGet) {
    const std::string rev = "abc123def456";

    roulin::IndexBuilder ib;
    roulin::Hash32 h{}; h.fill(0x42);
    ib.Add(MakeEntry("ui/icons/player", h));
    ib.SaveToFile(TmpDir() + "/index/" + rev);

    auto parcel = roulin::Parcel::Open(TmpDir(), rev);
    EXPECT_EQ(parcel.RevisionId(), rev);

    auto r = parcel.Get("ui/icons/player");
    ASSERT_TRUE(r.has_value());
    EXPECT_EQ(r->blob_hash, h);
    auto a = AddressOf(*r, "ui/icons/player");
    ASSERT_NE(a, nullptr);
    EXPECT_EQ(a->address_str, "ui/icons/player");
}

TEST_F(ParcelTest, GetMissingAddress) {
    const std::string rev = "rev001";
    roulin::IndexBuilder ib;
    roulin::Hash32 h{}; h.fill(0x01);
    ib.Add(MakeEntry("exists", h));
    ib.SaveToFile(TmpDir() + "/index/" + rev);

    auto parcel = roulin::Parcel::Open(TmpDir(), rev);
    EXPECT_FALSE(parcel.Get("does_not_exist").has_value());
}

TEST_F(ParcelTest, MissingIndexThrows) {
    EXPECT_THROW(roulin::Parcel::Open(TmpDir(), "nonexistent"), std::runtime_error);
}
