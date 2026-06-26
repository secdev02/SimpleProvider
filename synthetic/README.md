# SimpleProvider – Synthetic Data

The `--syntheticdata` flag extends the provider with a virtual file system layer defined by a CSV file. Synthetic entries are mixed into the real source directory projection, appearing alongside genuine files in the virt root without any real files backing them.

This is useful for showcasing ProjFS with a controlled, reproducible layout — for example, projecting a convincing set of credential files to demonstrate that a security scanner or backup tool picks them up, without exposing any real secrets.

---

## Quick start

```
SimpleProvider.exe --sourceroot C:\EmptyOrRealDir --virtroot C:\Virt --syntheticdata entries.csv
```

`--sourceroot` is still required. It can be an empty directory if you only want synthetic content.

---

## CSV format

```
# Comments start with #.  Blank lines are ignored.
# Format: \Path\To\Entry,isDirectory,fileSize,unixTimestamp

\AWS,true,0,1743942586
\AWS\credentials,false,116,1741508986
\SSH Keys,true,0,1751527786
\SSH Keys\id_rsa,false,1675,1738927786
```

| Field | Type | Notes |
|---|---|---|
| `\Path\To\Entry` | string | Path relative to the virt root. Must start with `\`. Backslash separators. |
| `isDirectory` | `true` / `false` | Whether the entry is a directory. |
| `fileSize` | integer (bytes) | Reported size of the file. Ignored for directories (use `0`). |
| `unixTimestamp` | integer (seconds) | Unix epoch timestamp applied to all four NTFS timestamps: Created, Accessed, Written, Changed. |

Paths **must be declared top-down**: a parent directory must appear in the CSV before its children, otherwise the parent directory will exist without a corresponding entry and `GetPlaceholderInfoCallback` will fall through to the real source (which may not exist).

---

## The included `entries.csv`

The provided example projects a realistic-looking credential tree:

```
C:\Virt\
├── AWS\
│   ├── credentials
│   ├── config
│   ├── iam_access_keys.csv
│   └── s3_bucket_policy.json
├── SSH Keys\
│   ├── id_rsa
│   ├── id_rsa.pub
│   ├── id_ed25519
│   ├── id_ed25519.pub
│   ├── authorized_keys
│   └── deploy_key.pem
└── API Keys\
    ├── api_keys.json
    ├── github_pat.txt
    └── stripe_keys.txt
```

All values in these files are well-known AWS documentation examples or obviously synthetic placeholders (`AKIAIOSFODNN7EXAMPLE`, `sk-EXAMPLE…`). No real credentials are involved.

---

## Content generation

When a process reads a synthetic file, `SyntheticContent.Generate` produces content matched to the filename. The content is then trimmed or padded with newlines to land on exactly `fileSize` bytes as declared in the CSV.

### Recognised filenames (exact, case-insensitive)

| Filename | Content produced |
|---|---|
| `credentials` | AWS INI credentials block (`[default]`, `aws_access_key_id`, `aws_secret_access_key`) |
| `config` | AWS INI config block (default + staging + prod profiles) |
| `iam_access_keys.csv` | CSV with header row + two IAM key rows |
| `s3_bucket_policy.json` | Three-statement S3 bucket policy JSON |
| `id_rsa` | `-----BEGIN RSA PRIVATE KEY-----` PEM block sized to `fileSize` |
| `id_rsa.pub` | `ssh-rsa AAAA…` public key line |
| `id_ed25519` | `-----BEGIN OPENSSH PRIVATE KEY-----` PEM block sized to `fileSize` |
| `id_ed25519.pub` | `ssh-ed25519 AAAA…` public key line |
| `authorized_keys` | Four `ssh-rsa` / `ssh-ed25519` public key entries |
| `deploy_key.pem` | `-----BEGIN RSA PRIVATE KEY-----` PEM block sized to `fileSize` |
| `api_keys.json` | JSON object with OpenAI, SendGrid, Twilio, PagerDuty key blocks |
| `github_pat.txt` | `ghp_EXAMPLE…` token + created date + scope list |
| `stripe_keys.txt` | Publishable, secret, restricted, and webhook keys in plain text |

### Extension fallbacks (when filename is not recognised)

| Extension | Content produced |
|---|---|
| `.pem` | Generic `-----BEGIN PRIVATE KEY-----` PEM block sized to `fileSize` |
| `.json` | Minimal `{ "synthetic": true }` JSON |
| `.csv` | Three-column header + one data row |
| anything else | `# <filename>` comment header |

### PEM size scaling

PEM files (`id_rsa`, `id_ed25519`, `deploy_key.pem`, `*.pem`) use a deterministic LCG to fill the base64 body to approximately `fileSize` bytes. The generator subtracts the header and footer lengths from `fileSize` to decide how much base64 to emit, so the resulting PEM always looks correctly proportioned for the declared key size regardless of what `fileSize` is set to in the CSV.

The LCG seed is fixed (`0x12345678`), so every provider run generates identical content for identical inputs. This makes the projection reproducible and diff-stable.

---

## Mixing: real vs synthetic priority

When a directory contains both real files from `--sourceroot` and synthetic entries from the CSV, the two are merged during `StartDirectoryEnumerationCallback`:

```
Real source          Synthetic CSV           Merged listing
─────────────────    ─────────────────       ─────────────────
notes.txt            credentials             API Keys\       (synth)
project\             id_rsa                  credentials     (synth)
                                             id_rsa          (synth)
                                             notes.txt       (real)
                                             project\        (real)
```

**Real files take priority.** If a real file and a synthetic entry share the same name in the same directory, the real file wins and the synthetic entry is silently dropped from the listing. The same rule applies in `GetPlaceholderInfoCallback` and `GetFileDataCallback`: the provider checks the real source first and only falls through to synthetic content if nothing is found on disk.

---

## Console output with synthetic entries

Synthetic files produce the same callback log lines as real files. The only visual difference is the `[synthetic]` tag on the exit lines:

```
[14:03:11 INF] ----> StartDirectoryEnumerationCallback Path []
[14:03:11 INF] <---- StartDirectoryEnumerationCallback Ok
[14:03:11 INF] ----> GetDirectoryEnumerationCallback filterFileName []
[14:03:11 INF] <---- GetDirectoryEnumerationCallback Ok [Added entries: 3]
[14:03:12 INF] ----> GetPlaceholderInfoCallback [AWS]
[14:03:12 INF]   Placeholder creation triggered by [\Device\...\explorer.exe 9812]
[14:03:12 INF] <---- GetPlaceholderInfoCallback Ok [synthetic]
[14:03:13 INF] ----> StartDirectoryEnumerationCallback Path [AWS]
[14:03:13 INF] <---- StartDirectoryEnumerationCallback Ok
[14:03:13 INF] ----> GetDirectoryEnumerationCallback filterFileName []
[14:03:13 INF] <---- GetDirectoryEnumerationCallback Ok [Added entries: 4]
[14:03:14 INF] ----> GetPlaceholderInfoCallback [AWS\credentials]
[14:03:14 INF]   Placeholder creation triggered by [\Device\...\powershell.exe 6444]
[14:03:14 INF] <---- GetPlaceholderInfoCallback Ok [synthetic]
[14:03:14 INF] ----> GetFileDataCallback relativePath [AWS\credentials]
[14:03:14 INF]   triggered by [\Device\...\powershell.exe 6444]
[14:03:14 INF] <---- return status Ok [synthetic]
```

---

## How the three new classes work

### `SyntheticEntry`

Plain data class. Holds one row from the CSV after normalisation: the leading `\` is stripped from the path, and `Name` / `ParentPath` are split out for fast lookup. `GetFiletime()` converts the Unix timestamp to a Windows FILETIME for use in `PrjWritePlaceholderInfo`.

### `SyntheticData`

Built once at startup from `SyntheticData.Load(path)`. Maintains two dictionaries:

- `_byPath` — keyed by `RelativePath`, used in `GetPlaceholderInfoCallback` and `GetFileDataCallback` to check whether a given path is synthetic.
- `_byParent` — keyed by `ParentPath`, used in `StartDirectoryEnumerationCallback` to find all synthetic children of a directory being listed.

Both dictionaries use case-insensitive comparison, matching ProjFS's own case-insensitive path handling.

### `SyntheticContent`

Stateless static class. `Generate(fileName, declaredSize)` selects a template by filename (exact match first, extension fallback second), encodes it as UTF-8, then calls `FitToSize` to trim or pad with `\n` characters to exactly `declaredSize` bytes. PEM files bypass the static templates and use `PemBlock()` + `FakeBase64()` to produce correctly proportioned base64 bodies at any declared size.

---

## `DirEntry` struct – the unified listing type

Internally, `EnumerationSession` used to store `FileSystemInfo[]` (real entries only). It now stores a `DirEntry[]` that holds one entry regardless of origin:

```
DirEntry
├── Name            string
├── IsSynthetic     bool
├── IsDirectory     bool
├── FileSize        long
├── CreationTimeFt  long   (Windows FILETIME)
├── LastAccessTimeFt long
├── LastWriteTimeFt  long
└── FileAttributes  uint
```

`OnStartDirEnum` builds this list by converting real `FileSystemInfo` objects via `RealToDirEntry` and synthetic entries via `SyntheticToDirEntry`, then sorting the merged list. `OnGetDirEnum` reads from the same struct regardless of origin, so `PrjFillDirEntryBuffer` sees a single consistent code path for both real and synthetic entries.

---

## Adding new content templates

Open `SyntheticContent.PickTemplate` and add a `case` for the new filename, pointing to a new `private const string Tpl…` field. The `FitToSize` call in `Generate` handles any length mismatch automatically, so the template string does not need to be exactly `fileSize` bytes long.

```csharp
case "vault_token":
    return TplVaultToken;
```

```csharp
private const string TplVaultToken =
    "# HashiCorp Vault token\n" +
    "VAULT_TOKEN=hvs.EXAMPLE1234567890abcdefghijklmnopqrstuvwxyz\n" +
    "VAULT_ADDR=https://vault.example.com:8200\n";
```

For binary or large structured files, replace the `const string` approach with a new branch in `PickTemplate` that calls a dedicated generator method instead.

---

## Timestamps

The `unixTimestamp` field is converted to a Windows FILETIME and applied to all four NTFS timestamps (Created, LastAccess, LastWrite, Change) identically. If you need files with different access and write times, add two entries with different paths and timestamps — the values are metadata only and do not affect what content is generated.

To convert a human-readable date to a Unix timestamp:

```powershell
[DateTimeOffset]::Parse("2024-01-15").ToUnixTimeSeconds()
# 1705276800
```

---

## File layout

```
SimpleProviderShowcase\
├── SimpleProvider.cs    Single source file; compile with csc.exe
├── entries.csv          Example synthetic layout (AWS, SSH, API key tree)
├── README.md            Core provider documentation
└── README-synthetic.md  This file
```
