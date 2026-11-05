using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ShiKe.ReleaseTools;

internal static class ReleaseKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ShiKe.ReleaseSigning.RSA-PSS-SHA256.v1");
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        nint reserved, nint prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, ref DataBlob entropy,
        nint reserved, nint prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    internal static string Initialize(string path)
    {
        path = ValidateExternalKeyPath(path);
        if (File.Exists(path)) throw new IOException("签名私钥已存在，拒绝覆盖或自动换钥");
        var publicPath = Path.Combine(Path.GetDirectoryName(path)!, "release-public.pem");
        if (File.Exists(publicPath)) throw new IOException("发行公钥文件已存在，拒绝覆盖");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var rsa = RSA.Create(3072);
        var privateBytes = rsa.ExportPkcs8PrivateKey();
        try
        {
            var encrypted = Transform(privateBytes, protect: true);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(encrypted);
            var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法确定当前用户");
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
            File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem(), new UTF8Encoding(false));
            return publicPath;
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
    }

    internal static RSA Open(string path, string expectedPublicPem)
    {
        path = ValidateExternalKeyPath(path);
        if (string.IsNullOrWhiteSpace(expectedPublicPem)) throw new InvalidOperationException("客户端尚未固定发行公钥，不能签正式清单");
        var privateBytes = Transform(File.ReadAllBytes(path), protect: false);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(privateBytes, out var consumed);
            if (consumed != privateBytes.Length || rsa.KeySize < 3072) throw new CryptographicException("发行私钥格式或强度不符");
            using var expected = RSA.Create();
            expected.ImportFromPem(expectedPublicPem);
            if (!CryptographicOperations.FixedTimeEquals(rsa.ExportSubjectPublicKeyInfo(), expected.ExportSubjectPublicKeyInfo()))
                throw new CryptographicException("签名私钥与客户端固定公钥不一致");
            return rsa;
        }
        catch { rsa.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
    }

    internal static string ValidateExternalKeyPath(string path)
    {
        var full = Path.GetFullPath(path);
        var userRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(userRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("发行私钥必须保存在当前 Windows 用户目录内的仓库外位置");
        var current = new DirectoryInfo(Path.GetDirectoryName(full)!);
        for (; current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                throw new IOException("发行私钥不得存放在 Git 工作区");
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("发行私钥路径不得包含目录链接");
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("发行私钥不能是链接文件");
        return full;
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = Allocate(bytes);
        var entropy = Allocate(Entropy);
        DataBlob output = default;
        try
        {
            var success = protect
                ? CryptProtectData(ref input, "ShiKe release signing key", ref entropy, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, 1, out output);
            if (!success) throw new CryptographicException("无法通过当前 Windows 用户保护或读取发行私钥，Win32=" + Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroAndFree(input, local: false);
            ZeroAndFree(entropy, local: false);
            ZeroAndFree(output, local: true);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var blob = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static void ZeroAndFree(DataBlob blob, bool local)
    {
        if (blob.Data == 0) return;
        for (var index = 0; index < blob.Length; index++) Marshal.WriteByte(blob.Data, index, 0);
        if (local) LocalFree(blob.Data); else Marshal.FreeHGlobal(blob.Data);
    }
}
