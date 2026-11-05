using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class BleBluetoothAudioUiChecks
{
    public static void Run(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(output,"data"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        var endpointType = assembly.GetType("SoncaAudioInspector.BluetoothPlaybackEndpoint")!;
        var writer = (MixerWriterProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.ISoncaMixerWriter")!,typeof(MixerWriterProxy));
        writer.Connected = writer.Success = true;
        var window = (Window)type.GetConstructors(hidden).Single(c=>c.GetParameters().Length==2).Invoke(new object?[]{null,writer});
        ulong target=0x439F3F264237; int generation=1,calls=0; CancellationToken lastToken=default;
        type.GetField("ReadBluetoothController",hidden)!.SetValue(window,(Func<ulong?>)(()=>target));
        type.GetField("ReadBluetoothGeneration",hidden)!.SetValue(window,(Func<int>)(()=>generation));
        var peerType=assembly.GetType("SoncaAudioInspector.BluetoothAudioPeer")!;
        var peersType=typeof(IReadOnlyList<>).MakeGenericType(peerType);
        object emptyTask=typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(peersType).Invoke(null,new object[]{Array.CreateInstance(peerType,0)})!;
        var reader=type.GetField("ReadConnectedAudioPeers",hidden)!;
        reader.SetValue(window,Expression.Lambda(reader.FieldType,Expression.Constant(emptyTask,reader.FieldType.GetGenericArguments()[0])).Compile());
        var pendingType=typeof(TaskCompletionSource<>).MakeGenericType(endpointType);
        object Pending()=>Activator.CreateInstance(pendingType)!;
        void Connector(object pending)
        {
            var field=type.GetField("ConnectBluetoothAudio",hidden)!;
            var address=Expression.Parameter(typeof(ulong),"address");var token=Expression.Parameter(typeof(CancellationToken),"token");
            Action<ulong,CancellationToken> seen=(actual,ct)=>{if(actual!=target)throw new Exception("Pairing targeted another board");calls++;lastToken=ct;};
            var invoke=Expression.Invoke(Expression.Constant(seen),address,token);
            var task=Expression.Constant(pendingType.GetProperty("Task")!.GetValue(pending),field.FieldType.GetMethod("Invoke")!.ReturnType);
            field.SetValue(window,Expression.Lambda(field.FieldType,Expression.Block(invoke,task),address,token).Compile());
        }
        void Result(object pending,string name)=>pendingType.GetMethod("SetResult")!.Invoke(pending,new[]{Activator.CreateInstance(endpointType,new object?[]{"fake-endpoint",name,(ulong?)target,true,null})!});
        void Status(bool connected)=>type.GetMethod("OnConnectionStatusChanged",hidden)!.Invoke(window,new object[]{connected,"fixture"});
        string Text()=>((TextBlock)window.FindName("TxtBluetoothAudioStatus")).Text;
        bool Pairing()=>(bool)type.GetField("_pairingBluetoothAudio",hidden)!.GetValue(window)!;
        static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
        try
        {
            var first=Pending();Connector(first);Status(true);
            Check(calls==1 && Pairing(),"BLE ready did not immediately start audio connection");
            Status(true);Check(calls==1,"Duplicate BLE status started another pairing");
            Result(first,"ACOUSTIC 3 stereo");Until(()=>!Pairing());
            Check(Text().Contains("ACOUSTIC 3 stereo"),"Verified audio endpoint not shown");
            Status(true);Check(calls==1,"Periodic status repeated pairing after success");

            generation++;var old=Pending();Connector(old);Status(true);var oldToken=lastToken;
            writer.Connected=false;Status(false);Check(oldToken.IsCancellationRequested,"BLE disconnect did not cancel pending audio connection");
            target=0x423E3F752ECC;generation++;writer.Connected=true;
            var current=Pending();Connector(current);Status(true);
            Check(calls==3 && Pairing(),"New board was blocked by a cancelled old pairing");
            Result(old,"STALE speaker");Drain();Check(!Text().Contains("STALE"),"Old pairing completion replaced the new board status");
            Result(current,"KBEATBOX stereo");Until(()=>!Pairing());Check(Text().Contains("KBEATBOX stereo"),"New board audio status missing");

            generation++;type.GetProperty("IsAudioMeasurementBusy")!.SetValue(window,(Func<bool>)(()=>true));
            Status(true);Check(calls==3,"Pairing ran while a measurement owned the route");
            type.GetProperty("IsAudioMeasurementBusy")!.SetValue(window,(Func<bool>)(()=>false));
            type.GetProperty("BluetoothAutoPairEnabled")!.SetValue(window,(Func<bool>)(()=>false));
            Status(true);Check(calls==3 && Text().Contains("đang tắt"),"Disabled model auto-pair policy was ignored");
            type.GetProperty("BluetoothAutoPairEnabled")!.SetValue(window,(Func<bool>)(()=>true));
            generation++;var failed=Pending();Connector(failed);Status(true);
            pendingType.GetMethod("SetException",new[]{typeof(Exception)})!.Invoke(failed,new object[]{new InvalidOperationException("Windows fixture failure")});
            Until(()=>!Pairing());Check(Text().Contains("Windows fixture failure"),"Audio connection failure was hidden");
            Status(true);Check(calls==4,"A failed connection caused an automatic retry loop");
            var retry=Pending();Connector(retry);
            var retryTask=(Task)type.GetMethod("EnsureBluetoothAudioAsync",hidden)!.Invoke(window,new object[]{true})!;
            Check(calls==5,"Explicit retry did not start");Result(retry,"Recovered stereo");Until(()=>retryTask.IsCompleted);retryTask.GetAwaiter().GetResult();
            Check(Text().Contains("Recovered stereo"),"Explicit retry did not recover status");

            var manager=type.GetField("_ble",hidden)!.GetValue(window)!;
            var managerType=manager.GetType();
            managerType.GetMethod("SuppressBluetoothAudioAutoPair",hidden)!.Invoke(manager,new object[]{target});
            generation++;Status(true);
            Check(calls==5 && writer.Connected && Text().Contains("BLE vẫn được giữ"),"Post-test audio removal disconnected BLE or immediately paired audio again");
            var afterRemoval=Pending();Connector(afterRemoval);
            var explicitAudio=(Task)type.GetMethod("EnsureBluetoothAudioAsync",hidden)!.Invoke(window,new object[]{true})!;
            Check(calls==6,"Explicit audio request remained blocked after removal");
            Result(afterRemoval,"Explicit stereo");Until(()=>explicitAudio.IsCompleted);explicitAudio.GetAwaiter().GetResult();

            generation++;var closing=Pending();Connector(closing);Status(true);var closingToken=lastToken;
            window.Close();Check(closingToken.IsCancellationRequested,"Closing did not cancel pending audio connection");
            Result(closing,"AFTER CLOSE");Drain();
            Check(writer.Packets.All(packet=>packet.Length<3 || packet[2]!=4),"Audio preparation sent a speaker parameter write");
            File.WriteAllText(Path.Combine(output,"result.txt"),$"PASS {calls} injected audio preparations; no native pairing or audio playback.\n");
            Console.WriteLine("PASS BLE-ready audio connection; duplicate suppression; disconnect/new-board/late-completion guards; measurement/model policy; failure/manual retry; close cancellation. Injected Windows connector, no native pairing or speaker writes.");
        }
        finally { window.Close(); }
    }
    private static void Drain()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
    private static void Until(Func<bool> condition)
    {
        var deadline=DateTime.UtcNow.AddSeconds(8);
        while(!condition())
        {
            if(DateTime.UtcNow>deadline)throw new TimeoutException("Audio UI continuation did not finish");
            var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);Thread.Sleep(1);
        }
    }
}
