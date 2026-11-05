using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class BriefP3UiChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Hidden)?.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Hidden)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
    private static object? Get(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner);
    private static void Put(object owner, string name, object? value) => owner.GetType().GetProperty(name)!.SetValue(owner, value);

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var value in Tree(child)) yield return value;
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in Visuals(VisualTreeHelper.GetChild(root, i))) yield return value;
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Tree(root).OfType<T>().Single(e => e.Name == name);

    private static void Click(ButtonBase button) =>
        typeof(ButtonBase).GetMethod("OnClick", Hidden)!.Invoke(button, null);

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void SaveScreenshot(FrameworkElement element, string path, int width = 1100, int height = 750)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        Drain();
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void Run(Assembly assembly, string output, string? screenshotDir = null)
    {
        screenshotDir ??= Path.Combine(output, "screenshots");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(screenshotDir);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); }
        Type Type(string name) => assembly.GetType("SoncaAudioInspector." + name)!;
        object Read(string name, string json) => JsonSerializer.Deserialize(json, Type(name))!;
        IList Tests(Window owner) => (IList)Get(Get(Get(Field(owner, "_draft")!, "testItems")!, "InOut")!, "Tests")!;

        Window? legacyEditor = null;
        Window? editor = null;

        try
        {
            // 1. Kiểm tra mở cửa sổ không tự đánh số lại trên legacyEditor (IO001, IO003, IO005)
            var legacyModel = Read("ModelConfig", """
                {
                  "model": "Legacy Model",
                  "testItems": {
                    "InOut": {
                      "Devices": { "Input": { "OUT": "mock-out" }, "Output": { "IN": "mock-in" } },
                      "Tests": [
                        { "id": "IO001", "name": "L1", "PlaybackOut": "OUT", "RecordingIn": "IN", "Enable": true },
                        { "id": "IO003", "name": "L2", "PlaybackOut": "OUT", "RecordingIn": "IN", "Enable": true },
                        { "id": "IO005", "name": "L3", "PlaybackOut": "OUT", "RecordingIn": "IN", "Enable": true }
                      ]
                    }
                  }
                }
                """);
            legacyEditor = (Window)Activator.CreateInstance(Type("ModelConfigEditorWindow"), legacyModel)!;
            legacyEditor.ShowInTaskbar = false;
            legacyEditor.ShowActivated = false;
            legacyEditor.Show();
            legacyEditor.UpdateLayout();
            Drain();

            var legacyTests = Tests(legacyEditor);
            Check(Get(legacyTests[0]!, "id")!.ToString() == "IO001" &&
                  Get(legacyTests[1]!, "id")!.ToString() == "IO003" &&
                  Get(legacyTests[2]!, "id")!.ToString() == "IO005",
                  "Legacy editor does not auto-renumber on open; keeps IO001, IO003, IO005");

            var legacyBtnRenumber = Named<Button>(legacyEditor, "BtnRenumberTests");
            Click(legacyBtnRenumber);
            Drain();

            Check(Get(legacyTests[0]!, "id")!.ToString() == "IO001" && Get(legacyTests[0]!, "ReferenceTestId") == null,
                  "Legacy L1 after renumber is IO001 with null ReferenceTestId");
            Check(Get(legacyTests[1]!, "id")!.ToString() == "IO002" && Get(legacyTests[1]!, "ReferenceTestId")!.ToString() == "IO003",
                  "Legacy L2 after renumber is IO002 with ReferenceTestId IO003");
            Check(Get(legacyTests[2]!, "id")!.ToString() == "IO003" && Get(legacyTests[2]!, "ReferenceTestId")!.ToString() == "IO005",
                  "Legacy L3 after renumber is IO003 with ReferenceTestId IO005");

            legacyEditor.Close();
            // 2. Trên editor có 4 bài [IO001, IO002, IO003, IO004]
            var model = Read("ModelConfig", """
                {
                  "model": "P3 sequence demo",
                  "testItems": {
                    "InOut": {
                      "Devices": {
                        "Input": { "OUT": "mock-out" },
                        "Output": { "IN": "mock-in" }
                      },
                      "Tests": [
                        { "id": "IO001", "name": "Đo tầng 1 Mic", "PlaybackOut": "OUT", "RecordingIn": "IN", "Enable": true },
                        { "id": "IO002", "name": "Đo tầng 2 Line", "PlaybackOut": "OUT", "RecordingIn": "IN", "Enable": true },
                        { "id": "IO003", "name": "Đo tầng 3 BT", "PlaybackOut": "OUT", "RecordingIn": "IN", "Enable": false },
                        { "id": "IO004", "name": "Đo tầng 4 Acoustic", "PlaybackOut": "OUT", "RecordingIn": "IN", "Enable": true }
                      ]
                    }
                  }
                }
                """);
            editor = (Window)Activator.CreateInstance(Type("ModelConfigEditorWindow"), model)!;
            editor.ShowActivated = false;
            editor.ShowInTaskbar = false;
            editor.Show();
            editor.UpdateLayout();
            Drain();

            var testList = Named<ListBox>(editor, "ConfigTests");
            Check(testList != null, "ListBox ConfigTests exists");
            Check(testList!.ItemTemplate != null, "ListBox ConfigTests has DataTemplate");
            Check(testList.AllowDrop, "ListBox ConfigTests has AllowDrop enabled");

            var btnUp = Named<Button>(editor, "BtnMoveTestUp");
            var btnDown = Named<Button>(editor, "BtnMoveTestDown");
            var btnRenumber = Named<Button>(editor, "BtnRenumberTests");
            Check(btnUp != null && btnUp.ToolTip?.ToString()?.Contains("Alt+Up") == true, "BtnMoveTestUp exists with Alt+Up tooltip");
            Check(btnDown != null && btnDown.ToolTip?.ToString()?.Contains("Alt+Down") == true, "BtnMoveTestDown exists with Alt+Down tooltip");
            Check(btnRenumber != null && btnRenumber.ToolTip?.ToString()?.Contains("IO001") == true, "BtnRenumberTests exists with IO001 renumber tooltip");

            // Xuất ảnh editor-sequence-default.png
            string defaultPngPath = Path.Combine(screenshotDir, "editor-sequence-default.png");
            SaveScreenshot(editor, defaultPngPath);
            Check(File.Exists(defaultPngPath), "Rendered editor-sequence-default.png screenshot");

            // Kiểm tra TestIdBox.IsReadOnly == true
            var idBox = Named<TextBox>(editor, "TestIdBox");
            Check(idBox != null && idBox.IsReadOnly, "TestIdBox exists and is read-only");

            // Kiểm tra click CheckBox "Bật" inline cập nhật test.Enable và JSON
            var draftTests = Tests(editor);
            testList.UpdateLayout();
            Drain();
            var item0 = (ListBoxItem)testList.ItemContainerGenerator.ContainerFromIndex(0);
            if (item0 == null)
            {
                testList.ApplyTemplate();
                testList.UpdateLayout();
                Drain();
                item0 = (ListBoxItem)testList.ItemContainerGenerator.ContainerFromIndex(0);
            }
            Check(item0 != null, "ListBoxItem at index 0 generated");
            var inlineCb0 = Visuals(item0!).OfType<CheckBox>().FirstOrDefault(cb => cb.Content?.ToString() == "Bật");
            Check(inlineCb0 != null, "Inline CheckBox 'Bật' found in ListBoxItem 0");

            Click(inlineCb0!);
            Drain();
            Check(Get(draftTests[0]!, "Enable")?.Equals(false) == true, "Click inline CheckBox sets test.Enable = false");
            var jsonBox = Named<TextBox>(editor, "ModelJson");
            Check(!JsonDocument.Parse(jsonBox.Text).RootElement.GetProperty("testItems").GetProperty("InOut").GetProperty("Tests")[0].GetProperty("enable").GetBoolean(), "Inline CheckBox click updates JSON projection");

            // Đổi CheckRunInAutoTest ở panel phải cập nhật lại CheckBox inline
            var checkRun = Named<CheckBox>(editor, "CheckRunInAutoTest");
            Check(checkRun != null, "CheckRunInAutoTest exists in right panel");
            checkRun!.IsChecked = true;
            Drain();
            inlineCb0 = Visuals((ListBoxItem)testList.ItemContainerGenerator.ContainerFromIndex(0)).OfType<CheckBox>().Single(cb => cb.Content?.ToString() == "Bật");
            Check(inlineCb0.IsChecked == true, "Changing CheckRunInAutoTest in right panel updates inline CheckBox");
            Check(Get(draftTests[0]!, "Enable")?.Equals(true) == true, "Right panel check updates test.Enable = true");

            // Call the same key handler used by PreviewKeyDown, with explicit modifiers instead of OS input.
            testList.SelectedIndex = 1; object selectedByKey = testList.SelectedItem;
            Check((bool)Call(editor, "HandleSequenceKey", Key.Up, ModifierKeys.Alt)! && testList.SelectedIndex == 0 && ReferenceEquals(testList.SelectedItem, selectedByKey), "Alt+Up handler moves the selected object and keeps selection");
            Check((bool)Call(editor, "HandleSequenceKey", Key.Down, ModifierKeys.Alt)! && testList.SelectedIndex == 1 && ReferenceEquals(testList.SelectedItem, selectedByKey), "Alt+Down handler restores selected object position");
            Check(!(bool)Call(editor, "HandleSequenceKey", Key.Up, ModifierKeys.Control)!, "Non-Alt shortcut is ignored");
            // Chọn bài thứ 4 (IO004), bấm BtnMoveTestUp 3 lần đưa lên đầu (index 0)
            testList.SelectedIndex = 3;
            Drain();
            Click(btnUp!); Drain();
            Click(btnUp!); Drain();
            Click(btnUp!); Drain();

            Check(testList.SelectedIndex == 0, "Selected item remains at index 0 after 3 MoveTestUp");
            Check(Get(draftTests[0]!, "name")!.ToString() == "Đo tầng 4 Acoustic", "Item at index 0 is Đo tầng 4 Acoustic");
            Check(Get(draftTests[0]!, "id")!.ToString() == "IO001", "Moved test id updated to IO001");
            Check(Get(draftTests[0]!, "ReferenceTestId")!.ToString() == "IO004", "Moved test ReferenceTestId is IO004");

            var idBoxAfter = Named<TextBox>(editor, "TestIdBox");
            Check(idBoxAfter.Text == "IO001", "TestIdBox shows IO001");
            bool noteFound = Tree(editor).OfType<TextBlock>().Any(tb => tb.Text.Contains("LINE CHUẨN gốc: IO004"));
            Check(noteFound, "Note 'LINE CHUẨN gốc: IO004' is displayed in form");

            bool parsedOk = false;
            try
            {
                var parseMethod = Type("ModelConfigEditorWindow").GetMethod("ParseModel", BindingFlags.Public | BindingFlags.Static)!;
                parseMethod.Invoke(null, ["P3 sequence demo", jsonBox.Text]);
                parsedOk = true;
            }
            catch { }
            Check(parsedOk, "Reordered JSON is valid and passes ParseModel");

            // Xuất ảnh editor-sequence-reordered.png
            string reorderedPngPath = Path.Combine(screenshotDir, "editor-sequence-reordered.png");
            SaveScreenshot(editor, reorderedPngPath);
            Check(File.Exists(reorderedPngPath), "Rendered editor-sequence-reordered.png screenshot");

            // Kiểm tra bấm 'Edit' JSON: _json.IsReadOnly == false, _formLayout.IsEnabled == false,
            // và gọi MoveSelectedTestDown() / RenumberTests() không làm thay đổi thứ tự
            var editJsonBtn = Named<Button>(editor, "EditJson");
            Click(editJsonBtn);
            Drain();

            var jsonField = (TextBox)Field(editor, "_json")!;
            var formLayoutField = (FrameworkElement)Field(editor, "_formLayout")!;
            Check(!jsonField.IsReadOnly, "JSON editor is not read-only after clicking Edit");
            Check(!formLayoutField.IsEnabled, "Form layout is disabled when editing JSON directly");

            string Order() => string.Join(",", draftTests.Cast<object>().Select(t => Get(t, "name")!.ToString()));
            string orderBefore = Order(); string jsonBefore = jsonField.Text;
            editor.GetType().GetMethod("MoveSelectedTestDown", BindingFlags.Public | BindingFlags.Instance)!.Invoke(editor, null);
            Drain();
            string orderAfterMove = Order();
            Check(orderBefore == orderAfterMove, "MoveSelectedTestDown is blocked while editing JSON");

            editor.GetType().GetMethod("RenumberTests", BindingFlags.Public | BindingFlags.Instance)!.Invoke(editor, null);
            Drain();
            string orderAfterRenumber = Order();
            Check(orderBefore == orderAfterRenumber && jsonBefore == jsonField.Text, "RenumberTests is blocked while editing JSON");
            Check(!(bool)Call(editor, "HandleSequenceKey", Key.Down, ModifierKeys.Alt)! && Order() == orderBefore, "Alt+Down handler is blocked while editing JSON");
        }
        finally
        {
            try { editor?.Close(); } catch { }
            try { legacyEditor?.Close(); } catch { }
        }

        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P3 WPF assertions; screenshots generated."));
        Console.WriteLine($"PASS {checks.Count} P3 WPF assertions and sequence editor PNGs; no audio/BLE/server.");
    }
}
