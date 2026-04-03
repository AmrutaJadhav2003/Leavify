using System.Security.Cryptography;
using System.Text;

namespace Rite.LeaveManagement.Svc.Services
{
    public static class FileEncryptionHelper
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("P9vC7nTx4E2kF8ZbD3qJ6LmX1sRvWyUg");  // 32 chars = 256 bits
        private static readonly byte[] IV = Encoding.UTF8.GetBytes("X4vF7uA1zQdM2kLp");                 // 16 chars = 128 bits

        public static void EncryptAndSave(string filePath, byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;

            using var encryptor = aes.CreateEncryptor();
            using var fs = new FileStream(filePath, FileMode.Create);
            using var cs = new CryptoStream(fs, encryptor, CryptoStreamMode.Write);
            cs.Write(data, 0, data.Length);
        }

        public static byte[] DecryptFromFile(string filePath)
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.IV = IV;

            using var decryptor = aes.CreateDecryptor();
            using var fs = new FileStream(filePath, FileMode.Open);
            using var cs = new CryptoStream(fs, decryptor, CryptoStreamMode.Read);
            using var ms = new MemoryStream();
            cs.CopyTo(ms);
            return ms.ToArray();
        }
    }

}
