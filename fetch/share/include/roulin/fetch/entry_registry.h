#pragma once
#include "roulin/fetch/session.h"  // Handle

#include <memory>
#include <mutex>
#include <unordered_map>

namespace roulin::fetch {

// All methods acquire mMu. WithEntry / ForEach callbacks run with the mutex
// held — they must not re-enter the registry on the same thread.
template <class E>
class EntryRegistry {
public:
    void Insert(Handle h, std::unique_ptr<E> entry) {
        std::lock_guard<std::mutex> lk(mMu);
        mEntries.emplace(h, std::move(entry));
    }

    void Erase(Handle h) {
        std::lock_guard<std::mutex> lk(mMu);
        mEntries.erase(h);
    }

    template <class F>
    bool WithEntry(Handle h, F&& f) {
        std::lock_guard<std::mutex> lk(mMu);
        auto it = mEntries.find(h);
        if (it == mEntries.end()) return false;
        f(*it->second);
        return true;
    }

    template <class F>
    void ForEach(F&& f) {
        std::lock_guard<std::mutex> lk(mMu);
        for (auto& kv : mEntries) f(*kv.second);
    }

    bool Contains(Handle h) const {
        std::lock_guard<std::mutex> lk(mMu);
        return mEntries.find(h) != mEntries.end();
    }

    void Clear() {
        std::lock_guard<std::mutex> lk(mMu);
        mEntries.clear();
    }

private:
    mutable std::mutex                               mMu;
    std::unordered_map<Handle, std::unique_ptr<E>>   mEntries;
};

}  // namespace roulin::fetch
