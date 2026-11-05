namespace ShiKe.Services.Updates;

/// <summary>发行信任根只随程序交付；服务器不能下发或替换公钥。</summary>
public static class ReleaseTrust
{
    public const string KeyId = "shike-release-2026";
    // 私钥仅保存在发布者的 Windows 用户目录并受 DPAPI 保护；程序只包含公钥。
    public const string PublicKeyPem = """
    -----BEGIN PUBLIC KEY-----
    MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAsUyCST3ukhHpcsRN3XZY
    YfAzVTh2sx0zswXquKIzU26snllh2Yc8Iw4TUFq4qIgjDbhGXLFZlilF92ZTD6oh
    LlUPQvHYH1aeR8ZM9hFT2qeuBDij1jGbrqUm3+1d62a1Edcjsba5V1SR+5XiN8KQ
    /i3TfXACZ1TjviO8TyiQH1iGgRD8pPxBl3bOBfA8P9DZQ3xmTApp/sn/pD/FwZ3v
    O/g3rd4j25iknO12/8f07ILYwErZaRzdNbdMChZp+VkBfoRtJq06TDsK1N3Y6m6C
    5NQLMEyQq1k5MIRwe+PUPAOnlldfTQEwdJ0XHNU1Y/iAXm9vWzb8guH7HBLLaGy/
    y9QxbvnZ4Slx2AIDydmQh+cnWT6Djffbq5RLaAkgW+DU0X6Uy5E94BfPQgTfFGIO
    vcywTDpByiL5awpa2z/TjCMOJZe6vLrXlDN0HGn3HzF2Axm59B9hVjqGH2ynKd8m
    GVgfH9Tcm1HCqXCFpuSOVhRp2Tq13U0MEYi36o/I1AvtAgMBAAE=
    -----END PUBLIC KEY-----
    """;
}
