using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class SoncaBleMixerWindow
{
    private readonly Dictionary<ushort,ushort> _knownVolumes = new(), _additionalState = new();
    private readonly Dictionary<ushort,(SoncaMixerControl Definition, Slider Slider, TextBlock Value)> _dspControls = new();
    private string _dspFirmware = "";
    private (ushort Register, string Key, string Label, Slider Slider, TextBlock Text)[] VolumeControls() => new[]
    {
        (SoncaBleMixerProfile.MicVolume,"MicVol","MIC",SliderMicVol,TxtMicVal),
        (SoncaBleMixerProfile.MusicVolume,"MusicVol","MUSIC",SliderMusicVol,TxtMusicVal),
        (SoncaBleMixerProfile.MasterVolume,"MasterVol","MASTER DSP",SliderMasterVol,TxtMasterVal),
        (SoncaBleMixerProfile.EchoVolume,"EchoVol","ECHO",SliderEcho,TxtEchoVal),
        (SoncaBleMixerProfile.ReverbVolume,"ReverbVol","REVERB",SliderReverb,TxtReverbVal),
        (SoncaBleMixerProfile.LiveVolume,"LiveVol","RECORD",SliderLiveVol,TxtLiveVal)
    };
    private void RestoreKnownLevels()
    {
        foreach (var control in VolumeControls())
            if (!control.Slider.IsMouseCaptureWithin && _knownVolumes.TryGetValue(control.Register, out ushort raw))
                ApplyParameterReadback(control.Register, raw);
        RefreshDspControls();
    }
    private void RefreshDspControls()
    {
        if (PanelDspControls == null) return;
        string firmware = Convert.ToHexString(_writer.FirmwareInfoPayload);
        if (_dspFirmware != firmware || PanelDspControls.Children.Count == 0)
        {
            _dspFirmware = firmware; _dspControls.Clear(); PanelDspControls.Children.Clear();
            foreach (var group in SoncaMixerControls.ForFirmware(_writer.FirmwareInfoPayload).GroupBy(c => c.Group))
            {
                PanelDspControls.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Turquoise, Margin = new Thickness(0,12,0,4) });
                foreach (var definition in group)
                {
                    var row = new Grid { Margin = new Thickness(0,4,0,4), ToolTip = definition.Description };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                    row.Children.Add(new TextBlock { Text = definition.Name, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.White, FontSize = 11 });
                    var slider = new Slider { Minimum = definition.Minimum, Maximum = definition.Maximum,
                        SmallChange = definition.Step, TickFrequency = definition.Step, IsSnapToTickEnabled = true,
                        IsEnabled = false, Margin = new Thickness(6,0,6,0), Tag = definition.Register };
                    var value = new TextBlock { Text = "—", Foreground = Brushes.White, FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                    slider.ValueChanged += (_, _) =>
                    {
                        if (_suppressSliderEvents || !slider.IsEnabled || IsAudioMeasurementBusy?.Invoke() == true
                            || !_additionalState.ContainsKey(definition.Register)) return;
                        ushort raw = definition.Encode(slider.Value);
                        value.Text = definition.Display(raw);
                        QueueCommand("DSP:" + definition.Register, 0, definition.Register, definition.CommandFor(raw), definition.Group + " " + definition.Name);
                    };
                    Grid.SetColumn(slider,1); Grid.SetColumn(value,2); row.Children.Add(slider); row.Children.Add(value);
                    _dspControls[definition.Register] = (definition,slider,value); PanelDspControls.Children.Add(row);
                }
            }
            if (_dspControls.Count == 0) PanelDspControls.Children.Add(new TextBlock
            { Text = "Chưa có sơ đồ núm effect/âm sắc phù hợp với firmware đang nối.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
        }
        bool suppress = _suppressSliderEvents; _suppressSliderEvents = true;
        try
        {
            foreach (var pair in _dspControls)
            {
                var control = pair.Value;
                bool known = _writer.IsConnected && _writer.SupportsMixerControls && _additionalState.TryGetValue(pair.Key, out ushort raw)
                    && control.Definition.Accepts(raw);
                control.Slider.IsEnabled = known && IsAudioMeasurementBusy?.Invoke() != true;
                control.Slider.Opacity = known ? 1 : .4;
                bool pending = _sendingSliderCommand || _pendingCommands.ContainsKey("DSP:" + pair.Key) || control.Slider.IsMouseCaptureWithin;
                if (!known) control.Value.Text = "—";
                else if (!pending)
                {
                    raw = _additionalState[pair.Key]; control.Slider.Value = control.Definition.Decode(raw);
                    control.Value.Text = control.Definition.Display(raw);
                }
            }
        }
        finally { _suppressSliderEvents = suppress; }
    }
}
