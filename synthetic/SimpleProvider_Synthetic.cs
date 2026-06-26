// SimpleProvider.cs
// Self-contained ProjFS provider.  All P/Invoke declarations are embedded.
// No NuGet, no .csproj, no /r: references needed at compile time.
//
// PREREQUISITES
//   Windows 10 v1809+ with ProjFS enabled (once, elevated PowerShell):
//     Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart
//
// COMPILE
//   csc.exe /platform:x64 /out:SimpleProvider.exe SimpleProvider.cs
//
// RUN  (Administrator required)
//   -- Real files only:
//   SimpleProvider.exe --sourceroot C:\MySource --virtroot C:\MyVirt
//
//   -- Mix real + synthetic:
//   SimpleProvider.exe --sourceroot C:\MySource --virtroot C:\MyVirt --syntheticdata C:\entries.csv
//
// SYNTHETIC DATA CSV FORMAT  (one entry per line, # = comment, blank lines ignored)
//   \Path\To\Entry,isDirectory,fileSize,unixTimestamp
//   \AWS,true,0,1743942586
//   \AWS\credentials,false,116,1741508986
//
// NOTE: Written in C# 5 syntax for compatibility with the .NET Framework 4.x
//       csc.exe at C:\Windows\Microsoft.NET\Framework64\v4.0.30319\.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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

    // PRJ_FILE_BASIC_INFO - explicit MSVC x64 layout (56 bytes)
    //   offset  0: BOOLEAN IsDirectory (1 byte + 7 bytes padding)
    //   offset  8: INT64 FileSize
    //   offset 16: INT64 CreationTime (100-ns ticks since 1601-01-01)
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

    // Must be called once on the virtroot directory before PrjStartVirtualizing.
    // Pass null for targetPathName to mark rootPathName itself as the virt root.
    // versionInfo may be IntPtr.Zero; virtualizationInstanceID identifies this
    // provider instance and is embedded in the NTFS reparse point.
    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjMarkDirectoryAsPlaceholder(
        string   rootPathName,
        string   targetPathName,
        IntPtr   versionInfo,
        ref Guid virtualizationInstanceID);

    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    public static extern int PrjFillDirEntryBuffer(
        string            fileName,
        ref FileBasicInfo fileBasicInfo,
        IntPtr            dirEntryBufferHandle);

    [DllImport("ProjectedFSLib.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrjFileNameMatch(string fileNameToCheck, string pattern);

    // PRJ_CALLBACK_DATA field offsets (x64):
    //   4  UINT32 Flags  |  8 HANDLE VirtCtx  |  36 GUID DataStreamId
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

    // PRJ_PLACEHOLDER_INFO as byte[] (344 bytes)
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

    // HRESULT_FROM_WIN32(ERROR_NOT_A_REPARSE_POINT) – returned by PrjStartVirtualizing
    // when the virtroot has not been stamped with PrjMarkDirectoryAsPlaceholder yet,
    // or when stale placeholder files from a previous session remain in the directory.
    public const int HR_NOT_A_REPARSE_POINT = unchecked((int)0x80071126);

    public static string Hr(int hr)
    {
        switch (hr)
        {
            case S_OK:                   return "Ok";
            case HR_FILE_NOT_FOUND:      return "FileNotFound";
            case HR_PATH_NOT_FOUND:      return "PathNotFound";
            case HR_INSUFFICIENT_BUFFER: return "InsufficientBuffer";
            case HR_NOT_A_REPARSE_POINT: return "NotAReparsePoint";
            case E_FAIL:                 return "InternalError";
            default:                     return "0x" + ((uint)hr).ToString("X8");
        }
    }
}

// =============================================================================
// Synthetic virtual file system - loaded from a CSV
// =============================================================================

// One entry from the CSV.  RelativePath uses backslash separators and has NO
// leading backslash: "AWS\credentials", not "\AWS\credentials".
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

// Loads and indexes a CSV of synthetic entries.
internal sealed class SyntheticData
{
    // Keyed by RelativePath (case-insensitive).
    private readonly Dictionary<string, SyntheticEntry> _byPath;
    // Keyed by ParentPath (case-insensitive).  Root-level entries have key "".
    private readonly Dictionary<string, List<SyntheticEntry>> _byParent;

    private SyntheticData()
    {
        _byPath   = new Dictionary<string, SyntheticEntry>(StringComparer.OrdinalIgnoreCase);
        _byParent = new Dictionary<string, List<SyntheticEntry>>(StringComparer.OrdinalIgnoreCase);
    }

    // Parses the CSV file.  Each data line: \Path,isDirectory,fileSize,unixTimestamp
    public static SyntheticData Load(string csvPath)
    {
        SyntheticData data = new SyntheticData();
        string[] lines = File.ReadAllLines(csvPath);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;

            string[] parts = trimmed.Split(',');
            if (parts.Length < 4)
                continue;

            bool isDir;
            long fileSize;
            long unixTs;

            if (!bool.TryParse(parts[1].Trim(), out isDir))   continue;
            if (!long.TryParse(parts[2].Trim(), out fileSize)) continue;
            if (!long.TryParse(parts[3].Trim(), out unixTs))   continue;

            // Normalise path: strip leading backslash, keep internal backslashes
            string rawPath    = parts[0].Trim();
            string normalPath = rawPath.TrimStart('\\');

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

    // Returns the entry for a given relative path, or null.
    public SyntheticEntry Find(string relativePath)
    {
        if (relativePath == null) relativePath = string.Empty;
        SyntheticEntry entry;
        if (_byPath.TryGetValue(relativePath, out entry))
            return entry;
        return null;
    }

    // Returns all direct children of a parent directory path.
    // Pass "" for the root level.  Never returns null.
    public List<SyntheticEntry> GetChildren(string parentRelativePath)
    {
        if (parentRelativePath == null) parentRelativePath = string.Empty;
        List<SyntheticEntry> result;
        if (_byParent.TryGetValue(parentRelativePath, out result))
            return result;
        return new List<SyntheticEntry>();
    }
}

// Generates plausible-looking file content for synthetic entries.
// Content is trimmed or padded with newlines to match the declared file size.
internal static class SyntheticContent
{
    public static byte[] Generate(string fileName, long declaredSize)
    {
        string lower = fileName.ToLowerInvariant();
        byte[] raw   = Encoding.UTF8.GetBytes(PickTemplate(lower, declaredSize));
        return FitToSize(raw, declaredSize);
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

    private static string PickTemplate(string lower, long size)
    {
        switch (lower)
        {
            case "credentials":           return TplAwsCredentials;
            case "config":                return TplAwsConfig;
            case "iam_access_keys.csv":   return TplIamKeyCsv;
            case "s3_bucket_policy.json": return TplS3Policy;
            case "id_rsa":                return PemBlock("RSA PRIVATE KEY", size);
            case "id_rsa.pub":            return TplRsaPub;
            case "id_ed25519":            return PemBlock("OPENSSH PRIVATE KEY", size);
            case "id_ed25519.pub":        return TplEd25519Pub;
            case "authorized_keys":       return TplAuthorizedKeys;
            case "deploy_key.pem":        return PemBlock("RSA PRIVATE KEY", size);
            case "api_keys.json":         return TplApiKeys;
            case "github_pat.txt":        return TplGithubPat;
            case "stripe_keys.txt":       return TplStripeKeys;
        }

        string ext = Path.GetExtension(lower);
        if (ext == ".pem")  return PemBlock("PRIVATE KEY", size);
        if (ext == ".json") return TplGenericJson;
        if (ext == ".csv")  return TplGenericCsv;
        return "# " + lower + "\n# Synthetic file generated by SimpleProvider\n";
    }

    // Builds a PEM block whose base64 body is sized to fill approximately
    // targetSize bytes total, then FitToSize trims/pads to exact length.
    private static string PemBlock(string label, long targetSize)
    {
        string header  = "-----BEGIN " + label + "-----\n";
        string footer  = "\n-----END " + label + "-----\n";
        int bodyTarget = (int)targetSize - header.Length - footer.Length;
        if (bodyTarget < 64) bodyTarget = 64;
        return header + FakeBase64(bodyTarget) + footer;
    }

    // Deterministic LCG-based fake base64 (same output for same approxLen).
    private static string FakeBase64(int approxLen)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
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

    // ---- Content templates ----

    private const string TplAwsCredentials =
        "[default]\n" +
        "aws_access_key_id = AKIAIOSFODNN7EXAMPLE\n" +
        "aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY\n";

    private const string TplAwsConfig =
        "[default]\n" +
        "region = us-east-1\n" +
        "output = json\n" +
        "\n" +
        "[profile staging]\n" +
        "region = us-west-2\n" +
        "output = text\n" +
        "\n" +
        "[profile prod]\n" +
        "region = eu-west-1\n" +
        "output = json\n";

    private const string TplIamKeyCsv =
        "User Name,Access key ID,Secret access key,Status,Created\n" +
        "admin,AKIAIOSFODNN7EXAMPLE,wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY,Active,2024-01-15\n" +
        "deploy,AKIAI44QH8DHBEXAMPLE,je7MtGbClwBF/2Zp9Utk/h3yCo8nvbEXAMPLEKEY,Active,2024-03-01\n";

    private const string TplS3Policy =
        "{\n" +
        "    \"Version\": \"2012-10-17\",\n" +
        "    \"Statement\": [\n" +
        "        {\n" +
        "            \"Sid\": \"AllowRootAndAdminAccess\",\n" +
        "            \"Effect\": \"Allow\",\n" +
        "            \"Principal\": {\n" +
        "                \"AWS\": [\n" +
        "                    \"arn:aws:iam::123456789012:root\",\n" +
        "                    \"arn:aws:iam::123456789012:user/admin\"\n" +
        "                ]\n" +
        "            },\n" +
        "            \"Action\": \"s3:*\",\n" +
        "            \"Resource\": [\n" +
        "                \"arn:aws:s3:::example-company-backups\",\n" +
        "                \"arn:aws:s3:::example-company-backups/*\"\n" +
        "            ]\n" +
        "        },\n" +
        "        {\n" +
        "            \"Sid\": \"DenyUnencryptedObjectUploads\",\n" +
        "            \"Effect\": \"Deny\",\n" +
        "            \"Principal\": \"*\",\n" +
        "            \"Action\": \"s3:PutObject\",\n" +
        "            \"Resource\": \"arn:aws:s3:::example-company-backups/*\",\n" +
        "            \"Condition\": {\n" +
        "                \"StringNotEquals\": {\n" +
        "                    \"s3:x-amz-server-side-encryption\": \"AES256\"\n" +
        "                }\n" +
        "            }\n" +
        "        },\n" +
        "        {\n" +
        "            \"Sid\": \"AllowCrossAccountRead\",\n" +
        "            \"Effect\": \"Allow\",\n" +
        "            \"Principal\": {\n" +
        "                \"AWS\": \"arn:aws:iam::987654321098:role/ReadOnlyRole\"\n" +
        "            },\n" +
        "            \"Action\": [\"s3:GetObject\", \"s3:ListBucket\"],\n" +
        "            \"Resource\": [\n" +
        "                \"arn:aws:s3:::example-company-backups\",\n" +
        "                \"arn:aws:s3:::example-company-backups/*\"\n" +
        "            ]\n" +
        "        }\n" +
        "    ]\n" +
        "}\n";

    private const string TplRsaPub =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC7e1bXAklV8kGvFLkv0NCH" +
        "nfRakXS3w2fBWHCpWJCjVp6eLI3YJFTvX9z5JIFAYTmXW6r7M0cHZNrKs8D" +
        "CFx9mTCH6bQp1YJUvC3m8Xz4vNFn5T7b9HhXFCp3w1JKEjgQkTb5LMD2Jf9" +
        "Rn8bVz7e4wHpBh7kMNqKREz5v0cXn8fHwm3yGrKJ6TQpLHMXv8nJF7aExFd9" +
        "bTK4bVg5gQLJnExDYfmTRGkVsWP4bK2nJlpRZ7w+3bGmFhLq9sXvNtPcDe4A" +
        "kMn2oYrWjHcBpTuZsVKlIeQdFxOyRmAgEbJ5 admin@prod-server\n";

    private const string TplEd25519Pub =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGpTX9kMqzR7vE4pL" +
        "hNbDgCm2wH3FjKsP0uYxRnVt8Qo user@example-host\n";

    private const string TplAuthorizedKeys =
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC7e1bXAklV8kGvFLkv0NCH" +
        "nfRakXS3w2fBWHCpWJCjVp6eLI3YJFTvX9z5JIFAYTmXW6r7M0cHZNrKs8D" +
        "CFx9mTCH6bQp1YJUvC3m8Xz4vNFn5T7b9HhXFCp3w1JKEjgQkTb5LMD2Jf9" +
        "kMn2oYrWjHcBpTuZsVKlIe admin@prod-server\n" +
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQDm3pK7LrXnV5tBs8WqNzJ9" +
        "PeYc4FhOkM2iVgRdCbHlTsUwXnAJ6mDpY8FjLqKvGzEo7tN1cWrXsMbVaHp" +
        "eFd3KcR5nJW2oLhTgI8mBpD4qNsYvXkZcFwO6eA9jUxRnKHlGmP2oQrTsEb" +
        "VpZsWKlIeQdFxOyRmAgEbJ5 deploy@ci-runner\n" +
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGpTX9kMqzR7vE4pL" +
        "hNbDgCm2wH3FjKsP0uYxRnVt8Qo backup@monitoring\n" +
        "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC5f2cYBmkW6tHuX3sAr7Vn" +
        "OfZd5GiPlN3jUhScDaIkEsRwYoAL8nEqX6FkMpL7vJhDo5tO2bXrTmCwAIq" +
        "eFe4LdS6oKJ3mBuEhH9nQsPwJ7vAkN4bRxOzSmFhMp8tV developer@workstation\n";

    private const string TplApiKeys =
        "{\n" +
        "  \"version\": \"1.0\",\n" +
        "  \"environment\": \"production\",\n" +
        "  \"created\": \"2024-01-15\",\n" +
        "  \"keys\": {\n" +
        "    \"openai\": {\n" +
        "      \"api_key\": \"sk-EXAMPLE1234567890abcdefghijklmnopqrstuvwxyz1234\",\n" +
        "      \"organization\": \"org-EXAMPLE1234567890abcdefghij\",\n" +
        "      \"model\": \"gpt-4-turbo\"\n" +
        "    },\n" +
        "    \"sendgrid\": {\n" +
        "      \"api_key\": \"SG.EXAMPLE1234567890abcdefghijklmnopqrstuvwxyz\",\n" +
        "      \"from_email\": \"noreply@example.com\"\n" +
        "    },\n" +
        "    \"twilio\": {\n" +
        "      \"account_sid\": \"ACEXAMPLE1234567890abcdefghijklmnop\",\n" +
        "      \"auth_token\": \"EXAMPLE1234567890abcdefghijklmnopqr\",\n" +
        "      \"from_number\": \"+15005550006\"\n" +
        "    },\n" +
        "    \"pagerduty\": {\n" +
        "      \"api_key\": \"EXAMPLE1234567890abcdefghijklmnopqr\",\n" +
        "      \"service_key\": \"EXAMPLE1234567890abcdefghijklmnopqr\"\n" +
        "    }\n" +
        "  }\n" +
        "}\n";

    private const string TplGithubPat =
        "ghp_EXAMPLE1234567890abcdefghijklmnopqrstuvwxyz12\n" +
        "Created: 2024-01-15\n" +
        "Scopes: repo, workflow, read:org\n";

    private const string TplStripeKeys =
        "# Stripe API Keys - Production\n" +
        "# Rotated: 2024-01-15\n" +
        "\n" +
        "Publishable Key:\n" +
        "pk_live_EXAMPLE1234567890abcdefghijklmnopqrstuvwxyz\n" +
        "\n" +
        "Secret Key:\n" +
        "sk_live_EXAMPLE1234567890abcdefghijklmnopqrstuvwxyz\n" +
        "\n" +
        "Restricted Key (webhooks):\n" +
        "rk_live_EXAMPLE1234567890abcdefghijklmnopqrstuvwx\n" +
        "\n" +
        "Webhook Signing Secret:\n" +
        "whsec_EXAMPLE1234567890abcdefghijklmnopqrstuvwxyz\n";

    private const string TplGenericJson =
        "{\n  \"synthetic\": true,\n  \"generator\": \"SimpleProvider\"\n}\n";

    private const string TplGenericCsv =
        "column1,column2,column3\nvalue1,value2,value3\n";
}

// =============================================================================
// Entry point
// =============================================================================
internal static class Program
{
    private static int Main(string[] args)
    {
        string sourceRoot      = null;
        string virtRoot        = null;
        string syntheticCsv    = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            string flag = args[i];
            string val  = args[i + 1];
            if (flag.Equals("--sourceroot",    StringComparison.OrdinalIgnoreCase)) sourceRoot   = val;
            else if (flag.Equals("--virtroot",  StringComparison.OrdinalIgnoreCase)) virtRoot    = val;
            else if (flag.Equals("--syntheticdata", StringComparison.OrdinalIgnoreCase)) syntheticCsv = val;
        }

        if (sourceRoot == null || virtRoot == null)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  SimpleProvider.exe --sourceroot <path> --virtroot <path>");
            Console.WriteLine("                     [--syntheticdata <csv-file>]");
            Console.WriteLine();
            Console.WriteLine("CSV format:  \\Path\\To\\Entry,isDirectory,fileSize,unixTimestamp");
            Console.WriteLine();
            Console.WriteLine("Prerequisites:");
            Console.WriteLine("  Run as Administrator.");
            Console.WriteLine("  Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart");
            return 1;
        }

        SyntheticData synthetic = null;
        if (syntheticCsv != null)
        {
            if (!File.Exists(syntheticCsv))
            {
                Console.Error.WriteLine("Synthetic data file not found: " + syntheticCsv);
                return 1;
            }
            try
            {
                synthetic = SyntheticData.Load(syntheticCsv);
                Console.WriteLine("Loaded synthetic data: " + syntheticCsv);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to load synthetic data: " + ex.Message);
                return 1;
            }
        }

        SimpleProvider provider;
        try
        {
            provider = new SimpleProvider(sourceRoot, virtRoot, synthetic);
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
                Console.Error.WriteLine("The virtroot could not be marked as a ProjFS root.");
                Console.Error.WriteLine("Try deleting it manually:  rmdir /s /q \"" + Path.GetFullPath(virtRoot) + "\"");
            }
            else
            {
                Console.Error.WriteLine("Ensure Client-ProjFS is enabled and this process is elevated.");
            }
            return 1;
        }

        Console.WriteLine("Provider running.");
        Console.WriteLine("  Source    : " + Path.GetFullPath(sourceRoot));
        Console.WriteLine("  Virt      : " + Path.GetFullPath(virtRoot));
        if (syntheticCsv != null)
            Console.WriteLine("  Synthetic : " + Path.GetFullPath(syntheticCsv));
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
    // ---- Fields ----

    private readonly string        _sourceRoot;
    private readonly string        _virtRoot;
    private readonly SyntheticData _synthetic;  // null when not used
    private IntPtr                 _virtCtx;

    private readonly ConcurrentDictionary<Guid, EnumerationSession> _sessions
        = new ConcurrentDictionary<Guid, EnumerationSession>();

    private static SimpleProvider _current;

    private readonly Prj.StartDirEnumCb       _cbStartDirEnum;
    private readonly Prj.EndDirEnumCb         _cbEndDirEnum;
    private readonly Prj.GetDirEnumCb         _cbGetDirEnum;
    private readonly Prj.GetPlaceholderInfoCb _cbGetPlaceholderInfo;
    private readonly Prj.GetFileDataCb        _cbGetFileData;

    // ---- DirEntry: unified view of one file-system entry (real or synthetic) ----

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

    public SimpleProvider(string sourceRoot, string virtRoot, SyntheticData synthetic)
    {
        if (!Directory.Exists(sourceRoot))
            throw new ArgumentException("Source root does not exist: " + sourceRoot);

        _sourceRoot = Path.GetFullPath(sourceRoot);
        _virtRoot   = Path.GetFullPath(virtRoot);
        _synthetic  = synthetic;
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

        // Always start with a clean, empty virtroot.
        //
        // PrjStartVirtualizing returns 0x80071126 (ERROR_NOT_A_REPARSE_POINT) in
        // two situations:
        //   1. The directory has never been marked as a ProjFS virtualization root
        //      (PrjMarkDirectoryAsPlaceholder has not been called on it yet).
        //   2. The directory contains stale placeholder files or a leftover reparse
        //      point from a previous session that did not call PrjStopVirtualizing.
        //
        // Deleting and recreating the directory solves both cases.
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

        // Stamp the empty directory with the ProjFS NTFS reparse point.
        // This is a prerequisite for PrjStartVirtualizing and only needs to be
        // done once per directory lifetime.  Because we recreate the directory
        // above, we must call this on every run.
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

        // Collect real entries from the source directory (if it exists).
        List<DirEntry> all = new List<DirEntry>();
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

        // Merge synthetic children, skipping any whose name conflicts with a real entry.
        if (_synthetic != null)
        {
            List<SyntheticEntry> synths = _synthetic.GetChildren(relativePath);
            foreach (SyntheticEntry s in synths)
            {
                if (!NameExists(all, s.Name))
                    all.Add(SyntheticToDirEntry(s));
            }
        }

        // ProjFS requires entries in case-insensitive ordinal order.
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
            if (hr != Prj.S_OK) { Log("<---- GetDirectoryEnumerationCallback " + Prj.Hr(hr)); return hr; }
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

        // Synthetic entries take priority only when no real file/directory exists.
        string fullPath = FullSourcePath(relativePath);
        FileSystemInfo realEntry = SourceEntry(fullPath);

        if (realEntry == null && _synthetic != null)
        {
            SyntheticEntry synth = _synthetic.Find(relativePath);
            if (synth != null)
            {
                long ft = synth.GetFiletime();
                uint attrs = synth.IsDirectory
                    ? (uint)FileAttributes.Directory
                    : (uint)FileAttributes.Normal;

                byte[] info = Prj.BuildPlaceholderInfo(
                    synth.IsDirectory, synth.FileSize,
                    ft, ft, ft, ft, attrs);

                int hr = Prj.PrjWritePlaceholderInfo(virtCtx, relativePath, info, (uint)info.Length);
                Log("<---- GetPlaceholderInfoCallback " + Prj.Hr(hr) + " [synthetic]");
                return hr;
            }
        }

        if (realEntry == null)
        {
            Log("<---- GetPlaceholderInfoCallback FileNotFound");
            return Prj.HR_FILE_NOT_FOUND;
        }

        bool isDir = (realEntry.Attributes & FileAttributes.Directory) != 0;
        long size  = isDir ? 0L : ((FileInfo)realEntry).Length;

        byte[] realInfo = Prj.BuildPlaceholderInfo(
            isDir, size,
            realEntry.CreationTime.ToFileTime(),
            realEntry.LastAccessTime.ToFileTime(),
            realEntry.LastWriteTime.ToFileTime(),
            realEntry.LastWriteTime.ToFileTime(),
            (uint)realEntry.Attributes);

        int realHr = Prj.PrjWritePlaceholderInfo(virtCtx, relativePath, realInfo, (uint)realInfo.Length);
        Log("<---- GetPlaceholderInfoCallback " + Prj.Hr(realHr));
        return realHr;
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

        // Serve synthetic content when no real file exists for this path.
        string fullPath = FullSourcePath(relativePath);
        if (!File.Exists(fullPath) && _synthetic != null)
        {
            SyntheticEntry synth = _synthetic.Find(relativePath);
            if (synth != null)
                return ServeSyntheticContent(synth, virtCtx, dataStreamId, byteOffset, length);
        }

        if (!File.Exists(fullPath))
        {
            Log("<---- return status FileNotFound");
            return Prj.HR_FILE_NOT_FOUND;
        }

        // Stream the real file back in 64 KB sector-aligned chunks.
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

    // Serves a synthetic file: generates content, copies it into an aligned
    // buffer, and calls PrjWriteFileData for the requested byte range.
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
        e.Name            = info.Name;
        e.IsSynthetic     = false;
        e.IsDirectory     = (info.Attributes & FileAttributes.Directory) != 0;
        e.FileSize        = e.IsDirectory ? 0L : ((FileInfo)info).Length;
        e.CreationTimeFt  = info.CreationTime.ToFileTime();
        e.LastAccessTimeFt = info.LastAccessTime.ToFileTime();
        e.LastWriteTimeFt  = info.LastWriteTime.ToFileTime();
        e.FileAttributes  = (uint)info.Attributes;
        return e;
    }

    private static DirEntry SyntheticToDirEntry(SyntheticEntry s)
    {
        long ft = s.GetFiletime();
        DirEntry e = new DirEntry();
        e.Name            = s.Name;
        e.IsSynthetic     = true;
        e.IsDirectory     = s.IsDirectory;
        e.FileSize        = s.FileSize;
        e.CreationTimeFt  = ft;
        e.LastAccessTimeFt = ft;
        e.LastWriteTimeFt  = ft;
        e.FileAttributes  = s.IsDirectory
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

        public void Reset()
        {
            _index = 0;
        }

        public void StepBack()
        {
            if (_index > 0) _index--;
        }

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
