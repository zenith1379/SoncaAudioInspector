namespace SoncaAudioInspector;

public static class Acoustic3ModelDefinition
{
    public const string Name = "ACOUSTIC 3", Profile = "SoncaElectronic";
    public const ushort FirmwareModelId = 1071;
    public static ModelConfig Create(string analogPlayback, string capture, string usbPlayback = "Speakers (CS3600 MUSIC)",
        bool micReady = false, bool lineReady = false, bool usbReady = false, bool bluetoothReady = false)
    {
        var model = C360ModelDefinition.Create(Name);
        var io = model.testItems.InOut;
        io.AutomationProfile=Profile; io.AutomationProfileVersion=1; io.FirmwareModelId=FirmwareModelId;
        io.Description="ACOUSTIC 3: MIC IN effect / FBX / không effect; tắt MIC/ECHO/REVERB trước LINE IN, USB A và BT. Thu điện tử cố định qua LINE OUT / USB recording. Chỉ bật bài đã xác nhận đường đo.";
        io.Devices.Input["MIC IN"]=analogPlayback; io.Devices.Input["LINE IN"]=analogPlayback;
        io.Devices.Input["USB A"]=usbPlayback; io.Devices.Input["BT"]="ACOUSTIC 3";
        io.Devices.Output["LINE OUT"]=capture;
        io.TestVolume=new();
        foreach(var test in io.Tests)
        {
            test.MaximumRuns=3; test.RequiredPasses=2;
            test.Enable=test.SpeakerStage switch
            {
                "MicEffects" or "MicFrequencyShift" or "MicDry" => micReady,
                "LineIn" => lineReady, "UsbA" => usbReady, "Bluetooth" => bluetoothReady, _ => false
            };
            test.PlaybackLevelDbfs=test.SpeakerStage is "MicEffects" or "MicFrequencyShift" or "MicDry" ? -24 : -12;
        }
        return model;
    }
    public static void RequireFirmware(InOutConfig config, ReadOnlySpan<byte> payload)
    {
        if(config.FirmwareModelId is ushort expected && (!SoncaBleMixerProfile.TryReadModelId(payload,out ushort actual) || actual!=expected))
            throw new InvalidOperationException($"Firmware của bo BLE chưa khớp model đang chọn (mã {expected}). Kết nối đúng bo tại Ctrl+Shift+H trước khi đo.");
    }
}
