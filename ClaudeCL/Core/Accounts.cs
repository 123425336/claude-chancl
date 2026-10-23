using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCL.Core;

public enum AccountKind { Offline, Microsoft }

/// <summary>
/// 一个游戏账号；令牌以当前 Windows 用户加密后保存
/// </summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string? AccessTokenProtected { get; set; }
    public string? RefreshTokenProtected { get; set; }
    public DateTime ExpiresAt { get; set; }

    [JsonIgnore] public string KindText => Kind == AccountKind.Microsoft ? "微软账号" : "离线账号";
    [JsonIgnore] public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    [JsonIgnore]
    public string AccessToken
    {
        get => Unprotect(AccessTokenProtected) ?? Uuid;
        set => AccessTokenProtected = Protect(value);
    }

    [JsonIgnore]
    public string? RefreshToken
    {
        get => Unprotect(RefreshTokenProtected);
        set => RefreshTokenProtected = value is null ? null : Protect(value);
    }

    private static string Protect(string text) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
}


/// <summary>
/// 账号的创建、登录与刷新
/// </summary>
public static class AccountService
{
    public static Account? Selected
    {
        get
        {
            var s = Settings.Current;
            return s.Accounts.Find(a => a.Id == s.SelectedAccountId) ?? (s.Accounts.Count > 0 ? s.Accounts[0] : null);
        }
    }

    public static Account CreateOffline(string name)
    {
        name = name.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
            throw new ArgumentException("玩家名需要 3~16 位，只能包含英文字母、数字和下划线");
        var acc = new Account { Kind = AccountKind.Offline, Name = name, Uuid = OfflineUuid(name), ExpiresAt = DateTime.MaxValue };
        acc.AccessToken = Guid.NewGuid().ToString("N");
        return acc;
    }

    /// <summary>
    /// 离线 UUID：与原版服务端一致，对 "OfflinePlayer:名字" 做 MD5 并设置版本号 3
    /// </summary>
    public static string OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return Convert.ToHexStringLower(hash);
    }

    public static void Add(Account acc)
    {
        var list = Settings.Current.Accounts;
        // 同名同类型账号视为同一个，直接替换
        list.RemoveAll(a => a.Kind == acc.Kind && (a.Uuid == acc.Uuid || a.Name.Equals(acc.Name, StringComparison.OrdinalIgnoreCase)));
        list.Insert(0, acc);
        Settings.Current.SelectedAccountId = acc.Id;
        Settings.Save();
    }

    public static void Remove(Account acc)
    {
        Settings.Current.Accounts.RemoveAll(a => a.Id == acc.Id);
        if (Settings.Current.SelectedAccountId == acc.Id) Settings.Current.SelectedAccountId = null;
        Settings.Save();
    }

    // ---------------- 微软登录 ----------------

    private const string Scope = "XboxLive.signin offline_access";

    private static string ClientId =>
        string.IsNullOrWhiteSpace(Settings.Current.MsClientId)
            ? throw new InvalidOperationException("还没有设置微软登录的 Client ID，请先到「设置 → 微软登录」中填写")
            : Settings.Current.MsClientId.Trim();


    /// <summary>
    /// 网页授权登录：在浏览器打开微软登录页，用户同意后回调到本机临时端口，换取令牌
    /// </summary>
    public static async Task<Account> LoginMicrosoftAsync(Action<string> onUrl, CancellationToken ct)
    {
        var clientId = ClientId;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://localhost:{port}";
            // 授权码 + PKCE，防止授权码被截获后冒用
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));
            var url = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize" +
                      $"?client_id={Uri.EscapeDataString(clientId)}&response_type=code&response_mode=query" +
                      $"&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(Scope)}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256&state={state}&prompt=select_account";
            onUrl(url);
            Shell.Open(url);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            string? code = null;
            while (code is null)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var (query, ok) = await HandleCallbackAsync(client, state, timeout.Token);
                if (!ok) continue;
                if (query.TryGetValue("error", out var err))
                    throw new InvalidOperationException(query.GetValueOrDefault("error_description") ?? err);
                code = query.GetValueOrDefault("code");
            }

            var token = await PostFormAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", new()
            {
                ["client_id"] = clientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect,
                ["code_verifier"] = verifier,
                ["scope"] = Scope,
            }, ct);
            if (token["error"] is not null)
                throw new InvalidOperationException((string?)token["error_description"] ?? (string?)token["error"]);
            return await FinishAsync((string)token["access_token"]!, (string?)token["refresh_token"], ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("等了 10 分钟还没有完成网页登录，请重新试一次");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 处理浏览器的回调请求并返回一个温柔的提示页；不是登录回调时返回 false
    /// </summary>
    private static async Task<(Dictionary<string, string> Query, bool Ok)> HandleCallbackAsync(TcpClient client, string state, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, ct);
        var request = Encoding.ASCII.GetString(buffer, 0, read);
        var target = request.Split(' ').ElementAtOrDefault(1) ?? "/";
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var qi = target.IndexOf('?');
        if (qi >= 0)
        {
            foreach (var pair in target[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            }
        }
        var isCallback = query.GetValueOrDefault("state") == state;
        var success = isCallback && query.ContainsKey("code");
        var (title, text) = success
            ? ("登录成功啦", "已经拿到授权，可以关掉这个页面，回到 ClaudeCL 了～")
            : isCallback
                ? ("登录没有完成", HtmlEncode(query.GetValueOrDefault("error_description") ?? "授权被取消了，回到启动器再试一次吧。"))
                : ("ClaudeCL", "正在等待微软登录……");
        var html = $$"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>{{title}}</title>
            <style>body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:#F4EFE6;font-family:"Microsoft YaHei UI",sans-serif;color:#2A251F}
            .card{background:#FFFDF9;border:1px solid #E4DACB;border-radius:18px;padding:36px 44px;text-align:center;box-shadow:0 8px 30px rgba(90,70,50,.12)}
            h1{font-family:Georgia,serif;font-weight:normal;margin:0 0 10px;color:#D97757}p{margin:0;color:#6E655A}</style></head>
            <body><div class="card"><h1>{{title}}</h1><p>{{text}}</p></div></body></html>
            """;
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        return (query, isCallback);
    }

    private static string HtmlEncode(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// 启动前确保账号令牌有效，过期则用刷新令牌续期
    /// </summary>
    public static async Task EnsureValidAsync(Account acc, CancellationToken ct)
    {
        if (acc.Kind != AccountKind.Microsoft || DateTime.UtcNow < acc.ExpiresAt.AddMinutes(-10)) return;
        var refresh = acc.RefreshToken ?? throw new InvalidOperationException("登录已失效，请重新登录微软账号");
        var token = await PostFormAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", new()
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["scope"] = Scope,
        }, ct);
        if (token["error"] is not null)
            throw new InvalidOperationException("登录已失效，请重新登录微软账号：" + (string?)token["error_description"]);
        var renewed = await FinishAsync((string)token["access_token"]!, (string?)token["refresh_token"] ?? refresh, ct);
        acc.Name = renewed.Name;
        acc.AccessTokenProtected = renewed.AccessTokenProtected;
        acc.RefreshTokenProtected = renewed.RefreshTokenProtected;
        acc.ExpiresAt = renewed.ExpiresAt;
        Settings.Save();
    }

    /// <summary>
    /// 微软令牌 → Xbox Live → XSTS → 游戏令牌 → 玩家档案
    /// </summary>
    private static async Task<Account> FinishAsync(string msToken, string? refreshToken, CancellationToken ct)
    {
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, null, ct);
        var xblToken = (string)xbl["Token"]!;
        var uhs = (string)xbl["DisplayClaims"]!["xui"]![0]!["uhs"]!;

        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
        {
            ["Properties"] = new JsonObject { ["SandboxId"] = "RETAIL", ["UserTokens"] = new JsonArray(xblToken) },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        }, null, ct);
        if (xsts["XErr"] is { } xerr)
        {
            throw new InvalidOperationException((long)xerr switch
            {
                2148916233 => "这个微软账号还没有创建 Xbox 档案，请先到 Xbox 官网登录一次",
                2148916238 => "未成年账号需要家长把它加入家庭组后才能登录",
                _ => $"Xbox 验证失败（{xerr}）",
            });
        }

        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new JsonObject
        {
            ["identityToken"] = $"XBL3.0 x={uhs};{(string)xsts["Token"]!}",
        }, null, ct);
        var mcToken = (string)mc["access_token"]!;
        var expires = (int?)mc["expires_in"] ?? 86400;

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new("Bearer", mcToken);
        using var resp = await Net.Client.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("这个微软账号还没有购买 Minecraft，或者还没有创建游戏档案");
        resp.EnsureSuccessStatusCode();
        var profile = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;

        var acc = new Account
        {
            Kind = AccountKind.Microsoft,
            Name = (string)profile["name"]!,
            Uuid = (string)profile["id"]!,
            ExpiresAt = DateTime.UtcNow.AddSeconds(expires),
        };
        acc.AccessToken = mcToken;
        acc.RefreshToken = refreshToken;
        return acc;
    }

    private static async Task<JsonNode> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Net.Client.PostAsync(url, new FormUrlEncodedContent(form), ct);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    private static async Task<JsonNode> PostJsonAsync(string url, JsonObject body, string? bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Accept.ParseAdd("application/json");
        if (bearer is not null) req.Headers.Authorization = new("Bearer", bearer);
        using var resp = await Net.Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var json = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
        if (!resp.IsSuccessStatusCode && json["XErr"] is null)
            throw new HttpRequestException($"请求 {new Uri(url).Host} 失败（{(int)resp.StatusCode}）");
        return json;
    }
}