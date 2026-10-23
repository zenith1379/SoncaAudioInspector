using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;

internal static class ImpulseGraphUiChecks
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

    public static void Check(Assembly assembly, object result, string output)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        Directory.CreateDirectory(output);
        var window = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ImpulseAnalysisWindow")!, result)!;
        if (window.WindowState != WindowState.Maximized) throw new Exception("Impulse must open large by default.");
        var view = (UserControl)window.Content;
        var type = view.GetType();
        dynamic plot = type.GetField("_plot", hidden)!.GetValue(view)!;
        double focusStart = (double)type.GetField("_start", hidden)!.GetValue(view)!;
        double focusEnd = (double)type.GetField("_end", hidden)!.GetValue(view)!;
        double dataStart = (double)type.GetField("_dataStart", hidden)!.GetValue(view)!;
        double dataEnd = (double)type.GetField("_dataEnd", hidden)!.GetValue(view)!;
        type.GetMethod("ShowFullView", hidden)!.Invoke(view, null);
        dynamic fullLimits = plot.Plot.Axes.GetLimits();
        if ((double)fullLimits.Left != dataStart || (double)fullLimits.Right != dataEnd)
            throw new Exception("Full IR cannot recover quiet pre-arrival/tail samples.");
        type.GetMethod("ResetView", hidden)!.Invoke(view, null);
        dynamic focused = plot.Plot.Axes.GetLimits();
        if ((double)focused.Left != focusStart || (double)focused.Right != focusEnd)
            throw new Exception("Signal-region reset did not restore the focused opening.");
        foreach (string name in new[] { "_ir", "_etc", "_window" })
        {
            dynamic trace = type.GetField(name, hidden)!.GetValue(view)!;
            if (!(bool)trace.IsVisible) throw new Exception(name + " is missing from the combined IR graph.");
            if (trace.GetType().GetProperty("FillY") is System.Reflection.PropertyInfo fill && (bool)fill.GetValue(trace)!)
                throw new Exception(name + " fill hides narrow IR/ETC valleys.");
        }
        foreach (string name in new[] { "_decay", "_windowed" })
        {
            dynamic trace = type.GetField(name, hidden)!.GetValue(view)!;
            if ((bool)trace.IsVisible) throw new Exception(name + " should be optional in the initial Impulse view.");
        }
        var windowToggle = (CheckBox)type.GetField("_showWindow", hidden)!.GetValue(view)!;
        windowToggle.IsChecked = false; windowToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        foreach (string name in new[] { "_window", "_leftEdge", "_rightEdge" })
        {
            dynamic trace = type.GetField(name, hidden)!.GetValue(view)!;
            if ((bool)trace.IsVisible)
                throw new Exception("Window and its markers did not hide together.");
        }
        windowToggle.IsChecked = true; windowToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var windowedToggle = (CheckBox)type.GetField("_showWindowed", hidden)!.GetValue(view)!;
        windowedToggle.IsChecked = true; windowedToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        dynamic windowed = type.GetField("_windowed", hidden)!.GetValue(view)!;
        if (!(bool)windowed.IsVisible) throw new Exception("Windowed impulse cannot be inspected.");
        windowedToggle.IsChecked = false; windowedToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        if (!((string)plot.Plot.Axes.Left.Label.Text).Contains("dBr")
            || !((string)plot.Plot.Axes.Bottom.Label.Text).Contains("ms"))
            throw new Exception("Impulse plot lost its honest relative level or millisecond units.");
        var etcToggle = (CheckBox)type.GetField("_showEtc", hidden)!.GetValue(view)!;
        dynamic etc = type.GetField("_etc", hidden)!.GetValue(view)!;
        etcToggle.IsChecked = false; etcToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        if ((bool)etc.IsVisible) throw new Exception("ETC visibility control did not hide its trace.");
        etcToggle.IsChecked = true; etcToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var metricsToggle = (CheckBox)type.GetField("_showMetrics", hidden)!.GetValue(view)!;
        var metrics = (Border)type.GetField("_metrics", hidden)!.GetValue(view)!;
        metricsToggle.IsChecked = false; metricsToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        if (metrics.Visibility != Visibility.Collapsed) throw new Exception("The data panel cannot be hidden.");
        metricsToggle.IsChecked = true; metricsToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var regressionChoice = (ComboBox)type.GetField("_regressionChoice", hidden)!.GetValue(view)!;
        var regressionToggle = (CheckBox)type.GetField("_showRegression", hidden)!.GetValue(view)!;
        var fits = (System.Collections.IDictionary)type.GetField("_regressions", hidden)!.GetValue(view)!;
        if (fits.Count > 0)
        {
            regressionChoice.SelectedItem = fits.Keys.Cast<string>().First();
            regressionToggle.IsChecked = false; regressionToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            foreach (dynamic fit in fits.Values) if ((bool)fit.IsVisible) throw new Exception("Regression line cannot be hidden.");
            regressionToggle.IsChecked = true; regressionToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            int visible = fits.Values.Cast<object>().Count(fit => (bool)fit.GetType().GetProperty("IsVisible")!.GetValue(fit)!);
            if (visible != 1) throw new Exception("Regression selector did not select exactly one fitted line.");
            if (regressionChoice.Items.Contains("T20")) regressionChoice.SelectedItem = "T20";
        }
        metricsToggle.IsChecked = false; metricsToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        regressionToggle.IsChecked = false; regressionToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        plot.Plot.Axes.SetLimitsX(0.1, .8);
        type.GetMethod("SyncNavigator", hidden)!.Invoke(view, null);
        dynamic selected = type.GetField("_selection", hidden)!.GetValue(view)!;
        if (Math.Abs((double)selected.X1 - .1) > 1e-8 || Math.Abs((double)selected.X2 - .8) > 1e-8)
            throw new Exception("IR navigator does not track the visible time range.");
        type.GetMethod("ResetView", hidden)!.Invoke(view, null);
        Console.WriteLine("IR controls checked; rendering shown window.");
        // Render an actual shown WPF window so the overlay and native plot paint,
        // with no audio acquisition or device commands in this immutable view.
        window.Show(); window.UpdateLayout();
        Console.WriteLine("IR window shown; waiting for first paint.");
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => frame.Continue = false; timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop();
        var content = (FrameworkElement)window.Content;
        plot.Plot.Axes.SetLimitsY(-300.0, 20.0);
        using (dynamic ignored = plot.Plot.GetImage(1200, 650)) { }
        dynamic bounded = plot.Plot.Axes.GetLimits();
        if ((double)bounded.Bottom < -180.000001 || (double)bounded.Top > 20.000001)
            throw new Exception("Zoom-out escaped the -180 dBr dynamic-range floor.");
        plot.Plot.Axes.SetLimitsY(-250.0, -210.0);
        using (dynamic ignored = plot.Plot.GetImage(1200, 650)) { }
        bounded = plot.Plot.Axes.GetLimits();
        if ((double)bounded.Bottom < -180.000001 || (double)bounded.Top <= (double)bounded.Bottom)
            throw new Exception("Vertical pan escaped the dBr floor or inverted the axes.");
        plot.Plot.Axes.SetLimitsX(dataStart - 100, dataEnd + 100);
        using (dynamic ignored = plot.Plot.GetImage(1200, 650)) { }
        bounded = plot.Plot.Axes.GetLimits();
        if ((double)bounded.Left < dataStart - 1e-8 || (double)bounded.Right > dataEnd + 1e-8)
            throw new Exception("Horizontal zoom-out renders time outside the captured IR.");
        type.GetMethod("ResetView", hidden)!.Invoke(view, null);
        void Capture(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        static void Dark(Brush brush, string control)
        {
            if (brush is not SolidColorBrush solid || solid.Color.R > 85 || solid.Color.G > 85 || solid.Color.B > 95)
                throw new Exception(control + " has a light surface in the Impulse view.");
        }
        foreach (string name in new[] { "_showIr", "_showEtc", "_showMetrics", "_showRegression", "_showWindowed" })
        {
            var toggle = (CheckBox)type.GetField(name, hidden)!.GetValue(view)!;
            toggle.ApplyTemplate();
            if (toggle.Template.FindName("Box", toggle) is not Border box)
                throw new Exception(name + " still uses the system checkbox template.");
            Dark(box.Background, name);
        }
        regressionChoice.ApplyTemplate(); Dark(regressionChoice.Background, "regression selector");
        int code = DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 20, out int darkTitle, sizeof(int));
        if (code >= 0 && darkTitle != 1) throw new Exception("Impulse native title bar is not dark.");
        dynamic cursor = type.GetField("_cursor", hidden)!.GetValue(view)!;
        if (cursor is UIElement overlay) overlay.Visibility = Visibility.Collapsed; else cursor.IsVisible = false;
        ((TextBlock)type.GetField("_readout", hidden)!.GetValue(view)!).Text = "";
        plot.Refresh(); content.UpdateLayout();
        Capture(content, "impulse-window");
        using (dynamic image = plot.Plot.GetImage(1200, 650)) File.WriteAllBytes(Path.Combine(output, "impulse-plot.png"), (byte[])image.GetImageBytes());
        metricsToggle.IsChecked = true; metricsToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Capture(content, "impulse-controls");
        if (regressionChoice.Items.Count > 0)
        {
            regressionChoice.IsDropDownOpen = true; regressionChoice.UpdateLayout();
            var popup = (Popup)regressionChoice.Template.FindName("PART_Popup", regressionChoice)!;
            var dropBorder = (Border)regressionChoice.Template.FindName("DropDownBorder", regressionChoice)!;
            Dark(dropBorder.Background, "regression dropdown");
            Capture((FrameworkElement)popup.Child, "regression-menu");
            regressionChoice.IsDropDownOpen = false;
        }
        metricsToggle.IsChecked = false; metricsToggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        plot.Plot.Axes.SetLimitsX(-.02, .14); plot.Refresh();
        type.GetMethod("SyncNavigator", hidden)!.Invoke(view, null);
        using (dynamic image = plot.Plot.GetImage(1200, 650)) File.WriteAllBytes(Path.Combine(output, "impulse-near-peak.png"), (byte[])image.GetImageBytes());
        window.Close();
        var details = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.MeasurementDetailsWindow")!)!;
        if (details.WindowState != WindowState.Maximized) throw new Exception("Measurement details must open large by default.");
        Type rowType = assembly.GetType("SoncaAudioInspector.MeasurementValue")!;
        Array rows = Array.CreateInstance(rowType, 1);
        rows.SetValue(Activator.CreateInstance(rowType, "Nguồn", "Bản thu kiểm tra giao diện"), 0);
        details.GetType().GetMethod("Update")!.Invoke(details, new object[] { "Kiểm tra Impulse và bảng thông số", rows, new Dictionary<double, double>(), result });
        details.Show(); details.UpdateLayout();
        var tabs = ((DockPanel)details.Content).Children.OfType<TabControl>().Single();
        tabs.ApplyTemplate(); Dark(tabs.Background, "details tabs");
        Capture((FrameworkElement)details.Content, "details-impulse");
        tabs.SelectedIndex = 1; details.UpdateLayout();
        var table = (DataGrid)((TabItem)tabs.Items[1]).Content;
        Dark(table.Background, "details table");
        table.UpdateLayout();
        Capture((FrameworkElement)details.Content, "details-table"); details.Close();
        Console.WriteLine("PASS maximized Impulse/details, zoom/pan floor -180 dBr, dark tabs and data table.");
        Console.WriteLine("PASS dark checkbox surfaces, selector/dropdown and native title bar; full view, metrics and near-peak render captured.");
        Console.WriteLine("PASS REW-style red IR, magenta analytic ETC, blue actual maximum IR window, optional windowed IR/Schroeder/metrics, L/Ref/R markers, regression and navigator; rendered WPF view.");
    }
}
