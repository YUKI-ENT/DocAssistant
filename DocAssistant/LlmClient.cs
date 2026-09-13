using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DocAssistant;

internal sealed class LlmClient : IDisposable
{
    private readonly HttpClient http;
    internal LlmClient(HttpMessageHandler? handler = null)
    {
        http = handler == null ? new(new HttpClientHandler { AllowAutoRedirect = false }) : new(handler);
        http.Timeout = TimeSpan.FromMinutes(5);
    }
    internal static Uri BaseUri(string address, int port)
    {
        if (!address.Contains("://")) address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            port is < 1 or > 65535)
            throw new InvalidOperationException("接続先とポート（1～65535）を確認してください。");
        var path = uri.AbsolutePath.TrimEnd('/');
        return new UriBuilder(uri) { Port = port, Path = path.EndsWith("/v1", StringComparison.Ordinal) ? path + "/" : path + "/v1/" }.Uri;
    }
    private async Task<JsonDocument> SendAsync(Uri uri, object? body, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM API: HTTP {(int)response.StatusCode}。接続先、認証、モデル、コンテキスト長を確認してください。");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }
    internal async Task<string[]> ModelsAsync(Uri endpoint, string key, CancellationToken token)
    {
        using var json = await SendAsync(new(endpoint, "models"), null, key, token);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString())
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).Distinct().Order().ToArray();
    }
    internal async Task<string> GenerateAsync(Uri endpoint, string key, string model, string prompt, string? payload, CancellationToken token)
    {
        using var json = await SendAsync(new(endpoint, "chat/completions"), new
        {
            model, stream = false,
            messages = payload == null
                ? new[] { new { role = "user", content = prompt } }
                : new[] { new { role = "system", content = prompt }, new { role = "user", content = payload } }
        }, key, token);
        var text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("モデルから本文が返りませんでした。");
        return text;
    }
    public void Dispose() => http.Dispose();
}
