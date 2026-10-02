using System.Security.Cryptography;
using System.Text.Json;

namespace AgentUsage;

internal sealed class AccountStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "accounts.dpapi");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public AccountData Load()
    {
        if (!File.Exists(_path)) return new AccountData();
        byte[] encrypted = File.ReadAllBytes(_path);
        byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<AccountData>(plain, Json) ?? throw new InvalidDataException("Account file is empty."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(AccountData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temporary = _path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
