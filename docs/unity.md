# Unity Integration Guide

**Prerequisites**: roulin-server running and accessible. See [Local Server Setup](#3-local-server-setup) below for local development.

---

## 1. Requirements

| Software | Purpose |
|---|---|
| Unity 2022.3+ | Minimum supported version |
| [go-task](https://taskfile.dev) | Task runner for build and device commands |
| Docker | Local server stack (MinIO + roulin-server) |
| `adb` | Android device pairing |
| `iproxy`, `idevice_id` | iOS device pairing |

---

## 2. Package Installation

Add via **Package Manager → Add package from git URL**:

```
https://github.com/KirisameMarisa/roulin.git?path=plugins/unity
```

After installing, add `USE_ROULIN` to **Project Settings → Player → Scripting Define Symbols**.

| State | Behavior |
|---|---|
| Not set | Runtime assembly excluded from builds. Console shows a warning. |
| Set | All assemblies compile. Package fully active. |

Use the define for optional integrations:

```csharp
#if USE_ROULIN
await Roulin.Initialize(baseUrl);
#else
await Addressables.InitializeAsync();
#endif
```

---

## 3. Local Server Setup

Start the local stack:

```bash
task dev:up          # foreground
task dev:up:detach   # background
task dev:down        # stop and remove
```

Both services start automatically:
- **MinIO** at `http://localhost:9000` (S3-compatible object store)
  - Console: `http://localhost:9001` — user: `minioadmin` / password: `minioadmin`
  - Bucket `roulin-dev` is created automatically
- **roulin-server** at `http://localhost:8765` backed by MinIO

Set **Project Settings → Roulin → Server URL** to `http://localhost:8765`.

---

## 4. Build Pipeline

### Configure

Open **Project Settings → Roulin**:

| Setting | Description |
|---|---|
| **Server URL** | roulin-server base URL (e.g. `http://localhost:8765`) |
| **Manual Revision** | Leave empty to use `git rev-parse HEAD` automatically |
| **Bundle Output Dir** | Intermediate `.bundle` output directory (default: `Library/roulin/build`) |
| **Enable blob_meta capture** | Captures dependency data per bundle; warm rebuilds skip unchanged bundles |
| **Verbose logging** | Per-bundle detail logs. Off = aggregate summary only |

### Build

In the **Addressables Groups** window:

```
Build > New Build > Roulin Tree Build
```

1. The Scriptable Build Pipeline builds all groups into `.bundle` files
2. Each bundle is uploaded via `POST /blobs` (skipped if already present by hash)
3. The index is published via `POST /parcels/{revision}`
4. Build report written to `Library/roulin/build/roulin-build-report.json`

### Inspect

The **Editor Debug Window** (`Roulin > Debug Window`) shows bundle contents, sizes, and dependencies from the build report.

```bash
# Human-readable summary
roulin inspect-parcel --base-url http://localhost:8765 --revision <sha>

# JSON output
roulin inspect-parcel --base-url http://localhost:8765 --revision <sha> --json

# List all deployed revisions
roulin revisions --base-url http://localhost:8765
```

---

## 5. Runtime Integration

```csharp
// Sync, no I/O. Call once before any asset load.
Roulin.Initialize("https://your-cdn.example.com");

// Download the index for a revision and register the locator.
await Roulin.SwitchRevisionAsync(revisionId, ct);
```

```csharp
// Via Addressables — existing code works unchanged.
var texture = await Addressables.LoadAssetAsync<Texture2D>("ui/icons/player");

// Direct alias.
var texture = await Roulin.LoadAsync<Texture2D>("ui/icons/player");
Roulin.Release(texture);

// Download size and pre-downloading.
long bytes = await Addressables.GetDownloadSizeAsync("ui/icons/player");
await Addressables.DownloadDependenciesAsync("ui/icons/player");

// Cache management.
await Roulin.Cache.ClearAsync("ui/icons/player");
await Roulin.Cache.ClearAllAsync();

// Teardown.
Roulin.Shutdown();
```

---

## 6. Device Debugging

Roulin streams asset changes to a running device. On first launch the device app listens for the PC's IP over USB; subsequent launches reconnect via WiFi.

### Pairing

```bash
task device:pair              # one-shot: detect device, pair once, exit
task device:watch             # stay resident, re-pair on USB reconnect
task device:watch:vpn         # Android only: use adb reverse (USB tunnel) for VPN environments
```

Optional arguments:

| Argument | Default | Description |
|---|---|---|
| `rev=<sha>` | — | Revision hint sent to the device at pair time |
| `host=<ip>` | auto-detected | Override the PC's IP sent to the device |

```bash
task device:watch rev=abc123
task device:watch host=192.168.1.10 rev=abc123
```

### Re-pairing

Re-pair when the PC's WiFi IP changes:

```csharp
RoulinPairing.Clear();  // clears stored IP; next launch will listen again
```
