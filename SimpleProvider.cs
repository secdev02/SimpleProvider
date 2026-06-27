// SimpleProvider.cs
// Self-contained ProjFS provider.  All P/Invoke declarations are embedded.
// Both the virtual file layout and content templates live in <exe>.exe.config.
//
// PREREQUISITES
//   Windows 10 v1809+ with ProjFS enabled (once, elevated PowerShell):
//     Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart
//
// COMPILE  (System.Xml.dll needed for app-config loading)
//   csc.exe /platform:x64 /r:System.Xml.dll /out:SimpleProvider.exe SimpleProvider.cs
//
// RUN  (Administrator required)
//   -- Real files only:
//   SimpleProvider.exe --sourceroot C:\MySource --virtroot C:\MyVirt
//
//   -- Config file list mixed with real files:
//   SimpleProvider.exe --sourceroot C:\MySource --virtroot C:\MyVirt
//
//   -- Config file list only (no real source needed):
//   SimpleProvider.exe --virtroot C:\MyVirt --syntheticonly
//
// CONFIGURATION  (<exe-name>.exe.config, same directory as the exe)
//   <syntheticFileList>  – virtual file tree (CDATA, same format as old CSV)
//   <syntheticTemplates> – content returned when synthetic files are read
//   Edit and restart; no recompile needed.
//
// NOTE: Written in C# 5 syntax for compatibility with the .NET Framework 4.x
//       csc.exe at C:\Windows\Microsoft.NET\Framework64\v4.0.30319\.
//       Namespace is "Synthetic" (not "SimpleProvider.Synthetic") to avoid a
//       name collision with the SimpleProvider class in the global namespace.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Synthetic;

// =============================================================================
// Synthetic virtual file system  (namespace: Synthetic)
// =============================================================================
namespace Synthetic
{
    // -------------------------------------------------------------------------
    // SyntheticEntry  – one row from the CSV after normalisation.
    // RelativePath uses backslash separators with NO leading backslash.
    // -------------------------------------------------------------------------
    internal sealed class SyntheticEntry
    {
        public string RelativePath;   // e.g. "AWS\credentials"
        public string Name;           // e.g. "credentials"
        public string ParentPath;     // e.g. "AWS"  (empty string = root level)
        public bool   IsDirectory;
        public long   FileSize;
        public long   UnixTimestamp;

        // Returns a Windows FILETIME (100-ns intervals since 1601-01-01).
        public long GetFiletime()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds((double)UnixTimestamp).ToFileTime();
        }
    }

    // -------------------------------------------------------------------------
    // SyntheticData  – loads and indexes the virtual file tree.
    //
    // Source: LoadFromConfig reads the <syntheticFileList> CDATA from the app config.
    //
    // Line format:  \Path\To\Entry,isDirectory,fileSize,unixTimestamp
    // -------------------------------------------------------------------------
    internal sealed class SyntheticData
    {
        private readonly Dictionary<string, SyntheticEntry>       _byPath;
        private readonly Dictionary<string, List<SyntheticEntry>> _byParent;

        private SyntheticData()
        {
            _byPath   = new Dictionary<string, SyntheticEntry>(StringComparer.OrdinalIgnoreCase);
            _byParent = new Dictionary<string, List<SyntheticEntry>>(StringComparer.OrdinalIgnoreCase);
        }

        // Reads the <syntheticFileList> CDATA block from the app config.
        // Returns null when the section is absent and prints a clear diagnostic.
        public static SyntheticData LoadFromConfig(string configPath)
        {
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine("[WARN] Config file not found: " + configPath);
                return null;
            }

            try
            {
                XmlDocument doc  = new XmlDocument();
                doc.Load(configPath);

                XmlNode node = doc.SelectSingleNode("/configuration/syntheticFileList");
                if (node == null)
                {
                    Console.Error.WriteLine(
                        "[WARN] No <syntheticFileList> section in config. " +
                        "Add it to project synthetic entries.");
                    return null;
                }

                string raw = node.InnerText;
                if (string.IsNullOrEmpty(raw.Trim()))
                {
                    Console.Error.WriteLine("[WARN] <syntheticFileList> is empty.");
                    return null;
                }

                string[] lines = raw.Split(new char[] { '\r', '\n' },
                                           StringSplitOptions.None);
                SyntheticData data = ParseLines(lines);
                return data;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WARN] Could not load file list from config: " + ex.Message);
                return null;
            }
        }

        // Total number of entries loaded (files + directories).
        public int EntryCount
        {
            get { return _byPath.Count; }
        }

        // Core parser used by LoadFromConfig.
        private static SyntheticData ParseLines(string[] lines)
        {
            SyntheticData data = new SyntheticData();

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    continue;

                string[] parts = trimmed.Split(',');
                if (parts.Length < 4) continue;

                bool isDir;
                long fileSize;
                long unixTs;

                if (!bool.TryParse(parts[1].Trim(), out isDir))    continue;
                if (!long.TryParse(parts[2].Trim(), out fileSize))  continue;
                if (!long.TryParse(parts[3].Trim(), out unixTs))    continue;

                string normalPath = parts[0].Trim().TrimStart('\\');

                SyntheticEntry entry = new SyntheticEntry();
                entry.RelativePath  = normalPath;
                entry.IsDirectory   = isDir;
                entry.FileSize      = fileSize;
                entry.UnixTimestamp = unixTs;

                int lastSlash = normalPath.LastIndexOf('\\');
                if (lastSlash >= 0)
                {
                    entry.Name       = normalPath.Substring(lastSlash + 1);
                    entry.ParentPath = normalPath.Substring(0, lastSlash);
                }
                else
                {
                    entry.Name       = normalPath;
                    entry.ParentPath = string.Empty;
                }

                data._byPath[normalPath] = entry;

                List<SyntheticEntry> siblings;
                if (!data._byParent.TryGetValue(entry.ParentPath, out siblings))
                {
                    siblings = new List<SyntheticEntry>();
                    data._byParent[entry.ParentPath] = siblings;
                }
                siblings.Add(entry);
            }

            return data;
        }

        // Returns the entry for a relative path, or null.
        public SyntheticEntry Find(string relativePath)
        {
            if (relativePath == null) relativePath = string.Empty;
            SyntheticEntry entry;
            if (_byPath.TryGetValue(relativePath, out entry)) return entry;
            return null;
        }

        // Returns all direct children of a parent path.  Pass "" for root level.
        public List<SyntheticEntry> GetChildren(string parentRelativePath)
        {
            if (parentRelativePath == null) parentRelativePath = string.Empty;
            List<SyntheticEntry> result;
            if (_byParent.TryGetValue(parentRelativePath, out result)) return result;
            return new List<SyntheticEntry>();
        }
    }

    // -------------------------------------------------------------------------
    // SyntheticContent  – generates plausible file content for synthetic entries.
    //
    // Templates are loaded at startup from <exe>.exe.config via LoadFromConfig().
    // Each <template> element specifies either:
    //   name="filename"        – exact filename match (case-insensitive)
    //   extension=".ext"       – file extension fallback
    // and either:
    //   type="pem" pemLabel="RSA PRIVATE KEY"   – LCG-generated PEM block
    //   (CDATA text)                            – static text template
    //
    // Content is trimmed or padded with newlines to hit the declared fileSize.
    // -------------------------------------------------------------------------
    internal static class SyntheticContent
    {
        private sealed class TemplateEntry
        {
            public bool   IsPem;
            public string PemLabel;
            public string Text;
        }

        // Exact filename → template  (case-insensitive, populated by LoadFromConfig)
        private static Dictionary<string, TemplateEntry> _exact;
        // File extension → template  (e.g. ".json")
        private static Dictionary<string, TemplateEntry> _ext;

        // Call once at startup with the path to <exe>.exe.config.
        // Silently continues with no templates if the file is missing.
        public static void LoadFromConfig(string configPath)
        {
            _exact = new Dictionary<string, TemplateEntry>(StringComparer.OrdinalIgnoreCase);
            _ext   = new Dictionary<string, TemplateEntry>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(configPath))
            {
                Console.WriteLine("[INFO] Config not found: " + configPath);
                Console.WriteLine("[INFO] Synthetic files will return generic text content.");
                return;
            }

            int count = 0;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(configPath);

                XmlNodeList nodes = doc.SelectNodes("/configuration/syntheticTemplates/template");
                if (nodes == null)
                {
                    Console.WriteLine("[INFO] No <syntheticTemplates> section found in config.");
                    return;
                }

                foreach (XmlNode node in nodes)
                {
                    string nameVal = Attr(node, "name");
                    string extVal  = Attr(node, "extension");
                    string typeVal = Attr(node, "type");
                    string pemVal  = Attr(node, "pemLabel");

                    bool isPem = string.Equals(typeVal, "pem", StringComparison.OrdinalIgnoreCase);

                    TemplateEntry entry = new TemplateEntry();
                    entry.IsPem    = isPem;
                    entry.PemLabel = (isPem && pemVal != null) ? pemVal : "PRIVATE KEY";
                    // Trim leading/trailing whitespace from CDATA (XML formatting artefact),
                    // then re-add a single trailing newline.
                    entry.Text     = isPem ? null : (node.InnerText.Trim() + "\n");

                    if (nameVal != null)
                    {
                        _exact[nameVal.ToLowerInvariant()] = entry;
                        count++;
                    }
                    else if (extVal != null)
                    {
                        _ext[extVal.ToLowerInvariant()] = entry;
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WARN] Could not load templates from config: " + ex.Message);
                return;
            }

            Console.WriteLine("Loaded " + count + " content templates from config.");
        }

        // Returns exactly declaredSize bytes of content for the given file name.
        public static byte[] Generate(string fileName, long declaredSize)
        {
            string lower = fileName.ToLowerInvariant();
            string text  = ResolveTemplate(lower, declaredSize);
            return FitToSize(Encoding.UTF8.GetBytes(text), declaredSize);
        }

        private static string ResolveTemplate(string lower, long size)
        {
            TemplateEntry entry;

            // Exact filename match first
            if (_exact != null && _exact.TryGetValue(lower, out entry))
                return Produce(entry, size);

            // Extension fallback
            string ext = Path.GetExtension(lower);
            if (!string.IsNullOrEmpty(ext) && _ext != null && _ext.TryGetValue(ext, out entry))
                return Produce(entry, size);

            // Generic fallback when no template is configured
            return "# " + lower + "\n# Synthetic file – add a <template> to SimpleProvider.exe.config\n";
        }

        private static string Produce(TemplateEntry entry, long size)
        {
            if (entry.IsPem) return PemBlock(entry.PemLabel, size);
            return entry.Text ?? string.Empty;
        }

        // LCG-generated PEM block scaled to approximately targetSize bytes.
        // FitToSize trims or pads to hit the exact declared file size.
        private static string PemBlock(string label, long targetSize)
        {
            string header  = "-----BEGIN " + label + "-----\n";
            string footer  = "\n-----END " + label + "-----\n";
            int bodyTarget = (int)targetSize - header.Length - footer.Length;
            if (bodyTarget < 64) bodyTarget = 64;
            return header + FakeBase64(bodyTarget) + footer;
        }

        // Deterministic LCG fake base64 – same seed yields identical output.
        private static string FakeBase64(int approxLen)
        {
            const string chars =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            int state = unchecked(0x12345678);
            StringBuilder sb = new StringBuilder(approxLen + 80);
            int col = 0;
            while (sb.Length < approxLen)
            {
                state = unchecked(state * 1664525 + 1013904223);
                sb.Append(chars[((state >> 8) & 0x7FFFFFFF) % 64]);
                col++;
                if (col == 64) { sb.Append('\n'); col = 0; }
            }
            if (col > 0) sb.Append('\n');
            return sb.ToString();
        }

        private static byte[] FitToSize(byte[] raw, long size)
        {
            if (size <= 0) return new byte[0];
            byte[] result = new byte[(int)size];
            if (raw.Length >= (int)size)
            {
                Array.Copy(raw, result, (int)size);
            }
            else
            {
                Array.Copy(raw, result, raw.Length);
                for (int i = raw.Length; i < (int)size; i++)
                    result[i] = (byte)'\n';
            }
            return result;
        }

        private static string Attr(XmlNode node, string name)
        {
            if (node.Attributes == null) return null;
            XmlAttribute a = node.Attributes[name];
            return a != null ? a.Value : null;
        }
    }
}

// =============================================================================
// P/Invoke layer for ProjectedFSLib.dll
// =============================================================================
internal static class Prj
{
    public const int S_OK                   = 0;
    public const int E_FAIL                 = unchecked((int)0x80004005);
    public const int HR_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);
    public const int HR_FILE_NOT_FOUND      = unchecked((int)0x80070002);
    public const int HR_PATH_NOT_FOUND      = unchecked((int)0x80070003);

    // Returned by PrjStartVirtualizing when the virtroot has not been stamped
    // with PrjMarkDirectoryAsPlaceholder, or contains stale placeholder files.
    public const int HR_NOT_A_REPARSE_POINT = unchecked((int)0x80071126);

    public const int FLAG_ENUM_RESTART_SCAN        = 0x00000001;
    public const int FLAG_ENUM_RETURN_SINGLE_ENTRY = 0x00000002;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int StartDirEnumCb(IntPtr cbd, IntPtr enumId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int EndDirEnumCb(IntPtr cbd, IntPtr enumId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetDirEnumCb(IntPtr cbd, IntPtr enumId, IntPtr searchExpr, IntPtr dirEntryBuf);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetPlaceholderInfoCb(IntPtr cbd);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetFileDataCb(IntPtr cbd, ulong byteOffset, uint length);

    [StructLayout(LayoutKind.Sequential)]
    public struct Callbacks
    {
        public IntPtr StartDirEnum;
        public IntPtr EndDirEnum;
        public IntPtr GetDirEnum;
        public IntPtr GetPlaceholderInfo;
        public IntPtr GetFileData;
        public IntPtr QueryFileName;
        public IntPtr Notification;
        public IntPtr CancelCommand;
    }

    // PRJ_FILE_BASIC_INFO – explicit MSVC x64 layout (56 bytes)
    //   offset  0: BOOLEAN IsDirectory (1 byte + 7 bytes padding)
    //   offset  8: INT64 FileSize
    //   offset 16: INT64 CreationTime   (100-ns ticks since 1601-01-01)
    //   offset 24: INT64 LastAccessTime
    //   offset 32: INT64 LastWriteTime
    //   offset 40: INT64 ChangeTime
    //   offset 48: UINT32 FileAttributes (+ 4 bytes padding)
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    public struct FileBasicInfo
    {
        [FieldOffset( 0)] public byte IsDirectory;
        [FieldOffset( 8)] public long FileSize;
        [FieldOffset(16)] public long CreationTime;
        [FieldOffset(24)] public long LastAccessTime;
        [FieldOffset(32)] public long LastWriteTime;
        [FieldOffset(40)] public long ChangeTime;
        [FieldOffset(48)] public uint FileAttributes;
    }

    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjStartVirtualizing(
        string        rootPath,
        ref Callbacks callbacks,
        IntPtr        instanceContext,
        IntPtr        options,
        out IntPtr    virtualizationContext);

    [DllImport("ProjectedFSLib.dll")]
    public static extern void PrjStopVirtualizing(IntPtr virtCtx);

    // Must be called on the virtroot directory before PrjStartVirtualizing.
    // Pass null for targetPathName to mark rootPathName itself as the virt root.
    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjMarkDirectoryAsPlaceholder(
        string   rootPathName,
        string   targetPathName,
        IntPtr   versionInfo,
        ref Guid virtualizationInstanceID);

    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjWritePlaceholderInfo(
        IntPtr      virtCtx,
        string      destinationFileName,
        [In] byte[] placeholderInfo,
        uint        placeholderInfoSize);

    [DllImport("ProjectedFSLib.dll")]
    public static extern int PrjWriteFileData(
        IntPtr   virtCtx,
        ref Guid dataStreamId,
        IntPtr   buffer,
        ulong    byteOffset,
        uint     length);

    [DllImport("ProjectedFSLib.dll")]
    public static extern IntPtr PrjAllocateAlignedBuffer(IntPtr virtCtx, UIntPtr size);

    [DllImport("ProjectedFSLib.dll")]
    public static extern void PrjFreeAlignedBuffer(IntPtr buffer);

    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjFillDirEntryBuffer(
        string            fileName,
        ref FileBasicInfo fileBasicInfo,
        IntPtr            dirEntryBufferHandle);

    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrjFileNameMatch(string fileNameToCheck, string pattern);

    // PRJ_CALLBACK_DATA field offsets (x64):
    //   4  UINT32 Flags  |  8  HANDLE VirtCtx  |  36 GUID DataStreamId
    //  56  PCWSTR FilePath  |  72 UINT32 TrigPid  |  80 PCWSTR TrigProc
    public static int    CbdFlags(IntPtr cbd)       { return Marshal.ReadInt32(cbd, 4); }
    public static IntPtr CbdVirtCtx(IntPtr cbd)     { return Marshal.ReadIntPtr(cbd, 8); }
    public static string CbdFilePath(IntPtr cbd)    { return PcwstrAt(cbd, 56); }
    public static uint   CbdTriggerPid(IntPtr cbd)  { return (uint)Marshal.ReadInt32(cbd, 72); }
    public static string CbdTriggerProc(IntPtr cbd) { return PcwstrAt(cbd, 80); }

    public static Guid CbdDataStreamId(IntPtr cbd)
    {
        return (Guid)Marshal.PtrToStructure(IntPtr.Add(cbd, 36), typeof(Guid));
    }

    public static Guid ReadGuid(IntPtr ptr)
    {
        return (Guid)Marshal.PtrToStructure(ptr, typeof(Guid));
    }

    public static string ReadPcwstr(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        return Marshal.PtrToStringUni(p);
    }

    private static string PcwstrAt(IntPtr cbd, int offset)
    {
        IntPtr p = Marshal.ReadIntPtr(cbd, offset);
        if (p == IntPtr.Zero) return null;
        return Marshal.PtrToStringUni(p);
    }

    // PRJ_PLACEHOLDER_INFO as byte[] (344 bytes):
    //    0– 55: PRJ_FILE_BASIC_INFO
    //   56– 79: EaInfo / SecurityInfo / StreamsInfo (all zero)
    //   80–335: VersionInfo.ProviderID + ContentID (all zero = token {0})
    //  336–343: VariableData + padding
    public static byte[] BuildPlaceholderInfo(
        bool isDirectory, long fileSize,
        long creationTime, long lastAccessTime, long lastWriteTime, long changeTime,
        uint fileAttributes)
    {
        byte[] buf = new byte[344];
        buf[0] = isDirectory ? (byte)1 : (byte)0;
        Wi64(buf,  8, isDirectory ? 0L : fileSize);
        Wi64(buf, 16, creationTime);
        Wi64(buf, 24, lastAccessTime);
        Wi64(buf, 32, lastWriteTime);
        Wi64(buf, 40, changeTime);
        Wu32(buf, 48, fileAttributes);
        return buf;
    }

    private static void Wi64(byte[] b, int o, long v)
    {
        ulong u = (ulong)v;
        for (int i = 0; i < 8; i++) b[o + i] = (byte)(u >> (i * 8));
    }

    private static void Wu32(byte[] b, int o, uint v)
    {
        for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (i * 8));
    }

    public static string Hr(int hr)
    {
        switch (hr)
        {
            case S_OK:                    return "Ok";
            case HR_FILE_NOT_FOUND:       return "FileNotFound";
            case HR_PATH_NOT_FOUND:       return "PathNotFound";
            case HR_INSUFFICIENT_BUFFER:  return "InsufficientBuffer";
            case HR_NOT_A_REPARSE_POINT:  return "NotAReparsePoint";
            case E_FAIL:                  return "InternalError";
            default:                      return "0x" + ((uint)hr).ToString("X8");
        }
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        string sourceRoot    = null;
        string virtRoot      = null;
        bool   syntheticOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i];

            if (flag.Equals("--syntheticonly", StringComparison.OrdinalIgnoreCase))
            {
                syntheticOnly = true;
                continue;
            }

            if (i + 1 >= args.Length) continue;
            string val = args[i + 1];

            if      (flag.Equals("--sourceroot", StringComparison.OrdinalIgnoreCase)) { sourceRoot = val; i++; }
            else if (flag.Equals("--virtroot",   StringComparison.OrdinalIgnoreCase)) { virtRoot   = val; i++; }
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------
        if (virtRoot == null)
        {
            PrintUsage();
            return 1;
        }

        if (!syntheticOnly && sourceRoot == null)
        {
            Console.Error.WriteLine("--sourceroot is required unless --syntheticonly is specified.");
            PrintUsage();
            return 1;
        }

        // ------------------------------------------------------------------
        // Load config  (<exe>.exe.config next to the executable)
        // Both the virtual file list and the content templates live here.
        // ------------------------------------------------------------------
        System.Reflection.Assembly asm = System.Reflection.Assembly.GetEntryAssembly();
        string configPath = (asm != null)
            ? asm.Location + ".config"
            : Path.Combine(Directory.GetCurrentDirectory(), "SimpleProvider.exe.config");

        Console.WriteLine("Config : " + configPath);

        // Content templates (what is returned when a synthetic file is read)
        SyntheticContent.LoadFromConfig(configPath);

        // Virtual file list  (<syntheticFileList> CDATA in the config)
        SyntheticData synthetic = SyntheticData.LoadFromConfig(configPath);
        if (synthetic != null)
        {
            Console.WriteLine("Loaded " + synthetic.EntryCount + " synthetic entries from config.");
        }
        else if (!syntheticOnly)
        {
            // Not fatal in mixed mode, but tell the user clearly.
            Console.Error.WriteLine("[WARN] No synthetic file list loaded. " +
                "Add a <syntheticFileList> section to the config if you want " +
                "synthetic entries in the virt root.");
        }

        if (syntheticOnly && synthetic == null)
        {
            Console.Error.WriteLine("--syntheticonly requires a <syntheticFileList> section in the config.");
            Console.Error.WriteLine("Config searched: " + configPath);
            return 1;
        }

        // ------------------------------------------------------------------
        // Create and start the provider
        // ------------------------------------------------------------------
        SimpleProvider provider;
        try
        {
            provider = new SimpleProvider(sourceRoot, virtRoot, synthetic, syntheticOnly);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed to create provider: " + ex.Message);
            return 1;
        }

        int hr = provider.StartVirtualizing();
        if (hr != Prj.S_OK)
        {
            Console.Error.WriteLine("PrjStartVirtualizing failed: " + Prj.Hr(hr));
            if (hr == Prj.HR_NOT_A_REPARSE_POINT)
            {
                Console.Error.WriteLine("The virtroot could not be stamped as a ProjFS root.");
                Console.Error.WriteLine("Try:  rmdir /s /q \"" + Path.GetFullPath(virtRoot) + "\"");
            }
            else
            {
                Console.Error.WriteLine("Ensure Client-ProjFS is enabled and this process is elevated.");
            }
            return 1;
        }

        Console.WriteLine("Provider running.");
        Console.WriteLine("  Virt   : " + Path.GetFullPath(virtRoot));
        if (!syntheticOnly)
            Console.WriteLine("  Source : " + Path.GetFullPath(sourceRoot));
        if (syntheticOnly)
            Console.WriteLine("  Mode   : synthetic only (no real source)");
        Console.WriteLine("Press ENTER to stop...");
        Console.ReadLine();

        provider.StopVirtualizing();
        Console.WriteLine("Stopped.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SimpleProvider.exe --virtroot <path> [--sourceroot <path>] [--syntheticonly]");
        Console.WriteLine();
        Console.WriteLine("  Virtual file layout and content templates are read from");
        Console.WriteLine("  <exe-name>.exe.config in the same directory as the executable.");
        Console.WriteLine("  No external CSV file is needed.");
        Console.WriteLine();
        Console.WriteLine("  --syntheticonly  Ignore real source; serve only the config file list.");
        Console.WriteLine("                   Requires <syntheticFileList> in the config.");
        Console.WriteLine();
        Console.WriteLine("Prerequisites:");
        Console.WriteLine("  Run as Administrator.");
        Console.WriteLine("  Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart");
    }
}

// =============================================================================
// ProjFS provider
// =============================================================================
internal sealed class SimpleProvider
{
    // ---- Fields ----

    private readonly string        _sourceRoot;
    private readonly string        _virtRoot;
    private readonly SyntheticData _synthetic;
    private readonly bool          _syntheticOnly;
    private IntPtr                 _virtCtx;

    private readonly ConcurrentDictionary<Guid, EnumerationSession> _sessions
        = new ConcurrentDictionary<Guid, EnumerationSession>();

    private static SimpleProvider _current;

    // Delegates must be stored as fields to prevent GC while ProjFS is active.
    private readonly Prj.StartDirEnumCb       _cbStartDirEnum;
    private readonly Prj.EndDirEnumCb         _cbEndDirEnum;
    private readonly Prj.GetDirEnumCb         _cbGetDirEnum;
    private readonly Prj.GetPlaceholderInfoCb _cbGetPlaceholderInfo;
    private readonly Prj.GetFileDataCb        _cbGetFileData;

    // ---- DirEntry: unified listing entry (real or synthetic) ----

    private struct DirEntry
    {
        public string Name;
        public bool   IsSynthetic;
        public bool   IsDirectory;
        public long   FileSize;
        public long   CreationTimeFt;
        public long   LastAccessTimeFt;
        public long   LastWriteTimeFt;
        public uint   FileAttributes;
    }

    // ---- Construction ----

    public SimpleProvider(
        string        sourceRoot,
        string        virtRoot,
        SyntheticData synthetic,
        bool          syntheticOnly)
    {
        _syntheticOnly = syntheticOnly;
        _synthetic     = synthetic;
        _virtRoot      = Path.GetFullPath(virtRoot);

        if (syntheticOnly)
        {
            _sourceRoot = string.Empty;
        }
        else
        {
            if (!Directory.Exists(sourceRoot))
                throw new ArgumentException("Source root does not exist: " + sourceRoot);
            _sourceRoot = Path.GetFullPath(sourceRoot);
        }

        Directory.CreateDirectory(_virtRoot);

        _cbStartDirEnum       = StartDirEnumThunk;
        _cbEndDirEnum         = EndDirEnumThunk;
        _cbGetDirEnum         = GetDirEnumThunk;
        _cbGetPlaceholderInfo = GetPlaceholderInfoThunk;
        _cbGetFileData        = GetFileDataThunk;
    }

    // ---- Lifecycle ----

    public int StartVirtualizing()
    {
        _current = this;

        // Guarantee a clean, reparse-point-free directory.
        // PrjStartVirtualizing returns 0x80071126 (ERROR_NOT_A_REPARSE_POINT) if
        // the directory retains stale state from a session that did not call
        // PrjStopVirtualizing.
        if (Directory.Exists(_virtRoot))
        {
            Console.WriteLine("Clearing previous virtroot: " + _virtRoot);
            try { Directory.Delete(_virtRoot, true); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[WARN] Could not fully clear virtroot: " + ex.Message);
            }
        }
        Directory.CreateDirectory(_virtRoot);

        // PrjStartVirtualizing requires the directory to be pre-stamped with the
        // ProjFS NTFS reparse point.  Must be called on every fresh directory.
        Guid instanceId = Guid.NewGuid();
        int markHr = Prj.PrjMarkDirectoryAsPlaceholder(
            _virtRoot, null, IntPtr.Zero, ref instanceId);
        if (markHr != Prj.S_OK)
        {
            Console.Error.WriteLine("PrjMarkDirectoryAsPlaceholder failed: " + Prj.Hr(markHr));
            return markHr;
        }

        Prj.Callbacks cbs = new Prj.Callbacks();
        cbs.StartDirEnum       = Marshal.GetFunctionPointerForDelegate(_cbStartDirEnum);
        cbs.EndDirEnum         = Marshal.GetFunctionPointerForDelegate(_cbEndDirEnum);
        cbs.GetDirEnum         = Marshal.GetFunctionPointerForDelegate(_cbGetDirEnum);
        cbs.GetPlaceholderInfo = Marshal.GetFunctionPointerForDelegate(_cbGetPlaceholderInfo);
        cbs.GetFileData        = Marshal.GetFunctionPointerForDelegate(_cbGetFileData);

        return Prj.PrjStartVirtualizing(_virtRoot, ref cbs, IntPtr.Zero, IntPtr.Zero, out _virtCtx);
    }

    public void StopVirtualizing()
    {
        if (_virtCtx == IntPtr.Zero) return;
        Prj.PrjStopVirtualizing(_virtCtx);
        _virtCtx = IntPtr.Zero;
    }

    // ---- Static callback thunks ----

    private static int StartDirEnumThunk(IntPtr cbd, IntPtr enumId)
    {
        try   { return _current.OnStartDirEnum(cbd, enumId); }
        catch (Exception ex) { Log("[EXCEPTION] " + ex.Message); return Prj.E_FAIL; }
    }

    private static int EndDirEnumThunk(IntPtr cbd, IntPtr enumId)
    {
        try   { return _current.OnEndDirEnum(cbd, enumId); }
        catch (Exception ex) { Log("[EXCEPTION] " + ex.Message); return Prj.E_FAIL; }
    }

    private static int GetDirEnumThunk(IntPtr cbd, IntPtr enumId, IntPtr se, IntPtr db)
    {
        try   { return _current.OnGetDirEnum(cbd, enumId, se, db); }
        catch (Exception ex) { Log("[EXCEPTION] " + ex.Message); return Prj.E_FAIL; }
    }

    private static int GetPlaceholderInfoThunk(IntPtr cbd)
    {
        try   { return _current.OnGetPlaceholderInfo(cbd); }
        catch (Exception ex) { Log("[EXCEPTION] " + ex.Message); return Prj.E_FAIL; }
    }

    private static int GetFileDataThunk(IntPtr cbd, ulong byteOffset, uint length)
    {
        try   { return _current.OnGetFileData(cbd, byteOffset, length); }
        catch (Exception ex) { Log("[EXCEPTION] " + ex.Message); return Prj.E_FAIL; }
    }

    // ---- Callback implementations ----

    private int OnStartDirEnum(IntPtr cbd, IntPtr enumIdPtr)
    {
        string relativePath = Prj.CbdFilePath(cbd);
        if (relativePath == null) relativePath = string.Empty;
        Log("----> StartDirectoryEnumerationCallback Path [" + relativePath + "]");

        List<DirEntry> all = new List<DirEntry>();

        // Real source entries (skipped in syntheticOnly mode)
        if (!_syntheticOnly)
        {
            string fullPath = FullSourcePath(relativePath);
            if (Directory.Exists(fullPath))
            {
                try
                {
                    FileSystemInfo[] real = new DirectoryInfo(fullPath).GetFileSystemInfos();
                    foreach (FileSystemInfo info in real)
                        all.Add(RealToDirEntry(info));
                }
                catch (Exception ex)
                {
                    Log("  [WARN] Could not read source directory: " + ex.Message);
                }
            }
        }

        // Synthetic children – skip any whose name conflicts with a real entry
        if (_synthetic != null)
        {
            List<SyntheticEntry> synths = _synthetic.GetChildren(relativePath);
            foreach (SyntheticEntry s in synths)
            {
                if (!NameExists(all, s.Name))
                    all.Add(SyntheticToDirEntry(s));
            }
        }

        all.Sort(delegate(DirEntry a, DirEntry b)
        {
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        _sessions[Prj.ReadGuid(enumIdPtr)] = new EnumerationSession(all.ToArray());
        Log("<---- StartDirectoryEnumerationCallback Ok");
        return Prj.S_OK;
    }

    private int OnEndDirEnum(IntPtr cbd, IntPtr enumIdPtr)
    {
        Log("----> EndDirectoryEnumerationCallback");
        EnumerationSession removed;
        _sessions.TryRemove(Prj.ReadGuid(enumIdPtr), out removed);
        Log("<---- EndDirectoryEnumerationCallback Ok");
        return Prj.S_OK;
    }

    private int OnGetDirEnum(IntPtr cbd, IntPtr enumIdPtr, IntPtr searchExprPtr, IntPtr dirBuf)
    {
        string filter = Prj.ReadPcwstr(searchExprPtr);
        if (filter == null) filter = string.Empty;
        Log("----> GetDirectoryEnumerationCallback filterFileName [" + filter + "]");

        Guid enumId = Prj.ReadGuid(enumIdPtr);
        EnumerationSession session;
        if (!_sessions.TryGetValue(enumId, out session))
        {
            Log("<---- GetDirectoryEnumerationCallback InternalError");
            return Prj.E_FAIL;
        }

        if ((Prj.CbdFlags(cbd) & Prj.FLAG_ENUM_RESTART_SCAN) != 0)
            session.Reset();

        int added = 0;
        DirEntry entry;
        while (session.TryGetNext(out entry))
        {
            if (!string.IsNullOrEmpty(filter) && !Prj.PrjFileNameMatch(entry.Name, filter))
                continue;

            Prj.FileBasicInfo info = new Prj.FileBasicInfo();
            info.IsDirectory    = entry.IsDirectory ? (byte)1 : (byte)0;
            info.FileSize       = entry.FileSize;
            info.CreationTime   = entry.CreationTimeFt;
            info.LastAccessTime = entry.LastAccessTimeFt;
            info.LastWriteTime  = entry.LastWriteTimeFt;
            info.ChangeTime     = entry.LastWriteTimeFt;
            info.FileAttributes = entry.FileAttributes;

            int hr = Prj.PrjFillDirEntryBuffer(entry.Name, ref info, dirBuf);
            if (hr == Prj.HR_INSUFFICIENT_BUFFER) { session.StepBack(); break; }
            if (hr != Prj.S_OK)
            {
                Log("<---- GetDirectoryEnumerationCallback " + Prj.Hr(hr));
                return hr;
            }
            added++;
        }

        Log("<---- GetDirectoryEnumerationCallback Ok [Added entries: " + added + "]");
        return Prj.S_OK;
    }

    private int OnGetPlaceholderInfo(IntPtr cbd)
    {
        string relativePath = Prj.CbdFilePath(cbd);
        if (relativePath == null) relativePath = string.Empty;
        string triggerProc = Prj.CbdTriggerProc(cbd);
        uint   triggerPid  = Prj.CbdTriggerPid(cbd);
        IntPtr virtCtx     = Prj.CbdVirtCtx(cbd);

        Log("----> GetPlaceholderInfoCallback [" + relativePath + "]");
        Log("  Placeholder creation triggered by [" + triggerProc + " " + triggerPid + "]");

        // Real source check (skipped in syntheticOnly mode)
        if (!_syntheticOnly)
        {
            FileSystemInfo realEntry = SourceEntry(FullSourcePath(relativePath));
            if (realEntry != null)
            {
                bool isDir = (realEntry.Attributes & FileAttributes.Directory) != 0;
                long size  = isDir ? 0L : ((FileInfo)realEntry).Length;
                byte[] info = Prj.BuildPlaceholderInfo(
                    isDir, size,
                    realEntry.CreationTime.ToFileTime(),
                    realEntry.LastAccessTime.ToFileTime(),
                    realEntry.LastWriteTime.ToFileTime(),
                    realEntry.LastWriteTime.ToFileTime(),
                    (uint)realEntry.Attributes);
                int realHr = Prj.PrjWritePlaceholderInfo(virtCtx, relativePath, info, (uint)info.Length);
                Log("<---- GetPlaceholderInfoCallback " + Prj.Hr(realHr));
                return realHr;
            }
        }

        // Synthetic entry
        if (_synthetic != null)
        {
            SyntheticEntry synth = _synthetic.Find(relativePath);
            if (synth != null)
            {
                long ft   = synth.GetFiletime();
                uint attr = synth.IsDirectory
                    ? (uint)FileAttributes.Directory
                    : (uint)FileAttributes.Normal;
                byte[] info = Prj.BuildPlaceholderInfo(
                    synth.IsDirectory, synth.FileSize,
                    ft, ft, ft, ft, attr);
                int hr = Prj.PrjWritePlaceholderInfo(virtCtx, relativePath, info, (uint)info.Length);
                Log("<---- GetPlaceholderInfoCallback " + Prj.Hr(hr) + " [synthetic]");
                return hr;
            }
        }

        Log("<---- GetPlaceholderInfoCallback FileNotFound");
        return Prj.HR_FILE_NOT_FOUND;
    }

    private int OnGetFileData(IntPtr cbd, ulong byteOffset, uint length)
    {
        string relativePath = Prj.CbdFilePath(cbd);
        if (relativePath == null) relativePath = string.Empty;
        Guid   dataStreamId = Prj.CbdDataStreamId(cbd);
        string triggerProc  = Prj.CbdTriggerProc(cbd);
        uint   triggerPid   = Prj.CbdTriggerPid(cbd);
        IntPtr virtCtx      = Prj.CbdVirtCtx(cbd);

        Log("----> GetFileDataCallback relativePath [" + relativePath + "]");
        Log("  triggered by [" + triggerProc + " " + triggerPid + "]");

        // Synthetic source (checked first; syntheticOnly never falls through to FS)
        if (_synthetic != null)
        {
            SyntheticEntry synth = _synthetic.Find(relativePath);
            if (synth != null)
                return ServeSyntheticContent(synth, virtCtx, dataStreamId, byteOffset, length);
        }

        if (_syntheticOnly)
        {
            Log("<---- return status FileNotFound [syntheticOnly, no entry]");
            return Prj.HR_FILE_NOT_FOUND;
        }

        // Real file
        string fullPath = FullSourcePath(relativePath);
        if (!File.Exists(fullPath))
        {
            Log("<---- return status FileNotFound");
            return Prj.HR_FILE_NOT_FOUND;
        }

        const uint ChunkSize = 65536;
        ulong writeOffset = byteOffset;
        ulong remaining   = length;

        try
        {
            FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                fs.Seek((long)byteOffset, SeekOrigin.Begin);
                while (remaining > 0)
                {
                    uint   toRead = (uint)Math.Min(remaining, (ulong)ChunkSize);
                    IntPtr buf    = Prj.PrjAllocateAlignedBuffer(virtCtx, new UIntPtr(toRead));
                    if (buf == IntPtr.Zero)
                    {
                        Log("<---- return status InternalError [alloc failed]");
                        return Prj.E_FAIL;
                    }
                    try
                    {
                        byte[] tmp  = new byte[toRead];
                        int    read = fs.Read(tmp, 0, (int)toRead);
                        if (read == 0) break;
                        Marshal.Copy(tmp, 0, buf, read);
                        int hr = Prj.PrjWriteFileData(virtCtx, ref dataStreamId, buf, writeOffset, (uint)read);
                        if (hr != Prj.S_OK) { Log("<---- return status " + Prj.Hr(hr)); return hr; }
                        writeOffset += (ulong)read;
                        remaining   -= (ulong)read;
                    }
                    finally { Prj.PrjFreeAlignedBuffer(buf); }
                }
            }
            finally { fs.Dispose(); }
        }
        catch (Exception ex)
        {
            Log("<---- return status InternalError [" + ex.Message + "]");
            return Prj.E_FAIL;
        }

        Log("<---- return status Ok");
        return Prj.S_OK;
    }

    private int ServeSyntheticContent(
        SyntheticEntry synth, IntPtr virtCtx, Guid dataStreamId,
        ulong byteOffset, uint length)
    {
        byte[] content    = SyntheticContent.Generate(synth.Name, synth.FileSize);
        ulong  contentLen = (ulong)content.Length;

        if (byteOffset >= contentLen)
        {
            Log("<---- return status Ok [synthetic, past eof]");
            return Prj.S_OK;
        }

        ulong endOffset = byteOffset + (ulong)length;
        if (endOffset > contentLen) endOffset = contentLen;
        uint toWrite = (uint)(endOffset - byteOffset);

        IntPtr buf = Prj.PrjAllocateAlignedBuffer(virtCtx, new UIntPtr(toWrite));
        if (buf == IntPtr.Zero)
        {
            Log("<---- return status InternalError [synthetic alloc failed]");
            return Prj.E_FAIL;
        }
        try
        {
            Marshal.Copy(content, (int)byteOffset, buf, (int)toWrite);
            int hr = Prj.PrjWriteFileData(virtCtx, ref dataStreamId, buf, byteOffset, toWrite);
            Log("<---- return status " + Prj.Hr(hr) + " [synthetic]");
            return hr;
        }
        finally { Prj.PrjFreeAlignedBuffer(buf); }
    }

    // ---- Helpers ----

    private string FullSourcePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return _sourceRoot;
        return Path.Combine(_sourceRoot, relativePath);
    }

    private static FileSystemInfo SourceEntry(string fullPath)
    {
        if (File.Exists(fullPath))      return new FileInfo(fullPath);
        if (Directory.Exists(fullPath)) return new DirectoryInfo(fullPath);
        return null;
    }

    private static DirEntry RealToDirEntry(FileSystemInfo info)
    {
        DirEntry e = new DirEntry();
        e.Name             = info.Name;
        e.IsSynthetic      = false;
        e.IsDirectory      = (info.Attributes & FileAttributes.Directory) != 0;
        e.FileSize         = e.IsDirectory ? 0L : ((FileInfo)info).Length;
        e.CreationTimeFt   = info.CreationTime.ToFileTime();
        e.LastAccessTimeFt = info.LastAccessTime.ToFileTime();
        e.LastWriteTimeFt  = info.LastWriteTime.ToFileTime();
        e.FileAttributes   = (uint)info.Attributes;
        return e;
    }

    private static DirEntry SyntheticToDirEntry(SyntheticEntry s)
    {
        long ft = s.GetFiletime();
        DirEntry e = new DirEntry();
        e.Name             = s.Name;
        e.IsSynthetic      = true;
        e.IsDirectory      = s.IsDirectory;
        e.FileSize         = s.FileSize;
        e.CreationTimeFt   = ft;
        e.LastAccessTimeFt = ft;
        e.LastWriteTimeFt  = ft;
        e.FileAttributes   = s.IsDirectory
            ? (uint)FileAttributes.Directory
            : (uint)FileAttributes.Normal;
        return e;
    }

    private static bool NameExists(List<DirEntry> list, string name)
    {
        foreach (DirEntry e in list)
        {
            if (string.Compare(e.Name, name, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
        }
        return false;
    }

    private static void Log(string msg)
    {
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + " INF] " + msg);
    }

    // ---- EnumerationSession: per-session cursor over a sorted DirEntry[] ----

    private sealed class EnumerationSession
    {
        private readonly DirEntry[] _entries;
        private int _index;

        public EnumerationSession(DirEntry[] entries)
        {
            _entries = entries;
            _index   = 0;
        }

        public void Reset()   { _index = 0; }

        public void StepBack() { if (_index > 0) _index--; }

        public bool TryGetNext(out DirEntry entry)
        {
            if (_index < _entries.Length)
            {
                entry = _entries[_index++];
                return true;
            }
            entry = default(DirEntry);
            return false;
        }
    }
}
