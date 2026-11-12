using System;
using System.Collections.Generic;
using System.Linq;
using PixelPlayer.WinUI.Models;

namespace PixelPlayer.WinUI.Services;

/// <summary>10 段均衡器 + 预设管理（对应参照项目 EqualizerManager 的 31Hz…16kHz 频段）。</summary>
public sealed class EqualizerService
{
    public static EqualizerService Instance { get; } = new();

    /// <summary>参照项目同款频点。</summary>
    public static readonly int[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public static readonly string[] BandLabels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };

    /// <summary>预置：名称 → 10 段增益（dB）。</summary>
    public static readonly Dictionary<string, double[]> Presets = new()
    {
        ["平坦"] = new double[10],
        ["摇滚"] = new[] { 5.0, 4.0, 2.5, -1.0, -2.5, -1.0, 2.0, 4.0, 5.0, 5.0 },
        ["流行"] = new[] { -1.5, 1.0, 3.0, 4.0, 3.5, 1.5, -1.0, -1.5, 1.0, 2.0 },
        ["嘻哈"] = new[] { 6.0, 5.5, 3.0, 1.0, -2.0, -3.0, -1.0, 1.5, 2.5, 3.0 },
        ["爵士"] = new[] { 3.0, 2.5, 1.5, 1.5, -1.0, -1.5, 0.5, 2.0, 3.0, 3.5 },
        ["古典"] = new[] { 3.5, 3.0, 2.5, 1.5, -1.0, -1.0, 0.0, 2.5, 3.0, 3.5 },
        ["电子"] = new[] { 4.5, 3.5, 1.0, -1.5, -2.5, 1.0, 2.5, 3.5, 4.5, 4.5 },
        ["低音增强"] = new[] { 7.0, 6.0, 4.5, 2.0, 0.5, 0.0, 0.0, 0.0, 0.5, 1.0 },
        ["高音增强"] = new[] { -2.0, -1.0, 0.0, 1.0, 1.5, 2.5, 4.5, 5.5, 6.0, 6.5 },
        ["人声"] = new[] { -2.5, -1.5, 0.5, 3.0, 4.5, 4.0, 2.5, 1.0, 0.0, -1.0 },
    };

    public event EventHandler? Changed;

    private EqualizerService() { }

    public bool Enabled
    {
        get => SettingsService.Instance.Settings.EqualizerEnabled;
        set { SettingsService.Instance.Settings.EqualizerEnabled = value; Save(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public string PresetName
    {
        get => SettingsService.Instance.Settings.EqPresetName;
        set { SettingsService.Instance.Settings.EqPresetName = value; Save(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public double[] Gains
    {
        get
        {
            var gains = SettingsService.Instance.Settings.EqGains;
            if (gains.Count != BandFrequencies.Length)
            {
                gains = new List<double>(new double[BandFrequencies.Length]);
                SettingsService.Instance.Settings.EqGains = gains;
            }
            return gains.ToArray();
        }
    }

    public void SetGain(int band, double dB)
    {
        var settings = SettingsService.Instance.Settings;
        if (settings.EqGains.Count != BandFrequencies.Length)
        {
            settings.EqGains = new List<double>(new double[BandFrequencies.Length]);
        }
        settings.EqGains[band] = Math.Clamp(dB, -12, 12);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyPreset(string name)
    {
        if (!Presets.TryGetValue(name, out var gains)) return;
        var settings = SettingsService.Instance.Settings;
        settings.EqPresetName = name;
        settings.EqGains = gains.ToList();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        ApplyPreset("平坦");
        var settings = SettingsService.Instance.Settings;
        settings.BassBoost = false;
        settings.Virtualizer = false;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool BassBoost
    {
        get => SettingsService.Instance.Settings.BassBoost;
        set { SettingsService.Instance.Settings.BassBoost = value; Save(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public bool Virtualizer
    {
        get => SettingsService.Instance.Settings.Virtualizer;
        set { SettingsService.Instance.Settings.Virtualizer = value; Save(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private void Save() => SettingsService.Instance.SaveSettings();
}
