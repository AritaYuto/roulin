<div align="center">

# Roulin

**Content-addressed asset delivery for game engines.** </br>
Drop-in replacement for Unity Addressables — S3-backed, engine-agnostic.

English | [日本語](README.ja.md)

[![CI](https://github.com/KirisameMarisa/roulin/actions/workflows/ci.yml/badge.svg)](https://github.com/KirisameMarisa/roulin/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](#license)
[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-000000.svg)]()

</div>

---

## Highlights

**Blob-level deduplication**</br>
Assets are identified by the hash of their content, so identical files are stored once in the cloud.  
Multiple revisions and bundles can share the same blob, so builds that share common assets share storage too.

**Catalogs stay small, even over years of development**</br>
The entire asset catalog is a single FlatBuffers file (flat schema).  
Even for production-scale titles it stays in the 1–4 MB range,  
so the catalog doesn't balloon as the project ages, and it's delivered reliably in a single HTTP request.

**Edited assets appear on device instantly**</br>
Save an asset in the Unity editor and Roulin streams it — with no rebuild —
to any device connected to your local PC. Works for Texture, Material, Mesh,
AudioClip, Prefab, and ScriptableObject.

**No cloud lock-in**</br>
Store anywhere S3-compatible — AWS S3, Cloudflare R2, MinIO, self-hosted.
Switching providers is a blob copy, nothing more.

**Warm rebuilds skip the expensive parts**</br>
After the first full build, unchanged bundles skip dependency analysis entirely.  
The same warm-cache logic runs in CI and on developer machines.

**Drops into existing Unity projects**</br>
Your `Addressables.LoadAssetAsync<T>(...)` code keeps working as-is.
Roulin slots in without touching any call sites.

---

## Getting started

### 1. Start roulin-server

```bash
docker compose up --build
```

roulin-server and a local MinIO instance both start automatically. For AWS S3 or other backends, run the binary directly:

```bash
roulin-server serve --storage s3://your-bucket/assets --port 8765
```

### 2. Install the Unity package

Add via **Package Manager → Add package from git URL**:

```
https://github.com/KirisameMarisa/roulin.git?path=plugins/unity
```

For the full Unity integration guide, see **[docs/unity.md](docs/unity.md)**.

---

## Roadmap

| Status | Item |
|---|---|
| ✅ | Unity Addressables replacement (LoadAssetAsync, DownloadDependenciesAsync, GetDownloadSizeAsync) |
| ✅ | Content-addressed S3 storage with BLAKE3 |
| ✅ | Warm rebuild via per-bundle metadata |
| ✅ | Live device debug — USB one-time pair → WiFi |
| ✅ | SSE hot-reload (stream asset changes to running device) |
| ✅ | roulin-fetch ↔ Unity HTTP backend (libcurl HTTP/2 replacing UnityWebRequest) |
| 📋 | FlatBuffers wire format for Unity blob metadata |
| 📋 | Unreal Engine and Godot plugins |
| 📋 | Encryption (ChaCha20, random-access capable) |

---

## Components

| Component | Language | Role |
|---|---|---|
| `roulin-core` | C++17 | Content-addressed VFS, FlatBuffers parsing, C FFI |
| `roulin-fetch` | C++17 + libcurl | HTTP/2 parallel downloader |
| `roulin-server` | Go 1.22+ | Stateless S3-bypass server (POST blobs/parcels, GET index/blobs) |
| `roulin-cli` | Go 1.22+ | `inspect-parcel`, `revisions`, `watch` (device pairing) |
| `plugins/unity` | C# (.NET Standard 2.1) | Unity UPM package |

---

## License

MIT — see [LICENSE](LICENSE).
