using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
namespace EyesOff
{
    internal static class ComponentTests
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr window);
        static int count;
        static string output;
        static object Field(object obj, string name) { return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj); }
        static void Put(object obj, string name, object value) { obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value); }
        static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args); }
        static void Check(bool result, string label)
        { if (!result) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS " + label); }
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "previews");
                Directory.CreateDirectory(output);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Check(LaunchOptions.Has(new string[] { "--OFF" }, "--off"), "case-insensitive launch options");
                Check(!LaunchOptions.Has(new string[] { "--office" }, "--off"), "exact argument matching");
                Check(SettingsStore.StartupCommand(@"D:\Folder Name\EyesOff.exe") == "\"D:\\Folder Name\\EyesOff.exe\" --tray", "quoted startup command");
                using (SingleInstance primary = new SingleInstance())
                {
                    Check(primary.IsOwner, "first instance owns lock");
                    using (SingleInstance secondary = new SingleInstance())
                    {
                        Check(!secondary.IsOwner, "duplicate instance cannot own lock");
                        secondary.RequestShow();
                        Check(((System.Threading.EventWaitHandle)Field(primary, "showEvent")).WaitOne(100), "show IPC request retained");
                        secondary.RequestOff();
                        Check(((System.Threading.EventWaitHandle)Field(primary, "offEvent")).WaitOne(100), "screen-off IPC routing without actual screen-off");
                        Check(!((System.Threading.EventWaitHandle)Field(primary, "showEvent")).WaitOne(0), "IPC events auto reset");
                    }
                }
                using (SingleInstance again = new SingleInstance()) Check(again.IsOwner, "instance lock released on exit");
                Stopwatch clock = Stopwatch.StartNew();
                using (MainForm form = new MainForm(false, true))
                {
                    clock.Stop(); Console.WriteLine("Component construction: " + clock.ElapsedMilliseconds + " ms (not a launch benchmark)");
                    Check(Field(form, "model") == null, "home does not scan processes");
                    Check(!(bool)Field(form, "busy"), "home is immediately interactive");
                    Check(Field(form, "tray") == null, "test mode creates no tray icon");
                    Layout(form); VerifyLayout(form, "home");
                    Save(form, "home.png");
                    Check(!IsWindowVisible(form.Handle), "design-time rendering never shows native window");
                    form.ClientSize = new Size(1004, 681); Layout(form); VerifyLayout(form, "minimum home");
                    form.ClientSize = new Size(1100, 740); Layout(form);
                    Call(form, "ShowPage", 2); Layout(form); VerifyLayout(form, "preferences");
                    Check(!((CheckBox)Field(form, "autoStartCheck")).Checked || SettingsStore.StartWithWindows, "startup checkbox reflects existing state");
                    Save(form, "preferences.png");
                    Put(form, "model", Fixture()); Call(form, "ShowPage", 1); Call(form, "RenderRows"); Layout(form);
                    ListView list = (ListView)Field(form, "lv");
                    Check(list.Items.Count == 3, "fixture apps render");
                    Check(list.Columns[2].Width == 0 && list.Columns[6].Width == 0, "secondary info stays in details");
                    int total = 0; foreach (ColumnHeader c in list.Columns) total += c.Width;
                    Check(total <= list.ClientSize.Width, "simple view fits viewport");
                    Check(!((Button)Field(form, "btnEndChecked")).Enabled, "no close action before selection");
                    foreach (ListViewItem item in list.Items)
                    {
                        RowModel row = (RowModel)item.Tag;
                        if (row.App.Status != AppStatus.Background) Check(!row.Checkable && !row.Endable, "protected fixture stays non-actionable");
                    }
                    RowModel selectable = null;
                    foreach (ListViewItem item in list.Items) if (((RowModel)item.Tag).App.Status == AppStatus.Background) selectable = (RowModel)item.Tag;
                    selectable.Checked = true; Call(form, "RefreshUiState");
                    Check(((Button)Field(form, "btnEndChecked")).Enabled, "checked row enables close without executing it");
                    selectable.Checked = false; Call(form, "RefreshUiState");
                    ((TextBox)Field(form, "txtSearch")).Text = "music";
                    Check(list.Items.Count == 1, "search filters publisher");
                    ((TextBox)Field(form, "txtSearch")).Text = "";
                    Save(form, "apps.png");
                    Call(form, "SetMode", true); Check(list.Columns.Count == 10, "advanced view retained");
                    Call(form, "SetMode", false);
                    Call(form, "ShowPage", 0); form.QueueScreenOff();
                    Check(!((System.Windows.Forms.Timer)Field(form, "offTimer")).Enabled, "test mode never schedules monitor power call");
                    Check(!((Button)Field(form, "btnScreenOff")).Enabled, "deferred screen-off updates button state");
                    Call(form, "HideToTray"); Check(!form.Visible, "tray hide path does not show a window");
                    if (SettingsStore.CloseToTray)
                    {
                        FormClosingEventArgs close = new FormClosingEventArgs(CloseReason.UserClosing, false);
                        Call(form, "OnFormClosing", close); Check(close.Cancel, "close-to-tray intercepts user close");
                    }
                    Put(form, "exitRequested", true);
                    FormClosingEventArgs exit = new FormClosingEventArgs(CloseReason.UserClosing, false);
                    Call(form, "OnFormClosing", exit); Check(!exit.Cancel, "explicit tray exit is not intercepted");
                }
                using (MainForm hidden = new MainForm(true, true))
                {
                    Check((bool)Field(hidden, "suppressFirstShow"), "tray launch starts hidden");
                    Check(Field(hidden, "model") == null, "tray launch does not scan");
                    Call(hidden, "SetVisibleCore", true);
                    Check(!hidden.Visible && !IsWindowVisible(hidden.Handle), "first visible request suppressed for tray launch");
                }
                Console.WriteLine("ALL PASS: " + count + " checks. No monitor power, process termination, startup writes or desktop capture.");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        static AppModel.Model Fixture()
        {
            AppModel.Model model = new AppModel.Model();
            model.Apps.Add(App("浏览器", "Browser Company", AppStatus.InUse, 524));
            model.Apps.Add(App("音乐播放器", "Music Studio", AppStatus.Background, 186));
            model.Apps.Add(App("同步助手", "Cloud Tools", AppStatus.UserProtected, 84));
            return model;
        }
        static AppEntry App(string name, string publisher, AppStatus status, int mb)
        {
            AppEntry app = new AppEntry(); app.Key = name; app.DisplayName = name; app.Publisher = publisher;
            app.Desc = "测试示例，不是真实进程"; app.Status = status; app.StatusText = status == AppStatus.InUse ? "正在使用" : status == AppStatus.Background ? "后台运行" : "用户保护";
            app.TotalMem = (long)mb * 1048576; app.RiskHint = "请先保存内容";
            app.Procs.Add(new ProcSnapshot { Pid = 987654, Name = "fixture", ExePath = @"D:\Example\fixture.exe", Memory = app.TotalMem,
                Status = status, CanEnd = status == AppStatus.Background, HasWindow = status == AppStatus.InUse });
            return app;
        }
        static void Layout(Control control)
        {
            control.CreateControl(); control.PerformLayout();
            foreach (Control child in control.Controls) Layout(child);
            control.PerformLayout();
        }
        static void VerifyLayout(MainForm form, string label)
        {
            Button off = (Button)Field(form, "btnScreenOff");
            Console.WriteLine(label + ": hero=" + off.Parent.ClientRectangle + ", off=" + off.Bounds);
            Check(off.Top >= 225 && off.Bottom <= off.Parent.ClientSize.Height, label + " primary button has breathing room");
            Check(off.Left >= 0 && off.Right <= off.Parent.ClientSize.Width, label + " primary button inside card");
            Button menu = (Button)Field(form, "btnMenu");
            Check(menu.Left >= 0 && menu.Right <= menu.Parent.ClientSize.Width, label + " menu inside header");
        }
        static void Save(MainForm form, string name)
        {
            // Design-time render only: set the managed visibility bit, never Show/ShowWindow.
            // The native top-level window remains hidden and no desktop pixels are read.
            IntPtr nativeHandle = form.Handle; // Create the root HWND while managed/native visibility is false.
            if (IsWindowVisible(nativeHandle)) throw new Exception("Preview root must be hidden");
            MethodInfo setState = typeof(Control).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic);
            setState.Invoke(form, new object[] { 2, true });
            try
            {
                Layout(form);
                if (IsWindowVisible(nativeHandle)) throw new Exception("Preview must remain hidden");
                using (Bitmap image = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                    image.Save(Path.Combine(output, name), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            finally { setState.Invoke(form, new object[] { 2, false }); }
        }
    }
}
