using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PixelPlayer.WinUI.Services;

namespace PixelPlayer.WinUI.Pages;

/// <summary>均衡器页：10 段 EQ + 预设 + 低音增强/空间音效（对应参照项目 EqualizerScreen）。</summary>
public sealed partial class EqualizerPage : Page
{
    public sealed class BandItem
    {
        public int Index { get; init; }
        public string Label { get; init; } = "";
        public double Gain { get; set; }
        public string GainText => $"{Gain:+0.0;-0.0;0.0} dB";
    }

    private readonly List<BandItem> _bands = new();
    private bool _suppress;

    public EqualizerPage()
    {
        InitializeComponent();

        PresetBox.ItemsSource = EqualizerService.Presets.Keys.ToList();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EqualizerService.Instance.Changed += OnEqChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EqualizerService.Instance.Changed -= OnEqChanged;
    }

    private void OnEqChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var eq = EqualizerService.Instance;

        _suppress = true;
        EqSwitch.IsOn = eq.Enabled;
        BassSwitch.IsOn = eq.BassBoost;
        VirtualSwitch.IsOn = eq.Virtualizer;

        if (PresetBox.SelectedItem is null || (PresetBox.SelectedItem as string) != eq.PresetName)
        {
            PresetBox.SelectedItem = eq.PresetName;
        }

        var gains = eq.Gains;
        _bands.Clear();
        for (var i = 0; i < EqualizerService.BandLabels.Length; i++)
        {
            _bands.Add(new BandItem
            {
                Index = i,
                Label = EqualizerService.BandLabels[i],
                Gain = gains[i],
            });
        }
        BandsList.ItemsSource = null;
        BandsList.ItemsSource = _bands;
        _suppress = false;
    }

    private void EqSwitch_Toggled(object sender, RoutedEventArgs e) =>
        EqualizerService.Instance.Enabled = EqSwitch.IsOn;

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (PresetBox.SelectedItem is string name) EqualizerService.Instance.ApplyPreset(name);
    }

    private void Band_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not Slider { Tag: int index }) return;
        EqualizerService.Instance.SetGain(index, e.NewValue);
    }

    private void Bass_Toggled(object sender, RoutedEventArgs e) =>
        EqualizerService.Instance.BassBoost = BassSwitch.IsOn;

    private void Virtual_Toggled(object sender, RoutedEventArgs e) =>
        EqualizerService.Instance.Virtualizer = VirtualSwitch.IsOn;

    private void Reset_Click(object sender, RoutedEventArgs e) => EqualizerService.Instance.Reset();
}
