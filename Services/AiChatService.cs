using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services;

/// <summary>
/// AI 聊天客户端（对应参照项目 AiClient/GenericOpenAiClient/GeminiAiClient）：
/// OpenAI 兼容 chat/completions + Gemini generateContent 双协议。
/// </summary>
public static class AiChatService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>预置服务商（对应 AiClientFactory）。</summary>
    public static readonly (string Name, string BaseUrl, string DefaultModel)[] Providers =
    {
        ("OpenAI 兼容", "https://api.openai.com/v1", "gpt-4o-mini"),
        ("Deepseek", "https://api.deepseek.com", "deepseek-chat"),
        ("Groq", "https://api.groq.com/openai/v1", "llama-3.1-8b-instant"),
        ("Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ("OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini"),
        ("Ollama（本机）", "http://127.0.0.1:11434/v1", "llama3"),
        ("Gemini", "https://generativelanguage.googleapis.com", "gemini-1.5-flash"),
    };

    private sealed record ProviderConfig(bool IsGemini, string BaseUrl, string ApiKey, string Model);

    private static ProviderConfig Conf
    {
        get
        {
            var s = SettingsService.Instance.Settings;
            var isGemini = s.AiProvider.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
            return new ProviderConfig(isGemini, s.AiBaseUrl.TrimEnd('/'), s.AiApiKey, s.AiModel);
        }
    }

    public sealed class ChatMessage
    {
        public string Role { get; init; } = "user";
        public string Content { get; init; } = "";
    }

    /// <summary>单轮生成（system + user），返回模型文本回复；失败抛异常。</summary>
    public static async Task<string> GenerateAsync(string system, string user, CancellationToken ct = default)
    {
        var c = Conf;
        if (string.IsNullOrEmpty(c.ApiKey) && !c.BaseUrl.Contains("127.0.0.1") && !c.BaseUrl.Contains("localhost"))
        {
            throw new InvalidOperationException("未配置 API Key");
        }

        return c.IsGemini
            ? await GenerateGeminiAsync(c, system, user, ct)
            : await GenerateOpenAiAsync(c, system, user, ct);
    }

    private static async Task<string> GenerateOpenAiAsync(ProviderConfig c, string system, string user, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = c.Model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            temperature = 0.8,
            stream = false,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{c.BaseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", c.ApiKey);
        if (c.BaseUrl.Contains("openrouter"))
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/PixelPlayer");
            req.Headers.TryAddWithoutValidation("X-Title", "PixelPlayer");
        }

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI 接口错误 {(int)resp.StatusCode}: {Truncate(text, 300)}");
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private static async Task<string> GenerateGeminiAsync(ProviderConfig c, string system, string user, CancellationToken ct)
    {
        var url = $"{c.BaseUrl}/v1beta/models/{c.Model}:generateContent?key={Uri.EscapeDataString(c.ApiKey)}";
        var body = JsonSerializer.Serialize(new
        {
            system_instruction = new { parts = new object[] { new { text = system } } },
            contents = new object[]
            {
                new { role = "user", parts = new object[] { new { text = user } } },
            },
            generationConfig = new { temperature = 0.8 },
        });

        using var resp = await _http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini 接口错误 {(int)resp.StatusCode}: {Truncate(text, 300)}");
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }

    /// <summary>测试连接（设置页用）。</summary>
    public static async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var reply = await GenerateAsync("You are a helpful assistant.", "回复两个字：成功", ct);
            return (true, $"连接成功：{Truncate(reply.Replace("\n", " "), 60)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
