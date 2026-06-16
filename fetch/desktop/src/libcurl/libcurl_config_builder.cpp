#include "libcurl_config_builder.h"

namespace roulin::fetch::desktop {

namespace {

// http:// → h2c prior-knowledge (no ALPN without TLS). https:// → ALPN.
long onPickHttpVersion(HttpMode mode, const std::string& url) {
    if (mode == HttpMode::Http1Only) return CURL_HTTP_VERSION_1_1;
    const bool is_https = url.compare(0, 8, "https://") == 0;
    return is_https ? CURL_HTTP_VERSION_2_0
                    : CURL_HTTP_VERSION_2_PRIOR_KNOWLEDGE;
}


size_t OnBytes(void* ptr, size_t sz, size_t n, void* ud) {
    auto* entry        = static_cast<LibcurlEntry*>(ud);
    const size_t total = sz * n;
    entry->session->WriteChunk(entry->handle,
                                static_cast<const uint8_t*>(ptr),
                                total);
    return total;
}

int OnProgress(void* ud,
    curl_off_t dltotal, curl_off_t /*dlnow*/,
    curl_off_t /*ultotal*/, curl_off_t /*ulnow*/) {
    auto* entry = static_cast<LibcurlEntry*>(ud);
    if (dltotal > 0) {
        entry->session->SetBytesTotal(entry->handle,
                                       static_cast<uint64_t>(dltotal));
    }
    if (entry->session->IsCancelRequested(entry->handle)) return 1;
    return 0;
}

}  // namespace

LibcurlConfigBuilder::LibcurlConfigBuilder(HttpMode http_mode)
    : mHttpMode(http_mode) {}

bool LibcurlConfigBuilder::Build(LibcurlEntry& entry) const {
    entry.easy = curl_easy_init();
    if (!entry.easy) return false;
    apply(entry);
    return true;
}

bool LibcurlConfigBuilder::Reconfigure(LibcurlEntry& entry) const {
    if (!entry.easy) return false;
    curl_easy_reset(entry.easy);
    apply(entry);
    return true;
}

void LibcurlConfigBuilder::apply(LibcurlEntry& entry) const {
    CURL* h = entry.easy;
    curl_easy_setopt(h, CURLOPT_URL,              entry.request.url.c_str());
    curl_easy_setopt(h, CURLOPT_WRITEFUNCTION,    OnBytes);
    curl_easy_setopt(h, CURLOPT_WRITEDATA,        &entry);
    curl_easy_setopt(h, CURLOPT_PRIVATE,          &entry);
    curl_easy_setopt(h, CURLOPT_FOLLOWLOCATION,   1L);
    curl_easy_setopt(h, CURLOPT_FAILONERROR,      1L);
    curl_easy_setopt(h, CURLOPT_NOPROGRESS,       0L);
    curl_easy_setopt(h, CURLOPT_XFERINFOFUNCTION, OnProgress);
    curl_easy_setopt(h, CURLOPT_XFERINFODATA,     &entry);
    curl_easy_setopt(h, CURLOPT_HTTP_VERSION, onPickHttpVersion(mHttpMode, entry.request.url));
}



}  // namespace roulin::fetch::desktop
