// ============================================================
// SecureKeyStorage.cs
// 역할 : API 키를 Windows DPAPI로 암호화해 로컬 파일에 저장.
//        저장 위치: %APPDATA%\TransLearn\{keyname}.dat (암호화 바이너리)
//
// 특징
//   - 현재 Windows 계정으로만 복호화 가능 (다른 PC/계정 이식 불가)
//   - API 키를 평문으로 앱 설정 파일에 저장하는 것보다 안전
// ============================================================
using System.Security.Cryptography;
using System.Text;
using System.IO;
namespace TransLearn.Services;

public static class SecureKeyStorage
{
    private static string KeyDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TransLearn");

    private static string KeyFile(string name) => Path.Combine(KeyDir, $"{name}.dat");

    public static void Save(string keyName, string value)
    {
        Directory.CreateDirectory(KeyDir);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyFile(keyName), encrypted);
    }

    public static string? Load(string keyName)
    {
        var path = KeyFile(keyName);
        if (!File.Exists(path)) return null;
        try
        {
            var decrypted = ProtectedData.Unprotect(
                File.ReadAllBytes(path), null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            return null; // Different user/machine
        }
    }

    public static void Delete(string keyName)
    {
        var path = KeyFile(keyName);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool Exists(string keyName) => File.Exists(KeyFile(keyName));
}
