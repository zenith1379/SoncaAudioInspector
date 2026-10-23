using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static class BleOptionsUiChecks
{
    public static void Run(Assembly assembly)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        var writer = (MixerWriterProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.ISoncaMixerWriter")!, typeof(MixerWriterProxy));
        writer.Connected = writer.Success = true; writer.OptionState[0x6254] = 30152;
        var window = (Window)type.GetConstructors(hidden).Single(c => c.GetParameters().Length == 2).Invoke(new object?[] { null, writer });
        var readback = type.GetMethod("ApplyParameterReadback", hidden)!;
        readback.Invoke(window, new object[] { (ushort)0x6254, (ushort)30152 });
        var fbx = (CheckBox)window.FindName("ChkFbx");
        if (!fbx.IsEnabled || fbx.IsChecked != false || !((CheckBox)window.FindName("ChkBassBoost")).IsEnabled
            || !((CheckBox)window.FindName("ChkEnhancer")).IsEnabled) throw new Exception("Verified C360 options remain disabled.");
        if (!((TextBlock)window.FindName("TxtAppBuild")).Text.StartsWith("APP ")) throw new Exception("Build identity not visible.");
        object option = Enum.Parse(assembly.GetType("SoncaAudioInspector.SoncaMixerOption")!, "MicFbx");
        var change = type.GetMethod("ChangeOptionAsync", hidden)!;
        void Complete(Task task)
        {
            while (!task.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        Complete((Task)change.Invoke(window, new[] { option, (object)true })!);
        if (fbx.IsChecked != true || writer.OptionState[0x6254] != 30153) throw new Exception("Option readback not reflected.");
        writer.Success = false;
        Complete((Task)change.Invoke(window, new[] { option, (object)false })!);
        if (fbx.IsChecked != true || writer.OptionState[0x6254] != 30153) throw new Exception("Rejected option appeared applied.");
        writer.Success = true; writer.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = (Task)change.Invoke(window, new[] { option, (object)false })!;
        if (fbx.IsEnabled || fbx.IsChecked != true) throw new Exception("Pending option was optimistic or unlocked.");
        writer.Pending.SetResult(true); writer.Pending = null; Complete(pending);
        if (!fbx.IsEnabled || fbx.IsChecked != false) throw new Exception("Option completion stuck.");
        type.GetProperty("IsAudioMeasurementBusy")!.SetValue(window, new Func<bool>(() => true));
        Complete((Task)change.Invoke(window, new[] { option, (object)true })!);
        if (writer.OptionState[0x6254] != 30152 || fbx.IsEnabled) throw new Exception("Option changed during measurement.");
        window.Close();
        Console.WriteLine("PASS firmware-controlled Mic FBX/Bass boost/Enhancer UI; build identity; confirmed readback; rejected write rollback; pending lock; measurement ownership. No physical writes.");
    }
}
