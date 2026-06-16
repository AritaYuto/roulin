#include "winhttp_attempt.h"

#include "winhttp_error_classifier.h"

#include <cstdint>
#include <vector>

namespace roulin::fetch::desktop {

namespace {

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int needed = MultiByteToWideChar(CP_UTF8, 0, s.data(),
                                            static_cast<int>(s.size()),
                                            nullptr, 0);
    std::wstring out(static_cast<size_t>(needed), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()),
                        out.data(), needed);
    return out;
}

std::string queryHeader(HINTERNET request, DWORD info_level) {
    DWORD len = 0;
    WinHttpQueryHeaders(request, info_level, WINHTTP_HEADER_NAME_BY_INDEX,
                        WINHTTP_NO_OUTPUT_BUFFER, &len,
                        WINHTTP_NO_HEADER_INDEX);
    if (len == 0) return {};
    std::wstring buf(len / sizeof(wchar_t), L'\0');
    if (!WinHttpQueryHeaders(request, info_level, WINHTTP_HEADER_NAME_BY_INDEX,
                              buf.data(), &len, WINHTTP_NO_HEADER_INDEX)) {
        return {};
    }
    while (!buf.empty() && buf.back() == L'\0') buf.pop_back();
    if (buf.empty()) return {};
    const int needed = WideCharToMultiByte(CP_UTF8, 0, buf.data(),
                                            static_cast<int>(buf.size()),
                                            nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, 0, buf.data(), static_cast<int>(buf.size()),
                        out.data(), needed, nullptr, nullptr);
    return out;
}

int decodeHttpVersion(HINTERNET request) {
    const std::string ver = queryHeader(request, WINHTTP_QUERY_VERSION);
    if (ver == "HTTP/2.0" || ver == "HTTP/2") return 3;
    if (ver == "HTTP/1.1")                    return 2;
    if (ver == "HTTP/1.0")                    return 1;
    return 0;
}

uint64_t decodeContentLength(HINTERNET request) {
    DWORD value = 0;
    DWORD size  = sizeof(value);
    if (WinHttpQueryHeaders(request,
                            WINHTTP_QUERY_CONTENT_LENGTH | WINHTTP_QUERY_FLAG_NUMBER,
                            WINHTTP_HEADER_NAME_BY_INDEX, &value, &size,
                            WINHTTP_NO_HEADER_INDEX)) {
        return value;
    }
    return 0;
}

struct ScopedHandle {
    HINTERNET h = nullptr;
    ScopedHandle() = default;
    explicit ScopedHandle(HINTERNET handle) : h(handle) {}
    ~ScopedHandle() { if (h) WinHttpCloseHandle(h); }
    ScopedHandle(const ScopedHandle&)            = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;
};

constexpr size_t kReadChunkSize = 64 * 1024;

WinHttpResult winHttpFailure(const char* context) {
    WinHttpResult r;
    const auto e = WinHttpErrorClassifier(GetLastError(), context).Classify();
    r.category = e.category;
    r.message  = e.message;
    return r;
}

}  // namespace

WinHttpResult runWinHttp(HINTERNET    session,
                          HttpMode     http_mode,
                          WinHttpEntry& entry) {
    WinHttpResult result;

    const std::wstring wurl = widen(entry.request.url);
    URL_COMPONENTS uc{};
    uc.dwStructSize = sizeof(uc);
    std::wstring host_buf(256, L'\0');
    std::wstring path_buf(2048, L'\0');
    uc.lpszHostName     = host_buf.data();
    uc.dwHostNameLength = static_cast<DWORD>(host_buf.size());
    uc.lpszUrlPath      = path_buf.data();
    uc.dwUrlPathLength  = static_cast<DWORD>(path_buf.size());
    if (!WinHttpCrackUrl(wurl.c_str(), static_cast<DWORD>(wurl.size()), 0, &uc)) {
        return winHttpFailure("WinHttpCrackUrl");
    }
    const bool is_https = (uc.nScheme == INTERNET_SCHEME_HTTPS);
    std::wstring host(uc.lpszHostName, uc.dwHostNameLength);

    ScopedHandle connection(WinHttpConnect(session, host.c_str(),
                                            uc.nPort, 0));
    if (!connection.h) {
        return winHttpFailure("WinHttpConnect");
    }

    DWORD open_flags = is_https ? WINHTTP_FLAG_SECURE : 0;
    ScopedHandle request(WinHttpOpenRequest(connection.h, L"GET",
                                              uc.lpszUrlPath,
                                              nullptr,
                                              WINHTTP_NO_REFERER,
                                              WINHTTP_DEFAULT_ACCEPT_TYPES,
                                              open_flags));
    if (!request.h) {
        return winHttpFailure("WinHttpOpenRequest");
    }

    if (http_mode == HttpMode::Auto) {
        DWORD proto = WINHTTP_PROTOCOL_FLAG_HTTP2;
        WinHttpSetOption(request.h, WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL,
                          &proto, sizeof(proto));
    }

    if (!WinHttpSendRequest(request.h, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                             WINHTTP_NO_REQUEST_DATA, 0, 0, 0)) {
        return winHttpFailure("WinHttpSendRequest");
    }
    if (!WinHttpReceiveResponse(request.h, nullptr)) {
        return winHttpFailure("WinHttpReceiveResponse");
    }

    // Matches libcurl CURLOPT_FAILONERROR: non-2xx → transport failure.
    DWORD status = 0;
    DWORD status_size = sizeof(status);
    if (WinHttpQueryHeaders(request.h,
                             WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                             WINHTTP_HEADER_NAME_BY_INDEX,
                             &status, &status_size,
                             WINHTTP_NO_HEADER_INDEX)) {
        if (status < 200 || status >= 300) {
            result.category = ErrorCategory::Network;
            result.message  = "HTTP status " + std::to_string(status);
            return result;
        }
    }

    result.http_version = decodeHttpVersion(request.h);
    const uint64_t content_length = decodeContentLength(request.h);
    if (content_length > 0) {
        entry.session->SetBytesTotal(entry.handle, content_length);
    }

    std::vector<uint8_t> chunk(kReadChunkSize);
    for (;;) {
        if (entry.session->IsCancelRequested(entry.handle)) {
            result.cancelled = true;
            return result;
        }
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request.h, &available)) {
            return winHttpFailure("WinHttpQueryDataAvailable");
        }
        if (available == 0) break;

        while (available > 0) {
            if (entry.session->IsCancelRequested(entry.handle)) {
                result.cancelled = true;
                return result;
            }
            const DWORD to_read = available < chunk.size()
                                       ? available
                                       : static_cast<DWORD>(chunk.size());
            DWORD read = 0;
            if (!WinHttpReadData(request.h, chunk.data(), to_read, &read)) {
                return winHttpFailure("WinHttpReadData");
            }
            if (read == 0) break;
            entry.session->WriteChunk(entry.handle, chunk.data(), read);
            available -= read;
        }
    }

    result.success = true;
    return result;
}

}  // namespace roulin::fetch::desktop
