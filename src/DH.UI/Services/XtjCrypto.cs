using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace NewAvalonia.Services
{
    /// <summary>
    /// .xtj 算法文件加/解密工具。
    /// 设计目标：
    /// 1) 使用对称加密 AES-GCM（.NET 5+ 原生支持），抗篡改且具备完整性校验；
    /// 2) 通过固定的程序级派生密钥，确保仅本程序可解密；
    /// 3) 文件格式："XTJ1:" + Base64(nonce|ciphertext|tag)。
    /// </summary>
    internal static class XtjCrypto
    {
        private const string Magic = "XTJ1:"; // 版本/魔术头

        // 从程序集信息 + 固定盐派生 32 字节密钥（不在日志或异常中泄露）
        private static byte[] DeriveAppKey()
        {
            var asm = typeof(XtjCrypto).Assembly.GetName();
            var name = asm.Name ?? "NewAvalonia";
            var pkt = asm.GetPublicKeyToken();
            var pktHex = pkt != null && pkt.Length > 0 ? string.Concat(pkt.Select(b => b.ToString("x2"))) : "nopkt";
            const string salt = "fd6a0caa-xtj-only-this-app-3e7a"; // 编译期常量盐；不要输出到日志
            using var sha = SHA256.Create();
            var secret = $"{name}|{pktHex}|{salt}"; // 不包含版本，避免升级后无法解密
            return sha.ComputeHash(Encoding.UTF8.GetBytes(secret)); // 32 字节
        }

        public static string EncryptJson(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var key = DeriveAppKey();
            var nonce = RandomNumberGenerator.GetBytes(12); // GCM 推荐 12 字节

            var plain = Encoding.UTF8.GetBytes(json);
            var cipher = new byte[plain.Length];
            var tag = new byte[16]; // GCM 128-bit tag
            using (var aes = new AesGcm(key))
            {
                aes.Encrypt(nonce, plain, cipher, tag, associatedData: null);
            }

            // payload = nonce | cipher | tag
            var payload = new byte[nonce.Length + cipher.Length + tag.Length];
            Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
            Buffer.BlockCopy(cipher, 0, payload, nonce.Length, cipher.Length);
            Buffer.BlockCopy(tag, 0, payload, nonce.Length + cipher.Length, tag.Length);
            var b64 = Convert.ToBase64String(payload);
            return Magic + b64;
        }

        /// <summary>
        /// 若传入内容为加密格式（Magic 开头），则尝试解密；否则原样返回。
        /// 解密失败返回 null，并通过 error 输出错误原因。
        /// </summary>
        public static string? TryDecryptIfEncrypted(string content, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(content)) { error = "内容为空"; return null; }
            if (!content.StartsWith(Magic, StringComparison.Ordinal))
            {
                // 非加密：直接返回原始内容
                return content;
            }

            try
            {
                var b64 = content.Substring(Magic.Length).Trim();
                var payload = Convert.FromBase64String(b64);
                if (payload.Length < 12 + 16)
                {
                    error = "密文格式错误";
                    return null;
                }

                var nonce = new byte[12];
                Buffer.BlockCopy(payload, 0, nonce, 0, nonce.Length);
                var tag = new byte[16];
                Buffer.BlockCopy(payload, payload.Length - tag.Length, tag, 0, tag.Length);
                var cipherLen = payload.Length - nonce.Length - tag.Length;
                if (cipherLen < 0) { error = "密文长度无效"; return null; }
                var cipher = new byte[cipherLen];
                Buffer.BlockCopy(payload, nonce.Length, cipher, 0, cipherLen);

                var key = DeriveAppKey();
                var plain = new byte[cipher.Length];
                using (var aes = new AesGcm(key))
                {
                    aes.Decrypt(nonce, cipher, tag, plain, associatedData: null);
                }
                return Encoding.UTF8.GetString(plain);
            }
            catch (FormatException)
            {
                error = "密文 Base64 解析失败";
                return null;
            }
            catch (CryptographicException)
            {
                error = "密文解密失败（密钥或数据无效）";
                return null;
            }
            catch (Exception ex)
            {
                error = $"解密异常: {ex.Message}";
                return null;
            }
        }

        // 可选：文件级便捷方法（不会在 UI 中暴露，供内部/开发时使用）
        public static async Task<string> EncryptFileAsync(string inputPath, string? outputPath = null)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentNullException(nameof(inputPath));
            if (!File.Exists(inputPath)) throw new FileNotFoundException("未找到要加密的文件", inputPath);
            var json = await File.ReadAllTextAsync(inputPath, Encoding.UTF8);
            var cipherText = EncryptJson(json);
            var outPath = outputPath ?? inputPath; // 默认覆盖
            await File.WriteAllTextAsync(outPath, cipherText, Encoding.UTF8);
            return outPath;
        }

        public static async Task<string?> DecryptFileIfEncryptedAsync(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentNullException(nameof(inputPath));
            if (!File.Exists(inputPath)) throw new FileNotFoundException("未找到文件", inputPath);
            var content = await File.ReadAllTextAsync(inputPath, Encoding.UTF8);
            var dec = TryDecryptIfEncrypted(content, out var _);
            return dec;
        }
    }
}