# SimpleProvider

A single-file, self-contained Windows Projected File System (ProjFS) provider written in C#.

It projects the contents of one directory (the **source root**) into another directory (the **virt root**). Files appear to exist in the virt root without being physically copied. Content is read from the source only when a process actually opens a file.

```
C:\Source\                       C:\Virt\
  report.pdf          ──────►     report.pdf    (placeholder – no bytes yet)
  data\                           data\
    sales.csv                       sales.csv   (placeholder)
    archive.zip                     archive.zip (placeholder)
```

The moment a process reads `C:\Virt\report.pdf`, ProjFS calls back into this provider, which streams the bytes from `C:\Source\report.pdf` on demand.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10 version 1809 or later | ProjFS ships as an optional component from 1809 onwards |
| ProjFS optional feature enabled | See below – one-time setup |
| .NET Framework 4.x runtime | Pre-installed on all modern Windows; no SDK needed to run |
| `csc.exe` | In `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\` |
| Administrator rights at runtime | ProjFS requires elevation |
| x64 machine | The native ProjFS filter driver is x64 only |

### Enable ProjFS (once, elevated PowerShell)

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart
```

Reboot if prompted. You can verify the driver loaded with:

```
fltmc | findstr PrjFlt
```

---

## Compile

No NuGet, no `.csproj`, no `/r:` references. `ProjectedFSLib.dll` is an inbox Windows system DLL loaded at runtime via P/Invoke.

```
csc.exe /platform:x64 /out:SimpleProvider.exe SimpleProvider.cs
```

The full path to `csc.exe` if it is not on your `PATH`:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

The source is written in C# 5 syntax so it compiles with the classic .NET Framework compiler with no flags beyond `/platform:x64`.

---

## Run

Open an **Administrator** command prompt:

```
SimpleProvider.exe --sourceroot C:\MySource --virtroot C:\MyVirt
```

| Argument | Required | Description |
|---|---|---|
| `--sourceroot` | Yes | Directory whose contents are projected |
| `--virtroot` | Yes | Directory where the projection appears; created if absent |

Press **Enter** to stop the provider cleanly.

---

## Console output

Every callback logs an entry line (`---->`) and an exit line (`<----`):

```
[13:42:46 INF] ----> StartDirectoryEnumerationCallback Path []
[13:42:46 INF] <---- StartDirectoryEnumerationCallback Ok
[13:42:46 INF] ----> GetDirectoryEnumerationCallback filterFileName [bo*]
[13:42:46 INF] <---- GetDirectoryEnumerationCallback Ok [Added entries: 1]
[13:42:46 INF] ----> GetDirectoryEnumerationCallback filterFileName []
[13:42:46 INF] <---- GetDirectoryEnumerationCallback Ok [Added entries: 0]
[13:42:46 INF] ----> EndDirectoryEnumerationCallback
[13:42:46 INF] <---- EndDirectoryEnumerationCallback Ok
[13:42:46 INF] ----> GetPlaceholderInfoCallback [boomer.txt]
[13:42:46 INF]   Placeholder creation triggered by [\Device\...\powershell.exe 6444]
[13:42:46 INF] <---- GetPlaceholderInfoCallback Ok
[13:42:46 INF] ----> GetFileDataCallback relativePath [boomer.txt]
[13:42:46 INF]   triggered by [\Device\...\powershell.exe 6444]
[13:42:46 INF] <---- return status Ok
```

| Log field | Meaning |
|---|---|
| `Path []` | Relative path inside the virt root; empty `[]` means the root directory itself |
| `filterFileName [bo*]` | Wildcard pattern the OS passed down (e.g. from `dir bo*`) |
| `Added entries: N` | How many directory entries fit into the kernel buffer in this call |
| `triggered by [path pid]` | The NT device path and PID of the process that caused the callback |
| `return status Ok` | The specific exit format used by `GetFileDataCallback` |

---

## How ProjFS works

```
  Your process
      │  open("C:\Virt\report.pdf")
      ▼
  NTFS / I/O Manager
      │  sees NTFS reparse point on C:\Virt
      ▼
  PrjFlt.sys  (kernel filter driver)
      │  file not yet hydrated → call user-mode provider
      ▼
  SimpleProvider.exe  (this process)
      │  reads C:\Source\report.pdf
      │  calls PrjWriteFileData
      ▼
  PrjFlt.sys  caches the bytes in the placeholder file
      ▼
  Your process receives the data
```

ProjFS installs a reparse point on the virt root directory when `PrjStartVirtualizing` is called. Subsequent file system operations on paths inside that root are intercepted by `PrjFlt.sys`, which routes them to the registered provider via the five callbacks below.

---

## The five callbacks

### 1. `StartDirectoryEnumerationCallback`

**When:** A process opens a directory for listing (e.g. `FindFirstFile`, `opendir`, Explorer browsing).

**What we do:** Call `GetFileSystemInfos()` on the matching source directory, sort the results in case-insensitive ordinal order (required by ProjFS), and store them in a per-session `EnumerationSession` keyed by the `enumerationId` GUID. Multiple directory listings can be in flight simultaneously; the GUID keeps them separate.

---

### 2. `GetDirectoryEnumerationCallback`

**When:** Called one or more times after `StartDirectoryEnumeration` to fill the OS buffer with directory entries.

**What we do:** Walk the `EnumerationSession` cursor, applying any wildcard filter via `PrjFileNameMatch`. Each entry is added to the kernel-side buffer by calling `PrjFillDirEntryBuffer`. When that function returns `HR_INSUFFICIENT_BUFFER` the buffer is full; we step the cursor back by one so that entry leads the next call. ProjFS calls again with the same session until we return without stepping back.

**Key detail – `restartScan`:** Bit 0 of `PRJ_CALLBACK_DATA.Flags` (`PRJ_CB_DATA_FLAG_ENUM_RESTART_SCAN`) signals that the listing should restart from the beginning. This happens when the caller rewinds the directory handle (e.g. `FindFirstFile` called again after a previous enumeration).

---

### 3. `EndDirectoryEnumerationCallback`

**When:** The directory handle is closed.

**What we do:** Remove the `EnumerationSession` from the dictionary.

---

### 4. `GetPlaceholderInfoCallback`

**When:** A process accesses a path inside the virt root for the first time (stat, `CreateFile`, `FindFirstFile` on a specific name). The path exists in the source but has no placeholder in the virt root yet.

**What we do:** Look up the file or directory in the source, then call `PrjWritePlaceholderInfo` to write a lightweight metadata-only record into the NTFS reparse point. No file content is copied at this stage. After this returns, the OS knows the item exists and has its correct size, timestamps, and attributes.

---

### 5. `GetFileDataCallback`

**When:** A process reads the content of a placeholder file whose bytes have not yet been cached locally (the file has not been "hydrated").

**What we do:** Open the source file, seek to `byteOffset` (ProjFS specifies exactly the range it needs), and stream the bytes back in 64 KB chunks. Each chunk is written into a sector-aligned buffer allocated with `PrjAllocateAlignedBuffer`, copied in with `Marshal.Copy`, then handed to ProjFS via `PrjWriteFileData`. After this call returns successfully, ProjFS caches the data and the placeholder transitions from a sparse virtual file to a fully hydrated local copy.

---

## Embedded P/Invoke layer

Rather than taking a NuGet dependency on `Microsoft.Windows.ProjFS`, all native declarations are embedded directly in the `Prj` static class. The three non-obvious decisions:

### Struct field offsets

`PRJ_FILE_BASIC_INFO` and `PRJ_CALLBACK_DATA` are declared with `[StructLayout(LayoutKind.Explicit)]` and hand-verified field offsets matching the MSVC x64 default struct layout. The critical rule is that each field is aligned to `min(sizeof(field), 8)` bytes. This produces two non-obvious padding gaps in `PRJ_CALLBACK_DATA`:

```
offset 36+16 = 52  →  next field (PCWSTR) must be at 56  →  4 bytes padding
offset 72+4  = 76  →  next field (PCWSTR) must be at 80  →  4 bytes padding
```

Getting these wrong silently produces garbage strings or crashes when reading the triggering process name or file path from callback data.

### `PRJ_PLACEHOLDER_INFO` as a byte array

The `PRJ_PLACEHOLDER_INFO` struct ends with a flexible array member (`VariableData[1]`), which cannot be declared directly in a C# struct. Instead, `Prj.BuildPlaceholderInfo` constructs the 344-byte native layout manually using little-endian integer writes, then passes it as `[In] byte[]` to `PrjWritePlaceholderInfo`. The size calculation is:

```
PRJ_FILE_BASIC_INFO      56 bytes
EaInformation             8 bytes
SecurityInformation       8 bytes
StreamsInformation        8 bytes
VersionInfo.ProviderID  128 bytes
VersionInfo.ContentID   128 bytes
VariableData[0]           1 byte
Padding to 8-byte align   7 bytes
                        ─────────
Total                   344 bytes
```

### Aligned write buffer

`PrjWriteFileData` requires a buffer allocated by `PrjAllocateAlignedBuffer` because it must be sector-aligned for direct I/O to the volume. Rather than using `unsafe` code to write directly to the native pointer, managed bytes are staged in a `byte[]` first then copied with `Marshal.Copy(byte[], int, IntPtr, int)`.

### Delegate lifetime

`Marshal.GetFunctionPointerForDelegate` does **not** root the delegate against the garbage collector. If the delegate object is collected, the native function pointer becomes a dangling pointer and ProjFS will crash the process the next time it fires a callback. The five `_cbXxx` fields on `SimpleProvider` are the only GC roots keeping them alive; they must remain reachable for the entire lifetime of the virtualization instance.

---

## Intentionally omitted

| Feature | Why omitted |
|---|---|
| Notification callbacks (`IOptionalCallbacks`) | Not required for basic projection; used to track modifications to hydrated files |
| Symlink support (`PrjFillDirEntryBuffer2`, `PrjWritePlaceholderInfo2`) | Requires Windows 11 / Server 2022 and NTFS; adds ~100 lines for one edge case |
| Negative path cache | Speeds up repeated lookups of non-existent paths; omitted to keep all callbacks firing visibly |
| Cache invalidation via `contentId` / `providerId` | Source is treated as read-only during the session; both tokens are zeroed |
| `PRJ_CB_DATA_FLAG_ENUM_RETURN_SINGLE_ENTRY` | The `HR_INSUFFICIENT_BUFFER` loop already handles this correctly in practice |
| Multiple concurrent providers | `SimpleProvider._current` is a static field; a second instance would overwrite it |

---

## Troubleshooting

**`PrjStartVirtualizing` returns `0x80070005` (Access Denied)**
The process must be run as Administrator.

**`PrjStartVirtualizing` returns `0x80070032` (Not Supported)**
The `Client-ProjFS` optional feature is not enabled, or `PrjFlt.sys` is not loaded. Run `fltmc` to check loaded filters.

**`PrjStartVirtualizing` returns `0x80070002` (File Not Found)**
The virt root path does not exist. The provider calls `Directory.CreateDirectory` in the constructor; if that fails silently, the directory may be on a volume that does not support reparse points (e.g. FAT32). ProjFS requires NTFS.

**Callbacks fire but files show as empty**
The source root path is wrong or inaccessible. Check the `FileNotFound` log lines from `GetFileDataCallback`.

**Provider stops responding after a crash**
If the provider process exits while `PrjFlt.sys` still has the reparse point registered, the virt root will return `ERROR_PROVIDER_NOT_AVAILABLE` to callers. Delete the virt root directory and recreate it before starting the provider again.

**`BadImageFormatException` at startup**
The executable was compiled without `/platform:x64`. Recompile with that flag; ProjFS is x64-only.
