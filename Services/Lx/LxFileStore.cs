using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PixelPlayer.WinUI.Services.Lx;

/// <summary>
/// LX 音源脚本存储（对齐参考项目 LxFileStore）：
/// 脚本存放于 %LocalAppData%\PixelPlayer.WinUI\lx_sources\*.js。
/// </summary>
public sealed class LxFileStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelPlayer.WinUI", "lx_sources");

    public static LxFileStore Instance { get; } = new();

    static LxFileStore()
    {
        try { Directory.CreateDirectory(DataDir); } catch { }
    }

    private LxFileStore() { }

    public record LxJsFile(string Name, long Size);

    public string Dir => DataDir;

    public List<LxJsFile> ListFiles()
    {
        try
        {
            return Directory.GetFiles(DataDir, "*.js")
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new LxJsFile(f.Name, f.Length))
                .ToList();
        }
        catch { return new List<LxJsFile>(); }
    }

    public string? Content(string name)
    {
        try
        {
            var path = SafePath(name);
            return path is null ? null : File.ReadAllText(path);
        }
        catch { return null; }
    }

    /// <summary>导入脚本（bytes 可为原始 UTF-8，兼容 zip 提取的字节）。</summary>
    public bool Import(string name, byte[] bytes) => Import(name, DecodeJsBytes(bytes));

    public bool Import(string name, string js)
    {
        try
        {
            var path = SafePath(name) ?? Path.Combine(DataDir, name);
            File.WriteAllText(path, js);
            return true;
        }
        catch { return false; }
    }

    public bool Delete(string name)
    {
        try
        {
            var path = SafePath(name);
            if (path is null || !File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>仅取 basename 防目录穿越（对齐参考项目 inspectZip）。</summary>
    private static string? SafePath(string name)
    {
        var baseName = name.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(baseName) || baseName == "." || baseName == "..") return null;
        return Path.Combine(DataDir, baseName);
    }

    /// <summary>兼容 zip 中 GB18030/UTF-8 混编的脚本字节（对齐 fileStore.decodeJsBytes）。</summary>
    public static string DecodeJsBytes(byte[] bytes)
    {
        try
        {
            // 去掉 UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            // 若含非 ASCII 且无法按 UTF-8 严格解码，则回退 GB18030
            var strict = new System.Text.UTF8Encoding(false, true);
            try { return strict.GetString(bytes); }
            catch
            {
                try { return System.Text.Encoding.GetEncoding("GB18030").GetString(bytes); }
                catch { return System.Text.Encoding.UTF8.GetString(bytes); }
            }
        }
        catch { return System.Text.Encoding.UTF8.GetString(bytes); }
    }
}
