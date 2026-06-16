package io.roulin.fetch

import okhttp3.Call
import okhttp3.Callback
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.Response
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap

class AndroidFetcher {

    // JNI-resolved field; the C++ destructor writes 0 from another thread.
    @Volatile
    private var nativeFetcherPtr: Long = 0

    private val client: OkHttpClient = OkHttpClient.Builder()
        .protocols(listOf(Protocol.HTTP_2, Protocol.HTTP_1_1))
        .build()

    private val http1Client: OkHttpClient = client.newBuilder()
        .protocols(listOf(Protocol.HTTP_1_1))
        .build()

    private val activeCalls = ConcurrentHashMap<Long, Call>()

    fun setNativeFetcher(ptr: Long) {
        nativeFetcherPtr = ptr
    }

    fun start(
        handle: Long,
        url: String,
        expectedHash: ByteArray,
        destPath: String,
        httpMode: Int,
    ) {
        val req = Request.Builder().url(url).build()
        val httpClient = if (httpMode == HTTP_MODE_HTTP1_ONLY) http1Client else client
        val call = httpClient.newCall(req)
        activeCalls[handle] = call

        call.enqueue(object : Callback {
            override fun onFailure(c: Call, e: IOException) {
                activeCalls.remove(handle)
                if (c.isCanceled()) {
                    nativeFailed(handle, ERROR_CANCELLED, "cancelled")
                } else {
                    nativeFailed(handle, ERROR_NETWORK, e.message ?: "network error")
                }
            }

            override fun onResponse(c: Call, response: Response) {
                response.use { resp ->
                    if (!resp.isSuccessful) {
                        activeCalls.remove(handle)
                        nativeFailed(handle, ERROR_NETWORK, "http ${resp.code}")
                        return
                    }
                    val body = resp.body
                    if (body == null) {
                        activeCalls.remove(handle)
                        nativeFailed(handle, ERROR_IO, "empty body")
                        return
                    }
                    val contentLength = body.contentLength()
                    if (contentLength > 0) {
                        nativeSetBytesTotal(handle, contentLength)
                    }
                    val src = body.source()
                    val buf = ByteArray(CHUNK_SIZE)
                    try {
                        while (true) {
                            if (!nativeShouldContinue(handle)) {
                                c.cancel()
                                activeCalls.remove(handle)
                                nativeFailed(handle, ERROR_CANCELLED, "cancelled")
                                return
                            }
                            val n = src.read(buf)
                            if (n == -1) break
                            nativeChunk(handle, buf, n)
                        }
                    } catch (e: IOException) {
                        activeCalls.remove(handle)
                        nativeFailed(handle, ERROR_IO, e.message ?: "io error")
                        return
                    }
                    val httpVersion = when (resp.protocol) {
                        Protocol.HTTP_2     -> HTTP_VERSION_H2
                        Protocol.HTTP_1_1   -> HTTP_VERSION_1_1
                        Protocol.HTTP_1_0   -> HTTP_VERSION_1_0
                        else                -> HTTP_VERSION_UNKNOWN
                    }
                    nativeComplete(handle, httpVersion)
                }
                activeCalls.remove(handle)
            }
        })
    }

    fun cancel(handle: Long) {
        activeCalls.remove(handle)?.cancel()
    }

    private external fun nativeChunk(handle: Long, data: ByteArray, len: Int)
    private external fun nativeComplete(handle: Long, httpVersion: Int)
    private external fun nativeFailed(handle: Long, category: Int, message: String)
    private external fun nativeShouldContinue(handle: Long): Boolean
    private external fun nativeSetBytesTotal(handle: Long, total: Long)

    companion object {
        // Must stay in sync with roulin::fetch::ErrorCategory.
        const val ERROR_NETWORK       = 0
        const val ERROR_HASH_MISMATCH = 1
        const val ERROR_CANCELLED     = 2
        const val ERROR_TIMEOUT       = 3
        const val ERROR_IO            = 4
        const val ERROR_UNKNOWN       = 5

        private const val HTTP_VERSION_UNKNOWN = 0
        private const val HTTP_VERSION_1_0     = 1
        private const val HTTP_VERSION_1_1     = 2
        private const val HTTP_VERSION_H2      = 3

        private const val HTTP_MODE_AUTO        = 0
        private const val HTTP_MODE_HTTP1_ONLY  = 1

        private const val CHUNK_SIZE = 8192

        init {
            System.loadLibrary("roulin_core")
        }
    }
}
