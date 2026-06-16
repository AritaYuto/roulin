#include "fetch.h"
#include "error_internal.h"
#include "roulin/fetch/session.h"

#if defined(__APPLE__)
#  include "roulin/fetch/apple_fetcher.h"
   using Backend       = roulin::fetch::apple::AppleFetcher;
   using BackendConfig = roulin::fetch::apple::Config;
   using BackendMode   = roulin::fetch::apple::HttpMode;
#elif defined(__ANDROID__)
#  include "roulin/fetch/android_fetcher.h"
   using Backend       = roulin::fetch::android::AndroidFetcher;
   using BackendConfig = roulin::fetch::android::Config;
   using BackendMode   = roulin::fetch::android::HttpMode;
#else
#  include "roulin/fetch/desktop_fetcher.h"
   using Backend       = roulin::fetch::desktop::DesktopFetcher;
   using BackendConfig = roulin::fetch::desktop::Config;
   using BackendMode   = roulin::fetch::desktop::HttpMode;
#endif

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <new>
#include <string>
#include <system_error>
#include <unordered_map>

#if defined(_WIN32)
  #include <process.h>
  #define ROULIN_GETPID _getpid
#else
  #include <unistd.h>
  #define ROULIN_GETPID getpid
#endif

using roulin::error::clearError;
using roulin::error::setError;
// MacTypes.h (Foundation, Obj-C++) defines `Handle` globally — alias to avoid collision.
using RoulinHandle = roulin::fetch::Handle;
using roulin::fetch::PollSnapshot;
using roulin::fetch::Request;
using roulin::fetch::Session;
using roulin::fetch::State;

namespace {

std::atomic<uint64_t>& sessionCounter() {
    static std::atomic<uint64_t> counter{0};
    return counter;
}

std::string makeWorkDir() {
    namespace fs = std::filesystem;
    const auto base = fs::temp_directory_path();
    const auto id   = sessionCounter().fetch_add(1, std::memory_order_relaxed);
    char suffix[64];
    std::snprintf(suffix, sizeof(suffix),
                  "roulin-fetch-%d-%llu",
                  static_cast<int>(ROULIN_GETPID()),
                  static_cast<unsigned long long>(id));
    return (base / suffix).string();
}

}  // namespace

struct rln_fetch_session {
    std::unique_ptr<Session>        session;
    std::unique_ptr<Backend>        backend;
    std::string                     work_dir;
    int                             default_max_attempts = 1;
    std::atomic<uint64_t>           next_temp_id{1};

    // C ABI requires single-threaded per-session use, so no mutex needed.
    std::unordered_map<uint64_t, std::string> dest_paths;
};

namespace {

std::string allocDestPath(rln_fetch_session* s) {
    const auto id = s->next_temp_id.fetch_add(1, std::memory_order_relaxed);
    char name[64];
    std::snprintf(name, sizeof(name), "blob-%llu.bin",
                  static_cast<unsigned long long>(id));
    return (std::filesystem::path(s->work_dir) / name).string();
}

bool slurpFile(const std::string& path, uint8_t** out_buf, size_t* out_len) {
    std::error_code ec;
    const auto size = std::filesystem::file_size(path, ec);
    if (ec) return false;

    std::ifstream f(path, std::ios::in | std::ios::binary);
    if (!f) return false;

    uint8_t* buf = nullptr;
    if (size > 0) {
        buf = static_cast<uint8_t*>(std::malloc(static_cast<size_t>(size)));
        if (!buf) return false;
        f.read(reinterpret_cast<char*>(buf),
               static_cast<std::streamsize>(size));
        if (!f) {
            std::free(buf);
            return false;
        }
    }
    if (out_buf) *out_buf = buf;
    else if (buf) std::free(buf);
    if (out_len) *out_len = static_cast<size_t>(size);
    return true;
}

}  // namespace

extern "C" {

rln_fetch_session* rln_fetch_session_new(const rln_fetch_config* cfg) {
    clearError();
    try {
        auto holder = std::make_unique<rln_fetch_session>();
        BackendConfig backend_cfg;
        holder->default_max_attempts = 3;
        if (cfg) {
            if (cfg->max_parallel > 0) backend_cfg.max_parallel = cfg->max_parallel;
            backend_cfg.http_mode = (cfg->http_mode == RLN_HTTP_1_ONLY)
                                       ? BackendMode::Http1Only
                                       : BackendMode::Auto;
            holder->default_max_attempts = cfg->max_attempts <= 0 ? 1
                                                                  : cfg->max_attempts;
        }

        holder->work_dir = makeWorkDir();
        std::error_code ec;
        std::filesystem::create_directories(holder->work_dir, ec);
        if (ec) {
            setError("rln_fetch_session_new",
                     ("mkdir " + holder->work_dir + ": " + ec.message()).c_str());
            return nullptr;
        }

        holder->session = std::make_unique<Session>();
        holder->backend = std::make_unique<Backend>(*holder->session,
                                                    backend_cfg);
        return holder.release();
    } catch (const std::exception& e) {
        setError("rln_fetch_session_new", e);
        return nullptr;
    } catch (...) {
        setError("rln_fetch_session_new", "unknown error");
        return nullptr;
    }
}

void rln_fetch_session_free(rln_fetch_session* session) {
    if (!session) return;
    // Backend before Session: I/O thread must join before Session goes away.
    session->backend.reset();
    session->session.reset();

    std::error_code ec;
    std::filesystem::remove_all(session->work_dir, ec);
    delete session;
}

uint64_t rln_fetch_enqueue(rln_fetch_session* session,
                          const char*       url,
                          const uint8_t*    expected_hash) {
    clearError();
    if (!session || !url) {
        setError("rln_fetch_enqueue", "null argument");
        return 0;
    }
    try {
        Request req;
        req.url           = url;
        req.dest_path     = allocDestPath(session);
        req.max_attempts  = session->default_max_attempts;
        if (expected_hash) {
            std::memcpy(req.expected_hash.data(), expected_hash, 32);
        }
        const RoulinHandle h = session->backend->Enqueue(req);
        if (h != 0) {
            session->dest_paths.emplace(h, req.dest_path);
        }
        return h;
    } catch (const std::exception& e) {
        setError("rln_fetch_enqueue", e);
        return 0;
    } catch (...) {
        setError("rln_fetch_enqueue", "unknown error");
        return 0;
    }
}

int rln_fetch_poll(rln_fetch_session* session,
                  uint64_t          handle,
                  uint64_t*         out_bytes_done,
                  uint64_t*         out_bytes_total,
                  uint8_t**         out_buf,
                  size_t*           out_len,
                  int*              out_http_version) {
    clearError();
    if (!session) {
        setError("rln_fetch_poll", "null session");
        return -1;
    }
    try {
        PollSnapshot snap = session->session->Poll(handle);
        if (out_bytes_done)  *out_bytes_done  = snap.bytes_done;
        if (out_bytes_total) *out_bytes_total = snap.bytes_total;

        switch (snap.state) {
            case State::InProgress:
                return 0;
            case State::Completed: {
                std::string path;
                auto it = session->dest_paths.find(handle);
                if (it != session->dest_paths.end()) {
                    path = std::move(it->second);
                    session->dest_paths.erase(it);
                }
                uint8_t* buf = nullptr;
                size_t   len = 0;
                if (!path.empty()) {
                    if (!slurpFile(path, &buf, &len)) {
                        setError("rln_fetch_poll",
                                 ("failed to read " + path).c_str());
                        std::error_code ec;
                        std::filesystem::remove(path, ec);
                        return -1;
                    }
                    std::error_code ec;
                    std::filesystem::remove(path, ec);
                }
                if (out_buf) *out_buf = buf;
                else if (buf) std::free(buf);
                if (out_len) *out_len = len;
                if (out_http_version) *out_http_version = snap.http_version;
                return 1;
            }
            case State::Failed:
            default: {
                auto it = session->dest_paths.find(handle);
                if (it != session->dest_paths.end()) {
                    session->dest_paths.erase(it);
                }
                if (out_http_version) *out_http_version = snap.http_version;
                setError("rln_fetch_poll",
                         snap.error_message.empty() ? "failed"
                                                    : snap.error_message.c_str());
                return -1;
            }
        }
    } catch (const std::exception& e) {
        setError("rln_fetch_poll", e);
        return -1;
    } catch (...) {
        setError("rln_fetch_poll", "unknown error");
        return -1;
    }
}

void rln_fetch_cancel(rln_fetch_session* session, uint64_t handle) {
    if (!session) return;
    session->backend->Cancel(handle);
}

void rln_fetch_free_buf(uint8_t* buf) {
    std::free(buf);
}

}  // extern "C"
