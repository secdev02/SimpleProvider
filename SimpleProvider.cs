// SimpleProvider.cs
// Self-contained ProjFS provider.  All P/Invoke declarations are embedded.
// No NuGet, no .csproj, no /r: references needed at compile time.
//
// PREREQUISITES
//   Windows 10 v1809+ with ProjFS enabled (once, elevated PowerShell):
//     Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart
//
// COMPILE  (ProjectedFSLib.dll is a Windows inbox DLL; loaded at runtime via P/Invoke)
//   csc.exe /platform:x64 /out:SimpleProvider.exe SimpleProvider.cs
//
// RUN  (Administrator required - ProjFS needs elevation)
//   SimpleProvider.exe --sourceroot C:\MySource --virtroot C:\MyVirt
//
// NOTE: written in C# 5 syntax so it compiles with the .NET Framework 4.x
//       csc.exe that ships in C:\Windows\Microsoft.NET\Framework64\v4.0.30319\.
//       No expression-bodied members, no discards, no nullable directives.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

// =============================================================================
// P/Invoke layer for ProjectedFSLib.dll
// =============================================================================
internal static class Prj
{
    // -------------------------------------------------------------------------
    // HRESULT constants
    // -------------------------------------------------------------------------
    public const int S_OK                   = 0;
    public const int E_FAIL                 = unchecked((int)0x80004005);
    public const int HR_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A); // HRESULT_FROM_WIN32(122)
    public const int HR_FILE_NOT_FOUND      = unchecked((int)0x80070002); // HRESULT_FROM_WIN32(2)
    public const int HR_PATH_NOT_FOUND      = unchecked((int)0x80070003); // HRESULT_FROM_WIN32(3)

    // PRJ_CALLBACK_DATA_FLAGS
    public const int FLAG_ENUM_RESTART_SCAN        = 0x00000001;
    public const int FLAG_ENUM_RETURN_SINGLE_ENTRY = 0x00000002;

    // -------------------------------------------------------------------------
    // Callback delegate types
    // -------------------------------------------------------------------------
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int StartDirEnumCb(IntPtr cbd, IntPtr enumId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int EndDirEnumCb(IntPtr cbd, IntPtr enumId);

    // searchExpr  = PCWSTR (raw pointer to wide string, or IntPtr.Zero)
    // dirEntryBuf = PRJ_DIR_ENTRY_BUFFER_HANDLE (opaque handle)
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetDirEnumCb(IntPtr cbd, IntPtr enumId, IntPtr searchExpr, IntPtr dirEntryBuf);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetPlaceholderInfoCb(IntPtr cbd);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int GetFileDataCb(IntPtr cbd, ulong byteOffset, uint length);

    // -------------------------------------------------------------------------
    // PRJ_CALLBACKS - eight function-pointer slots
    // -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct Callbacks
    {
        public IntPtr StartDirEnum;
        public IntPtr EndDirEnum;
        public IntPtr GetDirEnum;
        public IntPtr GetPlaceholderInfo;
        public IntPtr GetFileData;
        public IntPtr QueryFileName;    // optional - leave zero
        public IntPtr Notification;     // optional - leave zero
        public IntPtr CancelCommand;    // optional - leave zero
    }

    // -------------------------------------------------------------------------
    // PRJ_FILE_BASIC_INFO  (56 bytes, explicit MSVC x64 layout)
    //
    //  offset  size  field
    //    0       1   BOOLEAN IsDirectory
    //    1       7   (padding to align next INT64 to 8 bytes)
    //    8       8   INT64   FileSize
    //   16       8   INT64   CreationTime   (100-ns ticks since 1601-01-01)
    //   24       8   INT64   LastAccessTime
    //   32       8   INT64   LastWriteTime
    //   40       8   INT64   ChangeTime
    //   48       4   UINT32  FileAttributes
    //   52       4   (padding to keep struct size a multiple of 8)
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // Native function declarations
    // -------------------------------------------------------------------------

    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjStartVirtualizing(
        string        rootPath,
        ref Callbacks callbacks,
        IntPtr        instanceContext,  // pass IntPtr.Zero
        IntPtr        options,          // PRJ_STARTVIRTUALIZING_OPTIONS* - IntPtr.Zero for defaults
        out IntPtr    virtualizationContext);

    [DllImport("ProjectedFSLib.dll")]
    public static extern void PrjStopVirtualizing(IntPtr virtCtx);

    // placeholderInfo is a PRJ_PLACEHOLDER_INFO* marshalled as a byte[].
    // destinationFileName is the path relative to the virtualization root.
    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjWritePlaceholderInfo(
        IntPtr       virtCtx,
        string       destinationFileName,
        [In] byte[]  placeholderInfo,
        uint         placeholderInfoSize);

    // buffer must have been allocated by PrjAllocateAlignedBuffer.
    [DllImport("ProjectedFSLib.dll")]
    public static extern int PrjWriteFileData(
        IntPtr    virtCtx,
        ref Guid  dataStreamId,
        IntPtr    buffer,
        ulong     byteOffset,
        uint      length);

    [DllImport("ProjectedFSLib.dll")]
    public static extern IntPtr PrjAllocateAlignedBuffer(IntPtr virtCtx, UIntPtr size);

    [DllImport("ProjectedFSLib.dll")]
    public static extern void PrjFreeAlignedBuffer(IntPtr buffer);

    // Returns S_OK on success, HR_INSUFFICIENT_BUFFER when the result buffer is full.
    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjFillDirEntryBuffer(
        string           fileName,
        ref FileBasicInfo fileBasicInfo,
        IntPtr           dirEntryBufferHandle);

    // Returns true if fileNameToCheck matches pattern (wildcards * and ?).
    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrjFileNameMatch(string fileNameToCheck, string pattern);

    // -------------------------------------------------------------------------
    // PRJ_CALLBACK_DATA field readers  (x64 field offsets)
    //
    //  offset  type    field
    //    0     UINT32  Size
    //    4     UINT32  Flags                           <- restartScan is bit 0
    //    8     HANDLE  NamespaceVirtualizationContext  <- pass to PrjWrite* functions
    //   16     INT32   CommandId
    //   20     GUID    FileId
    //   36     GUID    DataStreamId                    <- pass to PrjWriteFileData
    //   52     (4 bytes padding to 8-byte boundary)
    //   56     PCWSTR  FilePathName
    //   64     void*   VersionInfo
    //   72     UINT32  TriggeringProcessId
    //   76     (4 bytes padding)
    //   80     PCWSTR  TriggeringProcessImageFileName
    //   88     void*   InstanceContext
    // -------------------------------------------------------------------------
    public static int CbdFlags(IntPtr cbd)
    {
        return Marshal.ReadInt32(cbd, 4);
    }

    public static IntPtr CbdVirtCtx(IntPtr cbd)
    {
        return Marshal.ReadIntPtr(cbd, 8);
    }

    public static string CbdFilePath(IntPtr cbd)
    {
        return PcwstrAt(cbd, 56);
    }

    public static uint CbdTriggerPid(IntPtr cbd)
    {
        return (uint)Marshal.ReadInt32(cbd, 72);
    }

    public static string CbdTriggerProc(IntPtr cbd)
    {
        return PcwstrAt(cbd, 80);
    }

    public static Guid CbdDataStreamId(IntPtr cbd)
    {
        return (Guid)Marshal.PtrToStructure(IntPtr.Add(cbd, 36), typeof(Guid));
    }

    // Read the GUID value that the given pointer points to.
    public static Guid ReadGuid(IntPtr ptr)
    {
        return (Guid)Marshal.PtrToStructure(ptr, typeof(Guid));
    }

    // Read a PCWSTR from a raw pointer value (not an offset into CBD).
    public static string ReadPcwstr(IntPtr p)
    {
        if (p == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUni(p);
    }

    private static string PcwstrAt(IntPtr cbd, int offset)
    {
        IntPtr p = Marshal.ReadIntPtr(cbd, offset);
        if (p == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUni(p);
    }

    // -------------------------------------------------------------------------
    // Build PRJ_PLACEHOLDER_INFO as a managed byte[] (344 bytes)
    //
    // Layout:
    //    0 –  55 : PRJ_FILE_BASIC_INFO (56 bytes)
    //   56 –  63 : EaInformation       (8 bytes, zeroed - no extended attributes)
    //   64 –  71 : SecurityInformation (8 bytes, zeroed)
    //   72 –  79 : StreamsInformation  (8 bytes, zeroed)
    //   80 – 207 : VersionInfo.ProviderID (128 bytes, zeroed = token {0})
    //  208 – 335 : VersionInfo.ContentID  (128 bytes, zeroed = token {0})
    //  336        : VariableData[0]
    //  337 – 343 : padding to 344 (8-byte aligned due to INT64 in FileBasicInfo)
    // -------------------------------------------------------------------------
    public static byte[] BuildPlaceholderInfo(
        bool   isDirectory,
        long   fileSize,
        long   creationTime,
        long   lastAccessTime,
        long   lastWriteTime,
        long   changeTime,
        uint   fileAttributes)
    {
        byte[] buf = new byte[344];
        buf[0] = isDirectory ? (byte)1 : (byte)0;
        Wi64(buf,  8, isDirectory ? 0L : fileSize);
        Wi64(buf, 16, creationTime);
        Wi64(buf, 24, lastAccessTime);
        Wi64(buf, 32, lastWriteTime);
        Wi64(buf, 40, changeTime);
        Wu32(buf, 48, fileAttributes);
        // All other bytes (EaInfo, SecurityInfo, StreamsInfo, VersionInfo) default to zero.
        return buf;
    }

    private static void Wi64(byte[] b, int o, long v)
    {
        ulong u = (ulong)v;
        for (int i = 0; i < 8; i++)
            b[o + i] = (byte)(u >> (i * 8));
    }

    private static void Wu32(byte[] b, int o, uint v)
    {
        for (int i = 0; i < 4; i++)
            b[o + i] = (byte)(v >> (i * 8));
    }

    // -------------------------------------------------------------------------
    // HRESULT display helper
    // -------------------------------------------------------------------------
    public static string Hr(int hr)
    {
        switch (hr)
        {
            case S_OK:                   return "Ok";
            case HR_FILE_NOT_FOUND:      return "FileNotFound";
            case HR_PATH_NOT_FOUND:      return "PathNotFound";
            case HR_INSUFFICIENT_BUFFER: return "InsufficientBuffer";
            case E_FAIL:                 return "InternalError";
            default:                     return "0x" + ((uint)hr).ToString("X8");
        }
    }
}

// =============================================================================
// Entry point
// =============================================================================
internal static class Program
{
    private static int Main(string[] args)
    {
        string sourceRoot = null;
        string virtRoot   = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--sourceroot", StringComparison.OrdinalIgnoreCase))
                sourceRoot = args[i + 1];
            else if (args[i].Equals("--virtroot", StringComparison.OrdinalIgnoreCase))
                virtRoot = args[i + 1];
        }

        if (sourceRoot == null || virtRoot == null)
        {
            Console.WriteLine("Usage: SimpleProvider.exe --sourceroot <path> --virtroot <path>");
            Console.WriteLine();
            Console.WriteLine("Prerequisites:");
            Console.WriteLine("  Run as Administrator.");
            Console.WriteLine("  Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart");
            return 1;
        }

        SimpleProvider provider;
        try
        {
            provider = new SimpleProvider(sourceRoot, virtRoot);
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
            Console.Error.WriteLine("Ensure Client-ProjFS is enabled and this process is elevated.");
            return 1;
        }

        Console.WriteLine("Provider running.");
        Console.WriteLine("  Source : " + Path.GetFullPath(sourceRoot));
        Console.WriteLine("  Virt   : " + Path.GetFullPath(virtRoot));
        Console.WriteLine("Press ENTER to stop...");
        Console.ReadLine();

        provider.StopVirtualizing();
        Console.WriteLine("Stopped.");
        return 0;
    }
}

// =============================================================================
// ProjFS provider
// =============================================================================
internal sealed class SimpleProvider
{
    // ---- State ----

    private readonly string _sourceRoot;
    private readonly string _virtRoot;
    private IntPtr _virtCtx;   // PRJ_NAMESPACE_VIRTUALIZATION_CONTEXT

    // Active enumeration sessions keyed by enumerationId.
    // ConcurrentDictionary because ProjFS dispatches callbacks on a thread pool.
    private readonly ConcurrentDictionary<Guid, EnumerationSession> _sessions
        = new ConcurrentDictionary<Guid, EnumerationSession>();

    // Static back-reference so the static thunks can reach this instance.
    // A showcase supports one provider at a time.
    private static SimpleProvider _current;

    // Delegate instances stored as fields.
    // IMPORTANT: Marshal.GetFunctionPointerForDelegate does NOT root the delegate.
    // The GC may collect it if the only reference is the native function pointer.
    // Keeping them as fields ensures they remain alive for the provider's lifetime.
    private readonly Prj.StartDirEnumCb       _cbStartDirEnum;
    private readonly Prj.EndDirEnumCb         _cbEndDirEnum;
    private readonly Prj.GetDirEnumCb         _cbGetDirEnum;
    private readonly Prj.GetPlaceholderInfoCb _cbGetPlaceholderInfo;
    private readonly Prj.GetFileDataCb        _cbGetFileData;

    // ---- Construction ----

    public SimpleProvider(string sourceRoot, string virtRoot)
    {
        if (!Directory.Exists(sourceRoot))
            throw new ArgumentException("Source root does not exist: " + sourceRoot);

        _sourceRoot = Path.GetFullPath(sourceRoot);
        _virtRoot   = Path.GetFullPath(virtRoot);
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
        if (_virtCtx == IntPtr.Zero)
            return;
        Prj.PrjStopVirtualizing(_virtCtx);
        _virtCtx = IntPtr.Zero;
    }

    // ---- Static callback thunks ----
    // These are the raw native callbacks.  Each wraps its call in a try/catch so
    // no managed exception propagates across the native/managed boundary.

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

    private static int GetDirEnumThunk(IntPtr cbd, IntPtr enumId, IntPtr searchExpr, IntPtr dirBuf)
    {
        try   { return _current.OnGetDirEnum(cbd, enumId, searchExpr, dirBuf); }
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

        string fullPath = FullSourcePath(relativePath);
        if (!Directory.Exists(fullPath))
        {
            Log("<---- StartDirectoryEnumerationCallback FileNotFound");
            return Prj.HR_FILE_NOT_FOUND;
        }

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(fullPath).GetFileSystemInfos();
        }
        catch (Exception ex)
        {
            Log("<---- StartDirectoryEnumerationCallback InternalError [" + ex.Message + "]");
            return Prj.E_FAIL;
        }

        // ProjFS requires entries returned in case-insensitive ordinal order.
        Array.Sort(entries, delegate(FileSystemInfo a, FileSystemInfo b)
        {
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        _sessions[Prj.ReadGuid(enumIdPtr)] = new EnumerationSession(entries);

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

        // Bit 0 of Flags == PRJ_CB_DATA_FLAG_ENUM_RESTART_SCAN
        if ((Prj.CbdFlags(cbd) & Prj.FLAG_ENUM_RESTART_SCAN) != 0)
            session.Reset();

        int added = 0;

        FileSystemInfo entry;
        while (session.TryGetNext(out entry))
        {
            if (!string.IsNullOrEmpty(filter) && !Prj.PrjFileNameMatch(entry.Name, filter))
                continue;

            bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;

            Prj.FileBasicInfo info = new Prj.FileBasicInfo();
            info.IsDirectory    = isDir ? (byte)1 : (byte)0;
            info.FileSize       = isDir ? 0L : ((FileInfo)entry).Length;
            info.CreationTime   = entry.CreationTime.ToFileTime();
            info.LastAccessTime = entry.LastAccessTime.ToFileTime();
            info.LastWriteTime  = entry.LastWriteTime.ToFileTime();
            info.ChangeTime     = entry.LastWriteTime.ToFileTime();
            info.FileAttributes = (uint)entry.Attributes;

            int hr = Prj.PrjFillDirEntryBuffer(entry.Name, ref info, dirBuf);

            if (hr == Prj.HR_INSUFFICIENT_BUFFER)
            {
                // Buffer full - step cursor back so this entry leads the next call.
                session.StepBack();
                break;
            }

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

        string fullPath = FullSourcePath(relativePath);
        FileSystemInfo entry = SourceEntry(fullPath);
        if (entry == null)
        {
            Log("<---- GetPlaceholderInfoCallback FileNotFound");
            return Prj.HR_FILE_NOT_FOUND;
        }

        bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
        long size  = isDir ? 0L : ((FileInfo)entry).Length;

        byte[] info = Prj.BuildPlaceholderInfo(
            isDir,
            size,
            entry.CreationTime.ToFileTime(),
            entry.LastAccessTime.ToFileTime(),
            entry.LastWriteTime.ToFileTime(),
            entry.LastWriteTime.ToFileTime(),
            (uint)entry.Attributes);

        int hr = Prj.PrjWritePlaceholderInfo(virtCtx, relativePath, info, (uint)info.Length);
        Log("<---- GetPlaceholderInfoCallback " + Prj.Hr(hr));
        return hr;
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

        string fullPath = FullSourcePath(relativePath);
        if (!File.Exists(fullPath))
        {
            Log("<---- return status FileNotFound");
            return Prj.HR_FILE_NOT_FOUND;
        }

        // ProjFS tells us exactly which byte range to fill.
        // Stream the source file back in 64 KB chunks using PrjAllocateAlignedBuffer
        // so the buffer meets the sector-alignment requirement of PrjWriteFileData.
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
                        Log("<---- return status InternalError [PrjAllocateAlignedBuffer returned null]");
                        return Prj.E_FAIL;
                    }

                    try
                    {
                        byte[] tmp  = new byte[toRead];
                        int    read = fs.Read(tmp, 0, (int)toRead);
                        if (read == 0)
                            break;

                        // Copy managed bytes into the sector-aligned native buffer.
                        Marshal.Copy(tmp, 0, buf, read);

                        int hr = Prj.PrjWriteFileData(virtCtx, ref dataStreamId, buf, writeOffset, (uint)read);
                        if (hr != Prj.S_OK)
                        {
                            Log("<---- return status " + Prj.Hr(hr));
                            return hr;
                        }

                        writeOffset += (ulong)read;
                        remaining   -= (ulong)read;
                    }
                    finally
                    {
                        Prj.PrjFreeAlignedBuffer(buf);
                    }
                }
            }
            finally
            {
                fs.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log("<---- return status InternalError [" + ex.Message + "]");
            return Prj.E_FAIL;
        }

        Log("<---- return status Ok");
        return Prj.S_OK;
    }

    // ---- Helpers ----

    private string FullSourcePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return _sourceRoot;
        return Path.Combine(_sourceRoot, relativePath);
    }

    private static FileSystemInfo SourceEntry(string fullPath)
    {
        if (File.Exists(fullPath))
            return new FileInfo(fullPath);
        if (Directory.Exists(fullPath))
            return new DirectoryInfo(fullPath);
        return null;
    }

    private static void Log(string msg)
    {
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + " INF] " + msg);
    }

    // ---- EnumerationSession - per-session directory listing cursor ----
    //
    // ProjFS may call GetDirectoryEnumeration multiple times for the same session
    // (when the kernel-side buffer fills mid-listing). We remember where we left off.

    private sealed class EnumerationSession
    {
        private readonly FileSystemInfo[] _entries;
        private int _index;

        public EnumerationSession(FileSystemInfo[] entries)
        {
            _entries = entries;
            _index   = 0;
        }

        public void Reset()
        {
            _index = 0;
        }

        // Undoes the last TryGetNext advance when PrjFillDirEntryBuffer returns
        // HR_INSUFFICIENT_BUFFER so that entry leads the next call.
        public void StepBack()
        {
            if (_index > 0)
                _index--;
        }

        public bool TryGetNext(out FileSystemInfo entry)
        {
            if (_index < _entries.Length)
            {
                entry = _entries[_index++];
                return true;
            }
            entry = null;
            return false;
        }
    }
}
