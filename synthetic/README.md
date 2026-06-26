# SimpleProvider – Synthetic Data

The `--syntheticdata` and `--syntheticonly` flags extend the provider with a virtual file system layer defined by a plain CSV file.  Synthetic entries appear alongside (or instead of) real source files without any real data backing them.

---

## Quick start

```
# Mix real + synthetic
SimpleProvider.exe --sourceroot C:\EmptyOrRealDir --virtroot C:\Virt --syntheticdata entries.csv

# Synthetic only – no real source directory needed
SimpleProvider.exe --virtroot C:\Virt --syntheticdata entries.csv --syntheticonly
```

---

## Compile

`System.Xml.dll` is now required (used to read the content templates from the app config):

```
csc.exe /platform:x64 /r:System.Xml.dll /out:SimpleProvider.exe SimpleProvider.cs
```

---

## Flags

| Flag | Required | Description |
|---|---|---|
| `--sourceroot <path>` | Required unless `--syntheticonly` | Directory whose real files are projected |
| `--virtroot <path>` | Always required | Directory where the projection appears |
| `--syntheticdata <csv>` | Required for synthetic features | CSV file defining the virtual layout |
| `--syntheticonly` | Optional | Serve only synthetic content; real source is ignored entirely |

`--syntheticonly` requires `--syntheticdata`.

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
| `fileSize` | integer (bytes) | Reported size of the file. Use `0` for directories. |
| `unixTimestamp` | integer | Unix epoch seconds applied to all four NTFS timestamps. |

Paths must be declared top-down: a parent directory must appear before its children.

---

## Content templates – `SimpleProvider.exe.config`

File content returned when a synthetic file is read is defined entirely in the config file, not in the source code.  **No recompile is needed to change templates.**

### File placement

The config file must be in the same directory as the exe, named `<your-exe-name>.exe.config`:

```
SimpleProvider.exe
SimpleProvider.exe.config   ← templates live here
entries.csv
```

If the exe is renamed (e.g. to `SimpleProvider_Synthetic.exe`), rename the config to match:

```
SimpleProvider_Synthetic.exe
SimpleProvider_Synthetic.exe.config
```

### Template element attributes

| Attribute | Value | Description |
|---|---|---|
| `name` | filename (case-insensitive) | Exact filename match, e.g. `credentials`, `id_rsa.pub` |
| `extension` | file extension | Fallback when no exact name matches, e.g. `.pem`, `.json` |
| `type` | `"pem"` | Generate a deterministic LCG base64 PEM block scaled to `fileSize` |
| `pemLabel` | string | Text inside `-----BEGIN ... -----` (only used with `type="pem"`) |
| CDATA body | text | Static template; trimmed of whitespace then padded/truncated to `fileSize` |

### Lookup order

```
1. Exact filename match  (name="credentials")
2. Extension fallback    (extension=".json")
3. Built-in generic text (# filename / synthetic comment)
```

### Adding a new template

Open `SimpleProvider.exe.config` and add a `<template>` element inside `<syntheticTemplates>`:

```xml
<!-- Static text template matched by exact filename -->
<template name="vault_token"><![CDATA[
# HashiCorp Vault token
VAULT_TOKEN=hvs.EXAMPLE1234567890abcdefghijklmnopqrstuvwxyz
VAULT_ADDR=https://vault.example.com:8200
]]></template>

<!-- PEM block sized to the fileSize declared in the CSV -->
<template name="service_account.pem" type="pem" pemLabel="EC PRIVATE KEY" />

<!-- Extension fallback for any unmatched .yaml file -->
<template extension=".yaml"><![CDATA[
synthetic: true
generator: SimpleProvider
]]></template>
```

Restart the provider to pick up changes.

### CDATA whitespace

The XML parser includes a leading newline before the CDATA content.  `SyntheticContent.LoadFromConfig` trims leading and trailing whitespace from the node text before appending one `\n`, so the template starts and ends cleanly regardless of how it is indented in the XML file.

---

## Namespace: `SimpleProvider.Synthetic`

The three supporting classes live in the `SimpleProvider.Synthetic` namespace:

| Class | Responsibility |
|---|---|
| `SyntheticEntry` | One row from the CSV after path normalisation. `GetFiletime()` converts the Unix timestamp to a Windows FILETIME. |
| `SyntheticData` | Loads and indexes the CSV at startup. Two internal dictionaries (`_byPath` for placeholder lookups, `_byParent` for directory listing) both use case-insensitive comparison. |
| `SyntheticContent` | Generates file content at read time. `LoadFromConfig(path)` parses the XML config. `Generate(name, size)` resolves the template and trims/pads to the exact declared size. |

These classes are isolated from the P/Invoke layer (`Prj`) and the provider logic (`SimpleProvider`) so the synthetic subsystem can be read, tested, or replaced independently.

---

## Mixing: real vs synthetic priority

When `--syntheticonly` is **not** set, real and synthetic entries are merged during `StartDirectoryEnumerationCallback`:

| Rule | Behaviour |
|---|---|
| Listing | Real and synthetic entries are merged and sorted case-insensitively |
| Name conflict | Real file always wins; synthetic entry with the same name is silently dropped |
| Placeholder | Real source checked first; synthetic entry used only if no real file/dir exists |
| File data | Real source checked first; synthetic content served only if real file absent |

When `--syntheticonly` **is** set, all real-source code paths are skipped entirely:

```
OnStartDirEnum        → only adds synthetic children
OnGetPlaceholderInfo  → only writes synthetic placeholder
OnGetFileData         → only calls ServeSyntheticContent
```

---

## Console output

```
Loaded 15 content templates from config.
Loaded synthetic data: .\entries.csv
Clearing previous virtroot: C:\FakeFiles
Provider running.
  Virt      : C:\FakeFiles
  Synthetic : C:\entries.csv
  Mode      : synthetic only (no real source)
```

```
[14:03:12 INF] ----> GetPlaceholderInfoCallback [AWS]
[14:03:12 INF]   Placeholder creation triggered by [\Device\...\explorer.exe 9812]
[14:03:12 INF] <---- GetPlaceholderInfoCallback Ok [synthetic]
[14:03:14 INF] ----> GetFileDataCallback relativePath [AWS\credentials]
[14:03:14 INF]   triggered by [\Device\...\powershell.exe 6444]
[14:03:14 INF] <---- return status Ok [synthetic]
```

The `[synthetic]` tag on exit lines makes it easy to distinguish synthetic callbacks from real ones.

---

## Troubleshooting

**`PrjStartVirtualizing` returns `0x80071126` (NotAReparsePoint)**
The provider now automatically clears and recreates the virtroot on every start to avoid this.  If it persists, delete the directory manually:
```
rmdir /s /q C:\FakeFiles
```

**`[INFO] Config not found`**
The config file is missing or misnamed.  Check that `<exe-name>.exe.config` is in the same directory as the exe.

**`[INFO] No <syntheticTemplates> section found in config`**
The config file exists but the XML structure is wrong.  Check that the root is `<configuration>` and the child is `<syntheticTemplates>`.

**Synthetic file content is just `# filename ...`**
No matching `<template>` was found for that filename or extension.  Add one to the config.

**File content is shorter/longer than expected**
`FitToSize` pads with `\n` or truncates to hit exactly `fileSize` bytes.  Adjust `fileSize` in the CSV, or adjust the template content in the config.

---

## File layout

```
SimpleProviderShowcase\
├── SimpleProvider.cs          Single source file
├── SimpleProvider.exe.config  Content templates (edit without recompiling)
├── entries.csv                Example synthetic file tree
├── README.md                  Core provider documentation
└── README-synthetic.md        This file
```
