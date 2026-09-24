using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SeedToolBox.DevTools;

/// <summary>HS* / RS* signing and verification of JWTs, including PEM public keys parsed with a minimal DER reader.</summary>
static class JwtCrypto
{
    static readonly string[] CommonSecrets = { "secret", "password", "123456", "12345678", "changeme", "your-256-bit-secret", "your-384-bit-secret", "your-512-bit-secret", "key", "jwt", "test", "admin" };

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string part)
    {
        var s = part.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    static HMAC Hmac(string alg, byte[] key) => alg switch
    {
        "HS256" => new HMACSHA256(key),
        "HS384" => new HMACSHA384(key),
        "HS512" => new HMACSHA512(key),
        _ => throw new NotSupportedException("不支持的算法 " + alg),
    };

    static HashAlgorithmName RsaHash(string alg) => alg switch
    {
        "RS256" => HashAlgorithmName.SHA256,
        "RS384" => HashAlgorithmName.SHA384,
        "RS512" => HashAlgorithmName.SHA512,
        _ => throw new NotSupportedException("不支持的算法 " + alg),
    };

    public static bool IsHmac(string alg) => alg is "HS256" or "HS384" or "HS512";
    public static bool IsRsa(string alg) => alg is "RS256" or "RS384" or "RS512";

    /// <summary>Signs "header.payload" with an HMAC secret and returns the full token.</summary>
    public static string SignHmac(string alg, string signingInput, string secret)
    {
        using var hmac = Hmac(alg, Encoding.UTF8.GetBytes(secret));
        return signingInput + "." + Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
    }

    /// <summary>Checks the signature of a three-part token; <paramref name="key"/> is the HMAC secret or a PEM public key.</summary>
    public static bool Verify(string alg, string token, string key)
    {
        int dot = token.LastIndexOf('.');
        var input = Encoding.ASCII.GetBytes(token.Substring(0, dot));
        var signature = FromBase64Url(token.Substring(dot + 1));
        if (IsHmac(alg))
        {
            using var hmac = Hmac(alg, Encoding.UTF8.GetBytes(key));
            var expected = hmac.ComputeHash(input);
            // Constant time, although timing hardly matters in a desktop tool
            int diff = expected.Length ^ signature.Length;
            for (int i = 0; i < Math.Min(expected.Length, signature.Length); i++) diff |= expected[i] ^ signature[i];
            return diff == 0;
        }
        if (IsRsa(alg))
        {
            using var rsa = new RSACng();
            rsa.ImportParameters(ParsePublicKey(key));
            return rsa.VerifyData(input, signature, RsaHash(alg), RSASignaturePadding.Pkcs1);
        }
        throw new NotSupportedException("暂不支持校验 " + alg + " 签名");
    }

    /// <summary>A warning when an HMAC secret is short or well known, otherwise null.</summary>
    public static string? WeakSecret(string alg, string secret)
    {
        int bits = int.Parse(alg.Substring(2));
        if (CommonSecrets.Contains(secret.Trim(), StringComparer.OrdinalIgnoreCase)) return "密钥是常见弱口令";
        int bytes = Encoding.UTF8.GetByteCount(secret);
        if (bytes * 8 < bits) return $"密钥只有 {bytes} 字节，{alg} 建议至少 {bits / 8} 字节";
        if (secret.Distinct().Count() <= 3) return "密钥字符种类太少";
        return null;
    }

    /// <summary>Accepts "PUBLIC KEY" (SubjectPublicKeyInfo), "RSA PUBLIC KEY" (PKCS#1) and "CERTIFICATE" PEM blocks.</summary>
    public static RSAParameters ParsePublicKey(string pem)
    {
        var text = pem.Trim();
        string? label = null;
        int begin = text.IndexOf("-----BEGIN ", StringComparison.Ordinal);
        if (begin >= 0)
        {
            int labelEnd = text.IndexOf("-----", begin + 11, StringComparison.Ordinal);
            if (labelEnd < 0) throw new FormatException("PEM 格式不完整");
            label = text.Substring(begin + 11, labelEnd - begin - 11);
            int end = text.IndexOf("-----END", labelEnd, StringComparison.Ordinal);
            if (end < 0) throw new FormatException("PEM 缺少 END 行");
            text = text.Substring(labelEnd + 5, end - labelEnd - 5);
        }
        byte[] der;
        try { der = Convert.FromBase64String(new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray())); }
        catch (FormatException) { throw new FormatException("公钥不是有效的 PEM / Base64"); }

        if (label == "CERTIFICATE")
        {
            using var cert = new X509Certificate2(der);
            using var rsa = cert.GetRSAPublicKey() ?? throw new FormatException("证书里不是 RSA 公钥");
            return rsa.ExportParameters(false);
        }
        if (label is "PRIVATE KEY" or "RSA PRIVATE KEY") throw new FormatException("请填写公钥而不是私钥");

        var reader = new DerReader(der);
        var outer = reader.Sequence();
        // SubjectPublicKeyInfo starts with the AlgorithmIdentifier sequence, PKCS#1 with the modulus
        if (outer.PeekTag() == 0x30)
        {
            var algorithm = outer.Sequence();
            var oid = algorithm.Read(0x06);
            if (!oid.SequenceEqual(new byte[] { 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01 })) throw new FormatException("不是 RSA 公钥");
            var bits = outer.Read(0x03);
            if (bits.Length == 0 || bits[0] != 0) throw new FormatException("公钥格式不正确");
            outer = new DerReader(bits.Skip(1).ToArray()).Sequence();
        }
        return new RSAParameters { Modulus = Unsigned(outer.Read(0x02)), Exponent = Unsigned(outer.Read(0x02)) };
    }

    static byte[] Unsigned(byte[] integer)
    {
        int skip = 0;
        while (skip < integer.Length - 1 && integer[skip] == 0) skip++;
        return integer.Skip(skip).ToArray();
    }

    /// <summary>Just enough DER to walk a public key: definite lengths, no tags above 30.</summary>
    sealed class DerReader
    {
        readonly byte[] _data;
        int _pos;
        readonly int _end;

        public DerReader(byte[] data, int start = 0, int end = -1) { _data = data; _pos = start; _end = end < 0 ? data.Length : end; }

        public int PeekTag() => _pos < _end ? _data[_pos] : -1;

        (int Start, int Length) Header(int tag)
        {
            if (_pos >= _end || _data[_pos] != tag) throw new FormatException("公钥格式不正确");
            _pos++;
            if (_pos >= _end) throw new FormatException("公钥数据被截断");
            int length = _data[_pos++];
            if (length >= 0x80)
            {
                int count = length & 0x7F;
                if (count == 0 || count > 4) throw new FormatException("公钥格式不正确");
                length = 0;
                for (int i = 0; i < count; i++)
                {
                    if (_pos >= _end) throw new FormatException("公钥数据被截断");
                    length = (length << 8) | _data[_pos++];
                }
            }
            if (length < 0 || _pos + length > _end) throw new FormatException("公钥数据被截断");
            int start = _pos;
            _pos += length;
            return (start, length);
        }

        public byte[] Read(int tag)
        {
            var (start, length) = Header(tag);
            var result = new byte[length];
            Array.Copy(_data, start, result, 0, length);
            return result;
        }

        public DerReader Sequence()
        {
            var (start, length) = Header(0x30);
            return new DerReader(_data, start, start + length);
        }
    }
}
