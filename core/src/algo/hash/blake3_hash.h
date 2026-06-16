#pragma once
#include "roulin/hasher.h"
#include <memory>

namespace roulin {

class Blake3Hash final : public IHash {
public:
    Blake3Hash();
    ~Blake3Hash() override;

    void   Update(const uint8_t* data, size_t len) override;
    Hash32 Finalize() override;

private:
    struct Impl;
    std::unique_ptr<Impl> mImpl;
};

} // namespace roulin
