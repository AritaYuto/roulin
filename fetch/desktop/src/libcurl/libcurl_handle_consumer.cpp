#include "libcurl_handle_consumer.h"

#include "libcurl_error_classifier.h"

namespace roulin::fetch::desktop {

LibcurlHandleConsumer::LibcurlHandleConsumer(CURLM*                       multi,
                                              EntryRegistry<LibcurlEntry>* registry,
                                              LibcurlConfigBuilder*        builder)
    : HandleConsumerBase<LibcurlEntry>(registry),
      mMulti(multi),
      mBuilder(builder) {}

void LibcurlHandleConsumer::releaseResources(LibcurlEntry& entry) {
    if (entry.attached) {
        curl_multi_remove_handle(mMulti, entry.easy);
        entry.attached = false;
    }
    if (entry.easy) {
        curl_easy_cleanup(entry.easy);
        entry.easy = nullptr;
    }
}

bool LibcurlHandleConsumer::reopen(LibcurlEntry& entry) {
    if (!mBuilder->Reconfigure(entry)) return false;
    if (curl_multi_add_handle(mMulti, entry.easy) != CURLM_OK) return false;
    entry.attached = true;
    return true;
}

void LibcurlHandleConsumer::OnDone(const CURLMsg& msg) {
    LibcurlEntry* entry = nullptr;
    curl_easy_getinfo(msg.easy_handle, CURLINFO_PRIVATE, &entry);
    if (!entry) return;

    curl_multi_remove_handle(mMulti, entry->easy);
    entry->attached = false;

    long http_ver = 0;
    curl_easy_getinfo(entry->easy, CURLINFO_HTTP_VERSION, &http_ver);

    const bool cancelled = entry->session->IsCancelRequested(entry->handle)
                         || msg.data.result == CURLE_ABORTED_BY_CALLBACK;

    if (cancelled) {
        handleCancellation(*entry);
        return;
    }
    if (msg.data.result == CURLE_OK) {
        handleTransportSuccess(*entry, static_cast<int>(http_ver));
    } else {
        const auto e = LibcurlErrorClassifier(msg.data.result).Classify();
        handleTransportFailure(*entry, e.category, e.message);
    }
}

}  // namespace roulin::fetch::desktop
