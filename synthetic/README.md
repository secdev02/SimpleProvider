# SimpleProvider – Synthetic Data

The `--syntheticonly` flag and the `<syntheticFileList>` config section let the provider project a virtual file system that has no real files behind it — or mix synthetic entries alongside real ones. Everything lives in the single config file next to the exe. No external CSV file is needed.

---

## Deployment: two files only

```
SimpleProvider_Synthetic.exe
SimpleProvider_Synthetic.exe.config   ← virtual layout + content templates
```

The config file must be named `<exe-name>.exe.config` and sit in the same directory as the executable.

---

## Compile

```
csc.exe /platform:x64 /r:System.Xml.dll /out:SimpleProvider_Synthetic.exe SimpleProvider.cs
```

`System.Xml.dll` is required; it is a standard .NET Framework assembly and does not require NuGet.

---

## Run

```
# Real files from sourceroot, synthetic entries from config, mixed together:
SimpleProvider_Synthetic.exe --sourceroot C:\RealFiles --virtroot C:\FakeFiles

# Synthetic entries only – no real source directory needed:
SimpleProvider_Synthetic.exe --virtroot C:\FakeFiles --syntheticonly
```

| Flag | Required | Description |
|---|---|---|
| `--virtroot <path>` | Always | Directory where the projection appears; created and cleared on every start |
| `--sourceroot <path>` | Unless `--syntheticonly` | Directory whose real files are projected |
| `--syntheticonly` | Optional | Skip all real-source I/O; serve only entries from `<syntheticFileList>` |

---

## The config file

The config file has two sections.  Both are optional — omit either if you don't need it.

```xml
<configuration>

  <syntheticFileList><![CDATA[
    ...one entry per line...
  ]]></syntheticFileList>

  <syntheticTemplates>
    ...one <template> element per file type...
  </syntheticTemplates>

</configuration>
```

### Section 1 – `<syntheticFileList>`

Defines which files and directories appear in the virt root.  The content is CDATA using the same line format as the old external CSV.

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
| `\Path\To\Entry` | string | Path relative to the virt root, must start with `\`, backslash separators |
| `isDirectory` | `true` / `false` | Whether the entry is a directory; use `0` for `fileSize` on directories |
| `fileSize` | integer (bytes) | Reported size written into the NTFS placeholder |
| `unixTimestamp` | integer | Seconds since Unix epoch, applied to all four NTFS timestamps (Created, Accessed, Written, Changed) |

**Rule:** parent directories must appear before their children in the list.

### Section 2 – `<syntheticTemplates>`

Controls what bytes are returned when a synthetic file is opened and read.  Each `<template>` element covers one filename or file extension.

```xml
<!-- Exact filename match -->
<template name="credentials"><![CDATA[
[default]
aws_access_key_id = AKIAIOSFODNN7EXAMPLE
aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
]]></template>

<!-- PEM block sized to the fileSize declared in <syntheticFileList> -->
<template name="id_rsa" type="pem" pemLabel="RSA PRIVATE KEY" />

<!-- Extension fallback for any .json file without an exact-name match -->
<template extension=".json"><![CDATA[
{ "synthetic": true }
]]></template>
```

| Attribute | Value | Description |
|---|---|---|
| `name` | filename (case-insensitive) | Exact match, e.g. `credentials`, `id_rsa.pub` |
| `extension` | `.ext` | Fallback when no exact name matches, e.g. `.pem`, `.json` |
| `type="pem"` | — | Generate a deterministic LCG base64 PEM block scaled to `fileSize` |
| `pemLabel` | string | Text inside `-----BEGIN ... -----` (only used with `type="pem"`) |
| CDATA body | text | Static template; leading/trailing whitespace trimmed, then padded or truncated to `fileSize` |

**Lookup order:**
1. Exact filename match (`name=`)
2. Extension fallback (`extension=`)
3. Built-in generic comment (`# filename / synthetic file`)

**No recompile is needed** to change either section — edit the config and restart the provider.

---

## Startup output

A correct startup with both sections present looks like:

```
Config : C:\Tools\SimpleProvider_Synthetic.exe.config
Loaded 15 content templates from config.
Loaded 16 synthetic entries from config.
Clearing previous virtroot: C:\FakeFiles
Provider running.
  Virt   : C:\FakeFiles
  Source : C:\RealFiles
Press ENTER to stop...
```

For `--syntheticonly`:

```
Config : C:\Tools\SimpleProvider_Synthetic.exe.config
Loaded 15 content templates from config.
Loaded 16 synthetic entries from config.
Clearing previous virtroot: C:\FakeFiles
Provider running.
  Virt   : C:\FakeFiles
  Mode   : synthetic only (no real source)
Press ENTER to stop...
```

---

## Callback log

```
[14:03:11 INF] ----> StartDirectoryEnumerationCallback Path []
[14:03:11 INF] <---- StartDirectoryEnumerationCallback Ok
[14:03:11 INF] ----> GetDirectoryEnumerationCallback filterFileName []
[14:03:11 INF] <---- GetDirectoryEnumerationCallback Ok [Added entries: 3]
[14:03:12 INF] ----> GetPlaceholderInfoCallback [AWS]
[14:03:12 INF]   Placeholder creation triggered by [\Device\...\explorer.exe 9812]
[14:03:12 INF] <---- GetPlaceholderInfoCallback Ok [synthetic]
[14:03:14 INF] ----> GetFileDataCallback relativePath [AWS\credentials]
[14:03:14 INF]   triggered by [\Device\...\powershell.exe 6444]
[14:03:14 INF] <---- return status Ok [synthetic]
```

The `[synthetic]` tag on exit lines identifies which callbacks were served from synthetic data rather than the real source.

---

## Mixing: real vs synthetic priority

When `--syntheticonly` is **not** set, real and synthetic entries are merged at listing time:

| Situation | Rule |
|---|---|
| Same name in both real source and `<syntheticFileList>` | Real file wins; synthetic entry is silently dropped |
| Name only in `<syntheticFileList>` | Synthetic entry is added to the listing |
| Placeholder lookup (`GetPlaceholderInfoCallback`) | Real source checked first; synthetic used only if no real file/dir exists at that path |
| File data (`GetFileDataCallback`) | Real source checked first; synthetic content served only if the real file is absent |

When `--syntheticonly` is set, all real-source code paths are skipped:

```
OnStartDirEnum        → only adds synthetic children (no Directory.GetFileSystemInfos call)
OnGetPlaceholderInfo  → only writes synthetic placeholder (no File/Directory.Exists check)
OnGetFileData         → only calls ServeSyntheticContent (no FileStream open)
```

---

## Adding entries to the file list

Edit `<syntheticFileList>` in the config and restart. No recompile needed.

```
\Browser Data,true,0,1749252586
\Browser Data\Chrome,true,0,1749252586
\Browser Data\Chrome\Login Data,false,32768,1749252586
\Browser Data\Chrome\Cookies,false,4194304,1749252586
```

Add a matching template to `<syntheticTemplates>` if you want non-generic content when the file is read:

```xml
<template name="Login Data"><![CDATA[
SQLite format 3...
]]></template>
```

---

## Namespace: `Synthetic`

The three supporting classes are in the `Synthetic` namespace (not `SimpleProvider.Synthetic` — a namespace prefix that matches the class name `SimpleProvider` causes compiler error CS0101).

| Class | Key methods | Responsibility |
|---|---|---|
| `SyntheticEntry` | `GetFiletime()` | One entry from the file list after path normalisation |
| `SyntheticData` | `LoadFromConfig(path)`, `Find(path)`, `GetChildren(parent)` | Loads and indexes the file list; two case-insensitive dictionaries (`_byPath` for placeholder lookups, `_byParent` for directory listings) |
| `SyntheticContent` | `LoadFromConfig(path)`, `Generate(name, size)` | Maps filenames/extensions to templates; trims/pads to exact `fileSize` |

These classes are isolated from the P/Invoke layer (`Prj`) and the provider logic (`SimpleProvider`) so the synthetic subsystem can be read, tested, or replaced independently.

---

## Troubleshooting

**Nothing shows up in the virt root**
Check the startup output for these warnings:
```
[WARN] No <syntheticFileList> section in config. Add it to project synthetic entries.
[WARN] No synthetic file list loaded.
```
This means your config file does not have `<syntheticFileList>`. Add it (or replace the config file with the one in this repo).

**`[WARN] Config file not found`**
The config is missing or misnamed. It must be in the same directory as the exe and named `<exe-name>.exe.config`. If your exe is `SimpleProvider_Synthetic.exe`, the config must be `SimpleProvider_Synthetic.exe.config`.

**`[INFO] No <syntheticTemplates> section found in config`**
The templates section is missing. The XML root must be `<configuration>` with `<syntheticTemplates>` as a direct child.

**Synthetic files appear but content is just `# filename ...`**
No `<template>` matched this filename or extension. Add one to `<syntheticTemplates>`.

**`PrjStartVirtualizing` returns `0x80071126` (NotAReparsePoint)**
The provider automatically clears and recreates the virtroot on every start to prevent this. If it still occurs, delete the directory manually:
```
rmdir /s /q C:\FakeFiles
```

**`PrjStartVirtualizing` fails with `0x80070005` (Access Denied)**
The process must be run as Administrator.

**`PrjStartVirtualizing` fails with `0x80070032` (Not Supported)**
The `Client-ProjFS` optional feature is not enabled:
```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart
```

---

## File layout

```
SimpleProviderShowcase\
├── SimpleProvider.cs              Single source file; compile with csc.exe
├── SimpleProvider.exe.config      Virtual file layout + content templates
├── README.md                      Core ProjFS provider documentation
└── README-synthetic.md            This file
```
