// ============================================================================
//  EyesOff.cs —— v2.1：熄屏首页 / 按需清理 / 单实例快捷启动
// ============================================================================
//  本版本在 v1 基础上做了产品化改造：
//    · v2.1 启动默认进入熄屏首页，应用清理按需加载，
//      v2.1 默认进入熄屏首页；进入清理页时才扫描应用。
//    · 一个应用（同一 exe 路径）的多个进程合并为一行，显示图标、名称、发布者、
//      用途、状态、内存、进程数量与风险提示。
//    · 应用分为四类状态：正在使用（已保护）/ 后台运行 / 用户保护 / 受保护 / 未知。
//      "正在使用""受保护""未知"默认不可清理；只有"可清理应用"区可勾选批量结束。
//    · 结束流程安全化：先 CloseMainWindow() 正常关闭并等待 3 秒；未退出的进程
//      默认【不】强制结束——只有用户在设置中开启"允许强制结束"并在确认框中
//      再次勾选确认后，才允许 Kill()。
//    · 新增 protect.txt 用户保护名单（应用名或 exe 名）、install_shortcut.bat
//      桌面/开始菜单快捷方式安装脚本。
//
//  安全硬约束（不可违反，全部代码围绕它展开）：
//    1. 只处理当前用户会话的进程；
//    2. PID ≤ 100 的进程一律不处理；
//    3. 本工具自身及其父进程链一律不处理；
//    4. 可执行文件位于系统目录（C:\Windows\ 及子目录）的进程一律不结束；
//    5. 硬编码黑名单进程（explorer/svchost/lsass/dwm 等）不出现在简洁模式、
//       任何情况下不得结束；
//    6. 不能仅凭内存占用高判定可清理；
//    7. 不能仅凭没有可见窗口判定安全；
//    8. 路径/发布者/会话信息读取失败 → 标记未知/受保护并禁止批量结束；
//    9. 所有批量操作必须先展示应用清单并弹确认框；
//   10. 刷新后已退出的进程不再显示（模型整体重建，不沿用旧选择）；
//   11. 结束前必须按 PID 重新打开进程并核对进程名与完整路径——防止 PID 被
//       系统复用后误杀"顶替者"。
//
//  编译（见 build.bat，Windows 自带 csc.exe，零第三方依赖）：
//    csc.exe /codepage:65001 /target:winexe /out:EyesOff.exe
//        /r:System.Windows.Forms.dll /r:System.Drawing.dll
//        /r:System.Core.dll /r:System.dll EyesOff.cs
//
//  语言版本：C# 5（系统自带 csc.exe 支持的上限），未使用任何新语法。
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Security.Principal;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("一键熄屏 + 后台清理")]
[assembly: AssemblyProduct("EyesOff")]
[assembly: AssemblyDescription("一键熄屏 + 应用清理小工具（普通用户版）")]
[assembly: AssemblyVersion("2.1.0.0")]

namespace EyesOff
{
    // ========================================================================
    // 程序入口
    // ========================================================================
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (LaunchOptions.Has(args, "/selftest")) { SelfTest.Run(); return; }
            if (LaunchOptions.Has(args, "--install-shortcuts"))
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "shortcut_result.txt"), ShortcutHelper.EnsureShortcuts(), Encoding.UTF8);
                return;
            }
            bool off = LaunchOptions.Has(args, "--off");
            bool hidden = off || LaunchOptions.Has(args, "--tray");
            using (SingleInstance instance = new SingleInstance())
            {
                if (!instance.IsOwner)
                {
                    if (off) instance.RequestOff();
                    else if (!LaunchOptions.Has(args, "--tray")) instance.RequestShow();
                    return;
                }
                try { NativeMethods.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (MainForm form = new MainForm(hidden))
                {
                    IntPtr handle = form.Handle;
                    instance.Attach(form);
                    if (off) form.QueueScreenOff();
                    Application.Run(form);
                }
            }
        }
    }
    internal static class LaunchOptions
    {
        public static bool Has(string[] args, string option)
        {
            foreach (string arg in args)
                if (string.Equals(arg, option, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
    // Login-session/user-scoped IPC. Events retain requests during startup.
    internal sealed class SingleInstance : IDisposable
    {
        readonly Mutex mutex;
        readonly EventWaitHandle showEvent, offEvent;
        RegisteredWaitHandle showWait, offWait;
        public readonly bool IsOwner;
        public SingleInstance()
        {
            string id = @"Local\EyesOff.2." + WindowsIdentity.GetCurrent().User.Value;
            bool created;
            mutex = new Mutex(true, id + ".Instance", out created); IsOwner = created;
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".Show");
            offEvent = new EventWaitHandle(false, EventResetMode.AutoReset, id + ".Off");
        }
        public void RequestShow() { showEvent.Set(); }
        public void RequestOff() { offEvent.Set(); }
        public void Attach(MainForm form)
        {
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate(object state, bool timedOut)
            { Dispatch(form, false); }, null, Timeout.Infinite, false);
            offWait = ThreadPool.RegisterWaitForSingleObject(offEvent, delegate(object state, bool timedOut)
            { Dispatch(form, true); }, null, Timeout.Infinite, false);
        }
        static void Dispatch(MainForm form, bool off)
        {
            try
            {
                if (form.IsDisposed || !form.IsHandleCreated) return;
                form.BeginInvoke((MethodInvoker)delegate
                {
                    if (form.IsDisposed) return;
                    if (off) form.QueueScreenOff(); else form.Present();
                });
            }
            catch (InvalidOperationException) { }
        }
        public void Dispose()
        {
            if (showWait != null) showWait.Unregister(null);
            if (offWait != null) offWait.Unregister(null);
            showEvent.Dispose(); offEvent.Dispose();
            if (IsOwner) mutex.ReleaseMutex(); mutex.Dispose();
        }
    }

    // ========================================================================
    // 数据模型
    // ========================================================================

    // 应用识别结果。Level 含义：
    //   2 = 高可信（来自 ProductName 或 FileDescription，发布者写的元数据）
    //   1 = 一般可信（有 CompanyName，用"公司名 + exe 名"作名称）
    //   0 = 不可识别（只有 exe 文件名本身）→ 显示为"未知应用"
    // 为什么这样分级：不能仅凭 exe 文件名猜测软件用途（安全规则），
    // 文件名可以随便改，而版本信息里的产品名/描述是发布者写入的，可靠得多。
    internal class IdentityInfo
    {
        public string Name;        // 应用显示名
        public string Publisher;   // 发布者
        public string Desc;        // 用途或说明
        public int Level;          // 0/1/2，见上
        public string Source;      // 识别来源（供详情查看）
    }

    // 单个进程的快照（扫描时一次性取齐，界面与结束流程都基于快照工作）
    internal class ProcSnapshot
    {
        public int Pid;
        public string Name;              // 原始进程名（不含 .exe）
        public string ExePath;           // 完整路径（可能为 null = 读不到）
        public long Memory;
        public int SessionId;
        public bool HasWindow;           // 扫描时刻是否有可见顶层窗口
        public bool IsForeground;        // 扫描时刻是否是前台窗口所属进程
        public AppStatus Status;         // 分类结果
        public string Reason;            // 分类原因（受保护/未知时给人看）
        public IdentityInfo Id;          // 应用识别结果
        public bool CanEnd;              // 是否允许被结束（独立于显示状态的安全开关）
    }

    // 应用状态。注意：显示状态与"能否结束"是两回事——
    // 例如记事本（exe 在系统目录）有窗口时显示为"正在使用（已保护）"，
    // 状态上是"正在使用"，但 CanEnd 仍为 false（系统目录规则不可违反）。
    internal enum AppStatus
    {
        InUse = 0,          // 正在使用（有可见窗口/前台窗口）
        UserProtected = 1,  // 命中 protect.txt 用户保护名单
        Background = 2,     // 后台运行的可识别用户应用（可清理候选）
        Unknown = 3,        // 无法可靠识别
        Protected = 4,      // 受保护（系统目录/其他会话/父进程链/路径不明等）
        Blacklisted = 5     // 硬编码黑名单（任何界面都不显示，任何操作都拒绝）
    }

    // 一个"应用"= 同一 exe 路径的若干进程合并后的展示单元
    internal class AppEntry
    {
        public string Key;             // 归一化 exe 路径（合并键）
        public string DisplayName;     // 按识别优先级得出的应用名
        public string Publisher;       // 发布者
        public string Desc;            // 用途或说明
        public int Level;              // 识别可信级别（0/1/2）
        public Icon AppIcon;           // 应用图标（读取失败用系统默认）
        public List<ProcSnapshot> Procs = new List<ProcSnapshot>();
        public long TotalMem;          // 所有进程内存之和
        public AppStatus Status;       // 聚合状态（取"最需要保护"的一档）
        public string StatusText;      // 当前状态（界面列）
        public string RiskHint;        // 风险提示（界面列）
        public string IdentifySource;  // 识别来源（详情用）

        public bool HasWindow          // 该应用是否有任一进程带可见窗口
        {
            get { foreach (ProcSnapshot p in Procs) if (p.HasWindow || p.IsForeground) return true; return false; }
        }
        public bool AllCanEnd          // 是否所有进程都允许结束（批量结束的前提）
        {
            get { foreach (ProcSnapshot p in Procs) if (!p.CanEnd) return false; return Procs.Count > 0; }
        }
    }

    // 一次结束操作的"任务"：某个应用（或高级模式下它被勾选的那部分进程）
    internal class EndTask
    {
        public string AppName;
        public string StatusText;      // 确认框里显示的状态（后台运行/正在使用/未知应用）
        public List<ProcSnapshot> Procs = new List<ProcSnapshot>();
        public long Mem;
        public bool HadWindow;         // 这些进程里是否有带窗口的（决定"无窗口未强制结束"文案）
    }

    // 结束结果（按应用汇总，给确认后结果报告用）
    internal class EndOutcome
    {
        public string AppName;
        public string Text;            // 已正常关闭 / 未退出，未强制结束 / 强制结束成功 / ...
        public bool Ok;
    }

    // ========================================================================
    // 设置存储（注册表 HKCU，只存用户的主动选择）
    // ========================================================================
    internal static class SettingsStore
    {
        const string SubKey = @"Software\EyesOff";
        public static bool CloseToTray { get { return ReadFlag("CloseToTray", true); } }
        static bool ReadFlag(string name, bool fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SubKey))
                { return key == null ? fallback : Convert.ToInt32(key.GetValue(name, fallback ? 1 : 0)) == 1; }
            }
            catch { return fallback; }
        }
        public static void SetCloseToTray(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SubKey))
                key.SetValue("CloseToTray", enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static string StartupCommand(string exe) { return "\"" + exe + "\" --tray"; }
        public static bool StartWithWindows
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                        return key != null && string.Equals(key.GetValue("EyesOff") as string,
                            StartupCommand(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }
        public static void SetStartWithWindows(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue("EyesOff", StartupCommand(Application.ExecutablePath));
                else key.DeleteValue("EyesOff", false);
            }
        }


        // "允许强制结束"默认关闭。这是刻意的安全设计：
        // 强制结束(Kill)不给程序保存数据的机会，必须由用户明确开启。
        public static bool AllowForceKill
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SubKey))
                    {
                        if (k == null) return false;
                        object v = k.GetValue("AllowForceKill");
                        return (v is int) && ((int)v) == 1;
                    }
                }
                catch { return false; }
            }
        }

        public static void SetAllowForceKill(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SubKey))
                {
                    k.SetValue("AllowForceKill", on ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }
    }

    // ========================================================================
    // protect.txt 用户保护名单
    // 规则：每行一个应用名或 exe 名；忽略空行与 # 注释；不区分大小写；
    //       命中的项目显示为"用户保护"，不可被清理；每次刷新重新读取。
    // ========================================================================
    internal static class ProtectList
    {
        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "protect.txt"); }
        }

        public static HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 确保文件存在（首次只写注释示例，不默认加入任何具体软件）并加载
        public static string EnsureLoaded()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    string tpl =
                        "# EyesOff 用户保护名单\r\n" +
                        "# 每行写一个应用名或 exe 名（不区分大小写），例如：\r\n" +
                        "#   wechat.exe\r\n" +
                        "#   网易云音乐\r\n" +
                        "# 名单中的应用显示为\"用户保护\"，永远不会被本工具结束。\r\n" +
                        "# 以 # 开头的行为注释。保存后回到工具点\"刷新应用\"即可生效。\r\n";
                    File.WriteAllText(FilePath, tpl, new UTF8Encoding(true));
                }
                Load();
                return "已加载保护名单 protect.txt（" + Names.Count + " 项）";
            }
            catch (Exception ex)
            {
                // 读不到名单时绝不能带着"名单可能生效"的错觉继续：清空并如实提示。
                // 其余硬编码安全规则不受影响。
                Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return "protect.txt 读取失败（" + ex.Message + "），本次运行不含用户保护项";
            }
        }

        public static void Load()
        {
            Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return;
            foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;   // 空行与注释
                if (line.Contains("\\")) line = line.Substring(line.LastIndexOf('\\') + 1); // 容错：写了完整路径
                string n = Normalize(line);
                if (n.Length > 0) Names.Add(n);
            }
        }

        public static string Normalize(string s)
        {
            if (s == null) return "";
            string n = s.Trim().ToLowerInvariant();
            if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
            return n.Trim();
        }

        // 命中判定：应用显示名 / 产品名 / 描述 / exe 文件名 / 原始进程名，任一精确匹配即命中。
        // 用精确匹配而不是包含匹配：防止保护 "wechat" 时误伤 "wechatupdate" 之类的其他软件。
        public static bool IsProtected(ProcSnapshot p)
        {
            if (Names.Count == 0) return false;
            string exeName = null;
            try { if (!string.IsNullOrEmpty(p.ExePath)) exeName = Path.GetFileNameWithoutExtension(p.ExePath); } catch { }
            if (exeName != null && Names.Contains(Normalize(exeName))) return true;
            if (Names.Contains(Normalize(p.Name))) return true;
            if (p.Id != null)
            {
                if (Names.Contains(Normalize(p.Id.Name))) return true;
            }
            return false;
        }
    }

    // ========================================================================
    // 应用识别：从 exe 的版本信息里读取"人看得懂"的名字。
    // 优先级：ProductName > FileDescription > CompanyName + exe 名 > exe 名。
    // 全部读取失败或只有裸文件名 → Level 0（未知应用）。
    // ========================================================================
    internal static class AppIdentity
    {
        class CacheItem { public IdentityInfo Id; }
        static Dictionary<string, CacheItem> cache = new Dictionary<string, CacheItem>(StringComparer.OrdinalIgnoreCase);

        public static IdentityInfo Identify(string exePath)
        {
            string key = (exePath ?? "").ToLowerInvariant();
            CacheItem hit;
            if (cache.TryGetValue(key, out hit)) return hit.Id;

            IdentityInfo id = new IdentityInfo();
            id.Name = null; id.Publisher = null; id.Desc = null; id.Level = 0; id.Source = "无法读取版本信息";

            string fileName = null;
            try { fileName = Path.GetFileNameWithoutExtension(exePath); } catch { }

            try
            {
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exePath);
                    string product = (vi.ProductName ?? "").Trim();
                    string desc = (vi.FileDescription ?? "").Trim();
                    string company = (vi.CompanyName ?? "").Trim();

                    if (product.Length > 0)
                    {
                        id.Name = product; id.Level = 2; id.Source = "exe 版本信息 - 产品名";
                    }
                    else if (desc.Length > 0)
                    {
                        id.Name = desc; id.Level = 2; id.Source = "exe 版本信息 - 文件说明";
                    }
                    else if (company.Length > 0 && !string.IsNullOrEmpty(fileName))
                    {
                        // 有公司名但没有产品名/描述：用"公司名 + exe 名"，可识别但可信度低一档。
                        id.Name = company + " " + fileName; id.Level = 1; id.Source = "exe 版本信息 - 公司名+文件名";
                    }

                    if (id.Name != null)
                    {
                        id.Publisher = company.Length > 0 ? company : "未知发布者";
                        // 用途说明优先用"文件说明"（它通常就是一句人话用途），避免与名称列重复。
                        if (desc.Length > 0 && !string.Equals(desc, id.Name, StringComparison.OrdinalIgnoreCase))
                            id.Desc = desc;
                        else if (product.Length > 0 && !string.Equals(product, id.Name, StringComparison.OrdinalIgnoreCase))
                            id.Desc = product;
                        else
                            id.Desc = "发布者：" + id.Publisher;
                    }
                }
            }
            catch
            {
                // 版本信息读取失败（权限/文件被占用等）→ 按未知处理，安全规则第 8 条。
            }

            if (id.Name == null)
            {
                // 只剩裸 exe 文件名：不能凭它猜测软件用途（安全规则），按"未知应用"处理。
                id.Name = string.IsNullOrEmpty(fileName) ? "未知应用" : fileName;
                id.Publisher = "未知发布者";
                id.Desc = "未知用途，不建议清理";
                id.Level = 0;
                id.Source = "无版本信息，仅 exe 文件名";
            }

            cache[key] = new CacheItem { Id = id };
            return id;
        }
    }

    // ========================================================================
    // 应用图标缓存：从 exe 提取图标，失败用系统默认程序图标。
    // 缓存按路径存放、整个进程生命周期保留，避免刷新时反复读盘。
    // ========================================================================
    internal static class AppIconCache
    {
        static Dictionary<string, Icon> cache = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

        public static Icon Get(string exePath)
        {
            string key = (exePath ?? "").ToLowerInvariant();
            Icon hit;
            if (cache.TryGetValue(key, out hit)) return hit;

            Icon icon = null;
            try
            {
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    icon = Icon.ExtractAssociatedIcon(exePath);
            }
            catch { icon = null; }   // 提取失败（权限/非标准 exe）→ 用默认图标，绝不让界面因此出错

            if (icon == null) icon = SystemIcons.Application;
            cache[key] = icon;
            return icon;
        }
    }

    // ========================================================================
    // Win32 API（全部为系统 user32 / kernel32 / ntdll 自带函数）
    // ========================================================================
    internal static class NativeMethods
    {
        // ---- 熄屏 ----
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MONITORPOWER = 0xF170;
        public const int HWND_BROADCAST = unchecked((int)0xFFFF);
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        // ---- 热键 ----
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;

        // ---- 窗口枚举 ----
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        // Vista+：用受限查询权限读取进程 exe 完整路径。
        // 为什么不用 Process.MainModule 为主：MainModule 对部分提权进程会直接抛异常，
        // QueryFullProcessImageName 的成功率高得多；MainModule 只作兜底。
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
            StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass,
            ref PROCESS_BASIC_INFORMATION pbi, int cb, out int returnLength);

        // 查询指定进程的父进程 PID（只对自家进程查询，权限充分）
        public static int GetParentProcessId(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return -2;    // -2 = 未知，不参与任何排除逻辑
                PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
                int len;
                int status = NtQueryInformationProcess(h, 0, ref pbi,
                    Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out len);
                if (status == 0) return unchecked((int)pbi.InheritedFromUniqueProcessId);
            }
            catch { }
            finally { if (h != IntPtr.Zero) try { CloseHandle(h); } catch { } }
            return -2;
        }

        // 读取进程 exe 完整路径；读不到返回 null（调用方按"路径无法确认"处理）
        public static string GetExePath(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return null;
                StringBuilder sb = new StringBuilder(1024);
                uint size = 1024;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
            }
            catch { }
            finally { if (h != IntPtr.Zero) try { CloseHandle(h); } catch { } }
            return null;
        }

        // ---- 熄屏三段式（主方案 + 两个备用方案），逻辑与 v1 相同 ----

        // 主方案：广播 WM_SYSCOMMAND + SC_MONITORPOWER(2)。lParam=2 表示关闭显示器。
        // 用 SendMessageTimeout 而不是 PostMessage：广播必须"送达"才可靠，
        // SMTO_ABORTIFHUNG 保证遇到挂起窗口时立刻放弃，不卡主线程。
        public static bool TurnOffMonitorByBroadcast()
        {
            IntPtr ret;
            IntPtr ok = SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND,
                (IntPtr)SC_MONITORPOWER, (IntPtr)2, SMTO_ABORTIFHUNG, 3000, out ret);
            return ok != IntPtr.Zero;
        }

        // 备用 1：直接发给自己窗口。DefWindowProc 会处理 SC_MONITORPOWER 并熄屏，
        // 可绕过个别窗口吞掉广播的情况。
        public static void TurnOffMonitorByOwnWindow(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
                SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);
        }

        // 备用 2：让 PowerShell 子进程（隐藏、无界面）把同一条消息再广播一次。
        // 诚实说明：用户态没有第二条熄屏通路（SetThreadExecutionState 只能阻止熄屏；
        // DDC/CI 断电会导致鼠标无法唤醒），本方案只覆盖"本进程消息被拦截"的极端场景。
        public static void TurnOffMonitorByPowerShell()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Environment.ExpandEnvironmentVariables(
                    @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe");
                psi.Arguments = "-NoProfile -WindowStyle Hidden -Command \"Add-Type -MemberDefinition '[DllImport(\\\"user32.dll\\\")]public static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);' -Name W -Namespace N; [N.W]::SendMessage([IntPtr]0xFFFF,0x0112,[IntPtr]0xF170,[IntPtr]2)\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch { }
        }

        // 检测"新式待机"机型（这类机器可能忽略熄屏消息，只做提示）
        public static bool IsModernStandby()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("CsEnabled");
                        if (v is int) return ((int)v) == 1;
                    }
                }
            }
            catch { }
            return false;
        }
    }

    // ========================================================================
    // 窗口扫描：一次枚举所有顶层窗口，得到"哪些进程当前有可见窗口"。
    // 判定"正在使用"的三条依据：有可见窗口 / 是前台窗口进程 / 窗口正常显示。
    // 可见 + 非工具窗口 + 有标题 → 记为该进程的可见窗口。
    // 保守取舍：UWP 挂起应用的窗口（被系统 Cloak 遮蔽）仍按可见处理——
    // 它们出现在任务栏里，用户会认为"正在使用"，按保护处理更安全。
    // ========================================================================
    internal static class WindowScanner
    {
        public static Dictionary<int, bool> ScanVisibleWindows()
        {
            Dictionary<int, bool> map = new Dictionary<int, bool>();
            NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                try
                {
                    if (NativeMethods.IsWindowVisible(hWnd))
                    {
                        int ex = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) == 0          // 排除工具窗口
                            && NativeMethods.GetWindowTextLength(hWnd) > 0)      // 排除无标题辅助窗口
                        {
                            int pid;
                            NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                            if (pid != 0) map[pid] = true;
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return map;
        }
    }

    // ========================================================================
    // 进程扫描与分类（只读，不结束任何进程；放在后台线程执行）
    // ========================================================================
    internal static class ProcessScanner
    {
        public static HashSet<int> AncestorPids = new HashSet<int>();   // 本工具的父进程链
        public static int MyPid;
        public static int CurrentSessionId;
        public static string MyExePath;
        static bool initialized;

        static void EnsureInit()
        {
            if (initialized) return;
            MyPid = Process.GetCurrentProcess().Id;
            try { CurrentSessionId = Process.GetCurrentProcess().SessionId; }
            catch { CurrentSessionId = -1; }
            try { MyExePath = Process.GetCurrentProcess().MainModule.FileName; }
            catch { MyExePath = ""; }

            // 规则 3：排除本工具的"父进程链"而不只是直接父进程。
            // 为什么：如果工具是被脚本/启动器拉起的，结束那一条链上的任何一环
            // 都可能连带打断用户正在使用的操作入口。沿父指针向上走，带深度
            // 防护（最多 12 层 + 环检测），避免异常数据导致死循环。
            int cur = MyPid;
            for (int depth = 0; depth < 12; depth++)
            {
                int parent = NativeMethods.GetParentProcessId(cur);
                if (parent <= 4 || parent == cur || AncestorPids.Contains(parent)) break;
                AncestorPids.Add(parent);
                cur = parent;
            }
            initialized = true;
        }

        public static bool IsBlacklisted(string processName)
        {
            // 硬编码黑名单（安全规则第 5 条，与"路径在系统目录"互为双重保险）。
            // 这些是 Windows 正常运行必需的进程，结束任何一个都会导致
            // 黑屏/卡死/输入法失效/安全防护失效，因此不显示、不结束。
            string n = Normalize(processName);
            switch (n)
            {
                case "system":
                case "system idle process":
                case "registry":
                case "smss":
                case "csrss":
                case "wininit":
                case "winlogon":
                case "services":
                case "lsass":
                case "svchost":
                case "dwm":
                case "explorer":
                case "ctfmon":
                case "sihost":
                case "taskhostw":
                case "runtimebroker":
                case "searchhost":
                case "startmenuexperiencehost":
                case "shellexperiencehost":
                case "textinputhost":
                case "fontdrvhost":
                case "audiodg":
                case "conhost":
                case "wmiprvse":
                case "searchindexer":
                case "securityhealthservice":
                case "msmpeng":
                    return true;
                default:
                    return false;
            }
        }

        public static string Normalize(string s)
        {
            if (s == null) return "";
            string n = s.Trim().ToLowerInvariant();
            if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
            return n.Trim();
        }

        // 规则 4：路径是否位于系统目录（含子目录）。
        // 用"规范化完整路径以系统目录+分隔符开头"判定；解析失败按 true 处理
        // （宁可错杀出列表，不可放进列表）。
        static string windowsDir;
        public static bool IsUnderWindowsDir(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (windowsDir == null)
            {
                string w = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!w.EndsWith("\\")) w += "\\";
                windowsDir = w.ToLowerInvariant();
            }
            string norm;
            try { norm = Path.GetFullPath(fullPath).ToLowerInvariant(); }
            catch { return true; }
            return norm.StartsWith(windowsDir, StringComparison.Ordinal);
        }

        // 单进程分类。每一步的"为什么安全"见行内注释。
        static void Classify(ProcSnapshot s)
        {
            // 先赋一个兜底身份（用原始进程名）：保证任何早退分支返回后 Id 都非 null，
            // 后续合并/界面逻辑不会遇到空引用。能读到路径的进程会在下方被版本信息覆盖。
            s.Id = new IdentityInfo();
            s.Id.Name = s.Name;
            s.Id.Publisher = "—";
            s.Id.Desc = "";
            s.Id.Level = 0;
            s.Id.Source = "受保护/未识别（未读取版本信息）";

            // 规则 2：PID ≤ 100 是系统保留段（0=Idle、4=System 等），根本不能被结束。
            if (s.Pid <= 100) { s.Status = AppStatus.Protected; s.Reason = "系统保留 PID（≤100）"; return; }

            // 规则 5：黑名单。这些进程结束会导致系统功能瘫痪，不显示、不结束。
            if (IsBlacklisted(s.Name)) { s.Status = AppStatus.Blacklisted; s.Reason = "系统关键进程（黑名单）"; return; }

            // 规则 3：本工具自身与父进程链。结束自己=自杀；结束父链上的进程
            // （资源管理器、启动它的终端/脚本）会打断用户的操作入口。
            if (s.Pid == MyPid || AncestorPids.Contains(s.Pid))
            { s.Status = AppStatus.Protected; s.Reason = "本工具自身或其父进程链"; return; }

            // 规则 1：只处理当前用户会话。其他会话（服务会话 0、其他登录用户）
            // 的进程不属于"我能负责"的范围。
            if (s.SessionId != CurrentSessionId)
            { s.Status = AppStatus.Protected; s.Reason = "其他会话的进程"; return; }

            // 路径读不到 → 无法证明它不是系统进程 → 按受保护处理（安全规则第 8 条）。
            // Id 已在方法开头赋了兜底值（原始进程名），界面显示不受影响。
            if (string.IsNullOrEmpty(s.ExePath))
            { s.Status = AppStatus.Protected; s.Reason = "路径无法确认"; return; }

            // 本工具的另一个实例也不处理（防止双开互杀）。
            if (string.Equals(s.ExePath, MyExePath, StringComparison.OrdinalIgnoreCase))
            { s.Status = AppStatus.Protected; s.Reason = "本工具自身（另一实例）"; return; }

            // 识别应用身份（供保护名单匹配与界面显示）。
            s.Id = AppIdentity.Identify(s.ExePath);

            // 用户保护名单：用户明确声明"不能动"的应用，状态显示"用户保护"。
            if (ProtectList.IsProtected(s))
            { s.Status = AppStatus.UserProtected; s.Reason = "protect.txt 保护名单"; return; }

            // 正在使用：有可见窗口或就是前台窗口进程。这类应用用户多半正在用，
            // 放进"正在使用（已保护）"区，默认不可清理。
            // 注意：有可见窗口的判定在前，系统目录里的应用（如记事本）带窗口时
            // 也按"正在使用（已保护）"显示——但 CanEnd 仍为 false（见下）。
            if (s.HasWindow || s.IsForeground)
            { s.Status = AppStatus.InUse; s.Reason = "有可见窗口"; return; }

            // 规则 4：exe 位于系统目录的进程一律不结束（无窗口也不行）。
            if (IsUnderWindowsDir(s.ExePath))
            { s.Status = AppStatus.Protected; s.Reason = "系统目录进程"; return; }

            // 后台 + 可识别（版本信息级别 ≥ 1）才进入候选；常见运行时、更新器、
            // 崩溃收集器和开发工具即使没有窗口，也不适合被"一键清理"。
            if (s.Id.Level >= 1 && IsLikelySafeBackground(s)) { s.Status = AppStatus.Background; s.Reason = "已识别为无窗口后台应用（仍需确认）"; return; }

            // 规则 8：识别不了 → 未知应用，不进推荐列表、禁止批量结束。
            s.Status = AppStatus.Unknown; s.Reason = "无窗口或用途不明确，待你确认";
        }

        static bool IsLikelySafeBackground(ProcSnapshot s)
        {
            string n = Normalize(s.Name);
            string d = ((s.Id.Name ?? "") + " " + (s.Id.Desc ?? "") + " " + (s.Id.Publisher ?? "")).ToLowerInvariant();
            string[] deny = { "update", "updater", "crashpad", "crash reporter", "runtime", "webview", "service", "helper", "assistant", "daemon", "guard", "tray", "node.js", "python", "tailscale", "docker", "ollama", "git", "powershell", "terminal", "windows app sdk" };
            for (int i = 0; i < deny.Length; i++)
                if (n.Contains(deny[i]) || d.Contains(deny[i])) return false;
            return true;
        }

        // 结束权限与显示状态解耦：即使显示为"正在使用"，
        // 只要命中任一硬性保护规则，CanEnd 必须为 false。
        static void ComputeCanEnd(ProcSnapshot s)
        {
            s.CanEnd =
                s.SessionId == CurrentSessionId &&
                s.Pid > 100 &&
                s.Status != AppStatus.Blacklisted &&
                s.Pid != MyPid && !AncestorPids.Contains(s.Pid) &&
                !string.IsNullOrEmpty(s.ExePath) &&
                !IsUnderWindowsDir(s.ExePath);
        }

        public class ScanResult
        {
            public List<ProcSnapshot> Procs = new List<ProcSnapshot>();  // 全部进程（含受保护，供高级模式/统计）
            public int TotalCount;                                       // 系统进程总数
        }

        // 扫描全部进程 → 逐个取快照 → 分类。全程只读。
        public static ScanResult ScanAll()
        {
            EnsureInit();
            ScanResult result = new ScanResult();

            // 先做一次窗口快照：判定"正在使用"需要全局的窗口归属信息。
            Dictionary<int, bool> winMap = WindowScanner.ScanVisibleWindows();
            IntPtr fg = NativeMethods.GetForegroundWindow();
            int fgPid = 0;
            NativeMethods.GetWindowThreadProcessId(fg, out fgPid);

            foreach (Process p in Process.GetProcesses())
            {
                ProcSnapshot s = new ProcSnapshot();
                try
                {
                    s.Pid = p.Id;
                    s.Name = p.ProcessName;
                    try { s.SessionId = p.SessionId; } catch { s.SessionId = -1; }
                    try { s.Memory = p.WorkingSet64; } catch { s.Memory = 0; }
                    s.HasWindow = winMap.ContainsKey(s.Pid);
                    s.IsForeground = (s.Pid == fgPid);

                    // 路径：优先 QueryFullProcessImageName（成功率高），MainModule 兜底。
                    s.ExePath = NativeMethods.GetExePath(s.Pid);
                    if (string.IsNullOrEmpty(s.ExePath))
                    {
                        try { ProcessModule mm = p.MainModule; if (mm != null) s.ExePath = mm.FileName; }
                        catch { s.ExePath = null; }
                    }

                    Classify(s);
                    ComputeCanEnd(s);
                    result.Procs.Add(s);
                    result.TotalCount++;
                }
                catch
                {
                    // 进程在枚举间隙退出/属性不可读：正常现象，跳过，不影响其他进程。
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
            return result;
        }
    }

    // ========================================================================
    // 应用合并模型：把进程快照按 exe 路径合并成"应用"
    // ========================================================================
    internal static class AppModel
    {
        public class Model
        {
            public List<AppEntry> Apps = new List<AppEntry>();   // 当前会话、非黑名单的全部应用
            public int TotalProcCount;                           // 系统进程总数
        }

        public static Model Build(ProcessScanner.ScanResult scan)
        {
            Model model = new Model();
            model.TotalProcCount = scan.TotalCount;
            Dictionary<string, AppEntry> byKey = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (ProcSnapshot p in scan.Procs)
            {
                // 黑名单与其他会话的进程不进入任何应用视图（简洁/高级都不出现）。
                if (p.Status == AppStatus.Blacklisted) continue;
                if (p.SessionId != ProcessScanner.CurrentSessionId) continue;

                // 同一软件经常由主程序、渲染器、插件和沙箱进程组成。用"产品名+发布者"
                // 合并它们，让用户看到一个可理解的应用组，而不是一串 exe。
                string group = ((p.Id.Name ?? p.Name) + "|" + (p.Id.Publisher ?? "")).Trim();
                string key = p.Id.Level >= 1 ? group.ToLowerInvariant() : (p.ExePath ?? ("$unknown$" + p.Pid)).ToLowerInvariant();
                AppEntry app;
                if (!byKey.TryGetValue(key, out app))
                {
                    app = new AppEntry();
                    app.Key = key;
                    app.DisplayName = p.Id.Name;
                    app.Publisher = p.Id.Publisher;
                    app.Desc = p.Id.Desc;
                    app.Level = p.Id.Level;
                    app.IdentifySource = p.Id.Source;
                    app.AppIcon = AppIconCache.Get(p.ExePath);
                    byKey[key] = app;
                    model.Apps.Add(app);
                }
                app.Procs.Add(p);
                app.TotalMem += p.Memory;
            }

            // 聚合状态：取"最需要保护"的一档。为什么这么排：
            // 只要有一个进程在用/被保护，整个应用就按更严的一档对待，
            // 避免出现"应用一半进程受保护、一半可清理"的暧昧状态。
            foreach (AppEntry app in model.Apps)
            {
                AppStatus st = AppStatus.Background;
                bool has = false;
                foreach (ProcSnapshot p in app.Procs)
                {
                    AppStatus cand = p.Status;
                    if (!has) { st = cand; has = true; continue; }
                    st = Stricter(st, cand);
                }
                app.Status = st;
                // 总占用很高时不能仅凭"后台"推荐清理；这类应用往往是浏览器/
                // 微信组件/编辑器的运行时，结束它可能丢失任务。
                if (app.Status == AppStatus.Background && app.TotalMem >= 700L * 1024L * 1024L)
                    app.Status = AppStatus.Unknown;

                // 状态列与风险提示列文案（给人看的话术）
                switch (app.Status)
                {
                    case AppStatus.InUse:
                        app.StatusText = "正在使用";
                        app.RiskHint = "有可见窗口，已保护，不会被推荐清理";
                        break;
                    case AppStatus.UserProtected:
                        app.StatusText = "用户保护";
                        app.RiskHint = "protect.txt 保护名单命中，不会被清理";
                        break;
                    case AppStatus.Background:
                        app.StatusText = "待确认";
                        app.RiskHint = "没有可见窗口；仅在你确认无任务时结束";
                        break;
                    case AppStatus.Unknown:
                        app.StatusText = app.TotalMem >= 700L * 1024L * 1024L ? "高占用待确认" : "待确认";
                        app.RiskHint = app.TotalMem >= 700L * 1024L * 1024L
                            ? "占用较高（" + (app.TotalMem / 1048576.0).ToString("F0") + " MB）；请先确认它没有未保存任务"
                            : "用途或安全边界不明确；请查看详情后决定";
                        break;
                    default:
                        app.StatusText = "受保护";
                        string r = "";
                        foreach (ProcSnapshot p in app.Procs) if (!string.IsNullOrEmpty(p.Reason)) { r = p.Reason; break; }
                        app.RiskHint = string.IsNullOrEmpty(r) ? "命中安全规则；详情中查看原因" : r;
                        break;
                }
            }
            return model;
        }

        static int Rank(AppStatus s)
        {
            // 越大越"需要保护"：黑名单(不显示)之外，受保护 > 用户保护 ≈ 正在使用 > 未知 > 后台
            switch (s)
            {
                case AppStatus.Protected: return 5;
                case AppStatus.UserProtected: return 4;
                case AppStatus.InUse: return 4;
                case AppStatus.Unknown: return 3;
                case AppStatus.Background: return 1;
                default: return 0;
            }
        }

        static AppStatus Stricter(AppStatus a, AppStatus b)
        {
            return Rank(a) >= Rank(b) ? a : b;
        }
    }

    // ========================================================================
    // 结束执行器：整个工具里唯一真正会结束进程的地方。
    // 流程（需求第五节）：
    //   1) 逐进程重新验证身份（防 PID 复用）；
    //   2) 有窗口的先 CloseMainWindow()（相当于点 ×）；
    //   3) 等待最多 3 秒并复查是否退出；
    //   4) 未退出的默认【不】Kill——报告"未退出，未强制结束"；
    //   5) 只有用户在设置中开启"允许强制结束"且在确认框勾选确认，才 Kill()。
    //   单个应用失败不中断其他应用。
    // ========================================================================
    internal static class AppEndRunner
    {
        // 温和阶段。返回需要"强制结束决策"的存活任务列表（写入 stubborn）。
        public static void GentlePhase(List<EndTask> tasks, List<EndOutcome> outcomes, List<EndTask> stubborn)
        {
            foreach (EndTask t in tasks)
            {
                int initialCount = t.Procs.Count;   // 任务开始时的进程数（用于区分"自行退出"与"被我们关闭"）
                List<ProcSnapshot> alive = new List<ProcSnapshot>();
                List<string> fails = new List<string>();
                int alreadyGone = 0;
                bool sentClose = false;
                bool aliveHadWindow = false;

                foreach (ProcSnapshot proc in t.Procs)
                {
                    string err;
                    Process p = Revalidate(proc, out err);
                    if (p == null)
                    {
                        if (err == AlreadyExitedText) alreadyGone++;
                        else fails.Add(proc.Name + " (PID " + proc.Pid + ")：" + err);
                        continue;
                    }
                    try
                    {
                        if (p.HasExited) { alreadyGone++; continue; }
                        // 有窗口的程序优先温和关闭：等价于用户点了窗口右上角的 ×，
                        // 给应用保存数据、正常退出的机会。
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            aliveHadWindow = true;
                            try { if (p.CloseMainWindow()) sentClose = true; }
                            catch { }
                        }
                        alive.Add(proc);
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                // 等待最多 3 秒并复查（需求规定的等待窗口）。轮询而不是死等，
                // 应用退出了就立刻进入下一步。
                if (alive.Count > 0)
                {
                    int waitMs = sentClose ? 3000 : 500;
                    for (int i = 0; i < waitMs / 200; i++)
                    {
                        Thread.Sleep(200);
                        List<ProcSnapshot> still = new List<ProcSnapshot>();
                        foreach (ProcSnapshot proc in alive) if (IsAlive(proc)) still.Add(proc);
                        alive = still;
                        if (alive.Count == 0) break;
                    }
                }

                t.Procs = alive;       // 存活部分留给强制阶段
                t.HadWindow = aliveHadWindow;

                if (alive.Count == 0)
                {
                    // 全部退出：区分"我们自己关掉的"和"动手前就已自己退出的"。
                    if (alreadyGone == initialCount && !sentClose && fails.Count == 0)
                    {
                        outcomes.Add(new EndOutcome { AppName = t.AppName, Text = "进程已经自行退出", Ok = true });
                    }
                    else
                    {
                        string extra = (alreadyGone > 0) ? "（另有 " + alreadyGone + " 个进程此前已自行退出）" : "";
                        if (fails.Count > 0)
                            outcomes.Add(new EndOutcome { AppName = t.AppName, Text = "已正常关闭" + extra + "；但有进程未能执行：" + JoinStr(fails), Ok = false });
                        else
                            outcomes.Add(new EndOutcome { AppName = t.AppName, Text = "已正常关闭" + extra, Ok = true });
                    }
                    continue;
                }

                if (fails.Count > 0)
                {
                    // 部分进程校验失败（如权限/身份变化），失败不中断其余流程，
                    // 但必须如实报告——失败不许静默。
                    outcomes.Add(new EndOutcome
                    {
                        AppName = t.AppName,
                        Text = "部分进程未能执行：" + JoinStr(fails) + "；其余进程等待强制结束决策",
                        Ok = false
                    });
                }
                stubborn.Add(t);
            }
        }

        // 强制阶段：仅当 设置开启 && 用户在确认框勾选"允许强制结束" 时才会被调用。
        public static EndOutcome ForceEnd(EndTask t)
        {
            int killed = 0, gone = 0, failed = 0;
            List<string> msgs = new List<string>();
            foreach (ProcSnapshot proc in t.Procs)
            {
                string err;
                Process p = Revalidate(proc, out err);
                if (p == null)
                {
                    if (err == AlreadyExitedText) gone++;
                    else { failed++; msgs.Add(proc.Name + " (PID " + proc.Pid + ")：" + err); }
                    continue;
                }
                try
                {
                    p.Kill();
                    p.WaitForExit(2000);
                    if (p.HasExited) killed++;
                    else { failed++; msgs.Add(proc.Name + " (PID " + proc.Pid + ")：已发出结束指令但未确认退出"); }
                }
                catch (Win32Exception wex)
                {
                    failed++;
                    msgs.Add(proc.Name + " (PID " + proc.Pid + ")：结束失败（" + wex.Message + "）。常见原因：目标以管理员权限运行，普通权限的工具无权结束它");
                }
                catch (Exception ex)
                {
                    failed++;
                    msgs.Add(proc.Name + " (PID " + proc.Pid + ")：结束失败：" + ex.Message);
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            if (failed == 0)
                return new EndOutcome { AppName = t.AppName, Text = "强制结束成功" + (gone > 0 ? "（另有 " + gone + " 个进程已自行退出）" : ""), Ok = true };
            return new EndOutcome
            {
                AppName = t.AppName,
                Text = (killed > 0 ? "强制结束 " + killed + " 个进程，" : "") + "失败 " + failed + " 个：" + JoinStr(msgs),
                Ok = false
            };
        }

        // 温和阶段结束后、未开启强制结束时的结果文案
        public static EndOutcome NoForceOutcome(EndTask t)
        {
            // 需求指定文案：没有主窗口的程序在未开启强制结束时不直接杀掉。
            if (!t.HadWindow)
                return new EndOutcome { AppName = t.AppName, Text = "该应用没有可关闭的窗口，未执行强制结束。", Ok = false };
            return new EndOutcome { AppName = t.AppName, Text = "未退出，未强制结束（程序可能已最小化到托盘或无响应）", Ok = false };
        }

        public const string AlreadyExitedText = "进程已经自行退出";

        // 结束前的最终身份核验（安全的核心中的核心）：
        // 列表是刷新时生成的，从生成到点击之间目标可能已退出、PID 可能被系统
        // 复用给另一个进程。只认 PID 会误杀"顶替者"，所以必须：
        //   ① 重新打开进程确认还活着；
        //   ② 核对进程名一致；
        //   ③ 重新读取完整路径并与快照一致（路径是比名字更硬的证据）；
        //   ④ 把会话/系统目录/黑名单等硬规则再过一遍。
        // 任何一条不满足 → 返回错误原因，拒绝执行（宁可放过，不可错杀）。
        static Process Revalidate(ProcSnapshot proc, out string err)
        {
            err = null;
            Process p = null;
            try { p = Process.GetProcessById(proc.Pid); }
            catch { err = AlreadyExitedText; return null; }

            try
            {
                if (p.HasExited) { err = AlreadyExitedText; return null; }

                string name = null;
                try { name = p.ProcessName; } catch { name = null; }
                if (name == null || !string.Equals(name, proc.Name, StringComparison.OrdinalIgnoreCase))
                {
                    err = "进程名已变化（现 \"" + (name ?? "未知") + "\"），疑似 PID 被系统复用，为避免误杀未执行";
                    return null;
                }

                // 重新验证完整路径。读不到当前路径时按"无法确认身份"拒绝：
                // 虽然偶尔会误伤正常目标，但相比"可能杀错进程"，保守是正确的取舍。
                string nowPath = NativeMethods.GetExePath(proc.Pid);
                if (string.IsNullOrEmpty(nowPath))
                {
                    err = "无法重新确认进程完整路径，为安全起见未执行";
                    return null;
                }
                if (!string.Equals(NormalizeKey(nowPath), NormalizeKey(proc.ExePath)))
                {
                    err = "完整路径已变化（现 " + nowPath + "），疑似 PID 被复用，为避免误杀未执行";
                    return null;
                }

                if (ProcessScanner.IsUnderWindowsDir(nowPath)) { err = "目标位于系统目录，禁止结束"; return null; }
                if (p.SessionId != ProcessScanner.CurrentSessionId) { err = "目标属于其他会话，禁止结束"; return null; }
                if (ProcessScanner.IsBlacklisted(name)) { err = "目标命中黑名单，禁止结束"; return null; }
                if (proc.Pid == ProcessScanner.MyPid || ProcessScanner.AncestorPids.Contains(proc.Pid))
                { err = "目标是本工具自身或其父进程链，禁止结束"; return null; }

                return p;   // 校验通过，调用方负责 Dispose
            }
            catch (Exception ex)
            {
                err = "校验失败：" + ex.Message;
                try { p.Dispose(); } catch { }
                return null;
            }
        }

        static bool IsAlive(ProcSnapshot proc)
        {
            try
            {
                using (Process p = Process.GetProcessById(proc.Pid)) return !p.HasExited;
            }
            catch { return false; }
        }

        static string NormalizeKey(string path)
        {
            try { return Path.GetFullPath(path).ToLowerInvariant(); }
            catch { return (path ?? "").ToLowerInvariant(); }
        }

        static string JoinStr(List<string> items)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append("；");
                sb.Append(items[i]);
            }
            return sb.ToString();
        }
    }

    // ========================================================================
    // 快捷方式：通过系统自带的 WScript.Shell COM 创建（无第三方依赖）。
    // 只创建"当前用户"桌面与开始菜单的 .lnk，不复制文件、不写注册表启动项。
    // ========================================================================
    internal static class ShortcutHelper
    {
        public const string LnkName = "EyesOff 一键熄屏.lnk";
        public static string EnsureShortcuts()
        {
            object shell = null;
            try
            {
                string exe = Application.ExecutablePath;
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                if (shell == null) return "创建快捷方式失败：系统缺少 WScript.Shell 组件";
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "EyesOff");
                Directory.CreateDirectory(menu);
                MakeLink(shell, Path.Combine(desk, LnkName), exe, "--show", "打开 EyesOff 控制台");
                MakeLink(shell, Path.Combine(desk, "EyesOff 立即熄屏.lnk"), exe, "--off", "直接熄屏，不打开主窗口");
                MakeLink(shell, Path.Combine(menu, "EyesOff.lnk"), exe, "--show", "打开 EyesOff 控制台");
                MakeLink(shell, Path.Combine(menu, "立即熄屏.lnk"), exe, "--off", "直接熄屏");
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LnkName);
                if (File.Exists(legacy)) MakeLink(shell, legacy, exe, "--show", "打开 EyesOff 控制台");
                return "已创建控制台、立即熄屏两个入口。开机启动可在偏好设置中选择。";
            }
            catch (Exception ex) { return "创建快捷方式失败：" + ex.Message; }
            finally { if (shell != null) try { Marshal.ReleaseComObject(shell); } catch { } }
        }
        static void MakeLink(object shell, string path, string exe, string args, string description)
        {
            object link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { path });
            try
            {
                Set(link, "TargetPath", exe); Set(link, "Arguments", args);
                Set(link, "WorkingDirectory", Path.GetDirectoryName(exe));
                Set(link, "IconLocation", exe + ",0"); Set(link, "Description", description);
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        static void Set(object link, string name, string value)
        { link.GetType().InvokeMember(name, BindingFlags.SetProperty, null, link, new object[] { value }); }
    }

    // ========================================================================
    // 行模型：ListView 一行 = 一个应用（简洁模式）或一个进程（高级模式）
    // ========================================================================
    internal class RowModel
    {
        public AppEntry App;         // 所属应用（两种模式都有）
        public ProcSnapshot Proc;    // 高级模式：本行对应的进程；简洁模式为 null
        public bool InAdvanced;
        public bool Checkable;       // 勾选框是否可用
        public bool Endable;         // "结束"按钮是否可用
        public bool Checked;         // 勾选状态（自绘，便于完全掌控交互）
        public ListViewGroup Group;  // 简洁模式的分区
    }

    // ========================================================================
    // 主窗体
    // ========================================================================
    internal static class Ui
    {
        public static readonly Color Canvas = Color.FromArgb(245, 247, 248);
        public static readonly Color Navy = Color.FromArgb(21, 39, 48);
        public static readonly Color Ink = Color.FromArgb(29, 49, 60);
        public static readonly Color Muted = Color.FromArgb(105, 123, 133);
        public static readonly Color SideMuted = Color.FromArgb(126, 151, 163);
        public static readonly Color Accent = Color.FromArgb(151, 231, 202);
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            float d = radius * 2; GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
    }
    internal sealed class SoftPanel : Panel
    {
        public SoftPanel() { DoubleBuffered = true; BackColor = Color.White; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Canvas : Parent.BackColor);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), 16))
            using (SolidBrush brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
        }
    }
    internal sealed class SoftButton : Button
    {
        bool hovered;
        public SoftButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Ui.Canvas : Parent.BackColor);
            if (Width < 2 || Height < 2) return;
            Color fill = Enabled ? BackColor : Color.FromArgb(225, 232, 233);
            if (hovered && Enabled) fill = ControlPaint.Light(fill, .12f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Ui.Round(new RectangleF(1, 1, Width - 3, Height - 3), 9))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, path);
                if (Focused) using (Pen pen = new Pen(Ui.Muted, 1)) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Ui.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
    internal sealed class BrandMark : Control
    {
        public BrandMark() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(Ui.Accent)) e.Graphics.FillEllipse(brush, 1, 1, Width - 3, Height - 3);
            using (SolidBrush brush = new SolidBrush(Ui.Navy)) e.Graphics.FillEllipse(brush, Width * .42f, -4, Width * .8f, Height * .8f);
        }
    }
    internal sealed class MonitorArt : Control
    {
        public MonitorArt() { DoubleBuffered = true; BackColor = Ui.Navy; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(83, 120, 132), 2))
            using (GraphicsPath path = Ui.Round(new RectangleF(8, 33, 182, 113), 12))
            {
                e.Graphics.DrawPath(pen, path); e.Graphics.DrawLine(pen, 99, 146, 99, 166);
                e.Graphics.DrawLine(pen, 70, 167, 128, 167);
            }
            using (SolidBrush brush = new SolidBrush(Ui.Accent)) e.Graphics.FillEllipse(brush, 76, 59, 48, 48);
            using (SolidBrush brush = new SolidBrush(Ui.Navy)) e.Graphics.FillEllipse(brush, 92, 50, 45, 45);
            using (SolidBrush brush = new SolidBrush(Ui.Accent))
            { e.Graphics.FillEllipse(brush, 139, 62, 4, 4); e.Graphics.FillEllipse(brush, 61, 99, 3, 3); }
        }
    }

    internal class MainForm : Form
    {
        // ---- 简洁模式列索引 ----
        const int C_NAME = 0, C_PUB = 1, C_DESC = 2, C_STATUS = 3, C_MEM = 4, C_CNT = 5, C_RISK = 6, C_DETAIL = 7, C_END = 8;
        // ---- 高级模式列索引 ----
        const int A_NAME = 0, A_RAW = 1, A_PID = 2, A_MEM = 3, A_SESS = 4, A_WIN = 5, A_STATUS = 6, A_PATH = 7, A_DETAIL = 8, A_END = 9;

        const int HOTKEY_ID_SCREEN = 1;
        const int HOTKEY_ID_SHOW = 2;

        // ---- 控件 ----
        Button btnScreenOff;
        Label lblScreenHint;
        TextBox txtSearch;
        Button btnMode, btnMenu;
        Label lblWarn;
        ListView lv;
        Button btnRefresh, btnEndChecked, btnCleanRec;
        Label lblChecked, lblLast;
        NotifyIcon tray;
        ContextMenuStrip trayMenu, mainMenu;

        // ---- 数据 ----
        AppModel.Model model;              // 最近一次扫描的应用模型
        List<RowModel> rows = new List<RowModel>();    // 当前视图的行
        HashSet<object> checkedKeys = new HashSet<object>();  // 勾选集合（应用或进程快照为键）
        bool advancedMode;
        bool busy;
        bool balloonShown;
        bool suppressFirstShow, exitRequested;
        readonly bool testMode;
        Panel pageHome, pageApps, pageSettings;
        Label pageTitle, pageSubtitle, homeActivity;
        Button navHome, navApps, navSettings;
        readonly System.Windows.Forms.Timer offTimer = new System.Windows.Forms.Timer();
        readonly List<Font> ownedFonts = new List<Font>();
        ImageList rowHeights;
        Icon ownedIcon;
        CheckBox autoStartCheck, closeTrayCheck, forceCheck;

        RowComparer comparer = new RowComparer();
        ListViewItem hoverItem; int hoverCol = -1;

        public MainForm(bool startHidden) : this(startHidden, false) { }
        internal MainForm(bool startHidden, bool testMode)
        {
            this.testMode = testMode;
            suppressFirstShow = startHidden;
            Text = "EyesOff · 给屏幕放个假";
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(1100, 740);
            MinimumSize = new Size(1020, 720);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Ui.Canvas; ForeColor = Ui.Ink; DoubleBuffered = true;
            try { ownedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); Icon = ownedIcon; } catch { }
            BuildUi(); if (!testMode) BuildTray(); BuildMenu(); SetMode(false); ShowPage(0);
            offTimer.Interval = 700;
            offTimer.Tick += delegate { offTimer.Stop(); DoScreenOff(); btnScreenOff.Enabled = true; };
            SetLast("已就绪 · 打开应用清理时才扫描，不影响熄屏速度");
        }
        Font Face(float size, FontStyle style)
        {
            Font font = new Font("Microsoft YaHei UI", size, style);
            ownedFonts.Add(font); return font;
        }
        Label Caption(Control parent, string text, float size, Color color, int x, int y, int w, int h, bool bold)
        {
            Label label = new Label(); label.Text = text; label.ForeColor = color;
            label.Font = Face(size, bold ? FontStyle.Bold : FontStyle.Regular);
            label.SetBounds(x, y, w, h); label.BackColor = Color.Transparent;
            parent.Controls.Add(label); return label;
        }
        Button ActionButton(Control parent, string text, int x, int y, int w, int h, bool primary, EventHandler click)
        {
            SoftButton button = new SoftButton(); button.Text = text;
            button.SetBounds(x, y, w, h); button.Font = Face(9.5f, FontStyle.Bold);
            button.BackColor = primary ? Ui.Accent : Color.White;
            button.ForeColor = primary ? Ui.Navy : Ui.Ink;
            button.Click += click; parent.Controls.Add(button); return button;
        }
        void BuildUi()
        {
            Panel side = new Panel(); side.Dock = DockStyle.Left; side.Width = 184; side.BackColor = Ui.Navy;
            Controls.Add(side);
            BrandMark mark = new BrandMark(); mark.SetBounds(22, 30, 34, 34); side.Controls.Add(mark);
            Caption(side, "EyesOff", 18, Color.White, 64, 28, 116, 36, true);
            Caption(side, "少一点打扰，多一点专注", 8, Ui.SideMuted, 24, 80, 158, 24, false);
            navHome = ActionButton(side, "熄屏首页", 16, 140, 152, 46, false, delegate { ShowPage(0); });
            navApps = ActionButton(side, "应用清理", 16, 196, 152, 46, false, delegate { ShowPage(1); });
            navSettings = ActionButton(side, "偏好设置", 16, 252, 152, 46, false, delegate { ShowPage(2); });
            Label edition = Caption(side, "DESIGNED FOR QUIET\r\n\r\n轻一点，也好用一点。\r\nv2.1  /  本地运行", 8, Ui.SideMuted,
                24, ClientSize.Height - 136, 150, 105, false);
            edition.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Panel stage = new Panel(); stage.Dock = DockStyle.Fill; stage.Padding = new Padding(30, 22, 30, 18);
            Controls.Add(stage); stage.BringToFront();
            Panel header = new Panel(); header.Dock = DockStyle.Top; header.Height = 87;
            pageTitle = Caption(header, "给屏幕放个假", 22, Ui.Ink, 0, 0, 530, 44, true);
            pageSubtitle = Caption(header, "暂时关掉亮光，不打断正在进行的事。", 9, Ui.Muted, 1, 51, 620, 24, false);
            btnMenu = ActionButton(header, "更多  ···", header.Width - 100, 10, 96, 35, false,
                delegate { mainMenu.Show(btnMenu, new Point(0, btnMenu.Height)); });
            btnMenu.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Panel status = new Panel(); status.Dock = DockStyle.Bottom; status.Height = 31;
            lblLast = Caption(status, "就绪", 8, Ui.Muted, 0, 8, 740, 22, false);
            lblLast.Dock = DockStyle.Fill; lblLast.AutoEllipsis = true; lblLast.TextAlign = ContentAlignment.BottomLeft;
            Panel content = new Panel(); content.Dock = DockStyle.Fill;
            stage.Controls.Add(content); stage.Controls.Add(status); stage.Controls.Add(header);
            pageHome = new Panel(); pageHome.Dock = DockStyle.Fill;
            pageApps = new Panel(); pageApps.Dock = DockStyle.Fill;
            pageSettings = new Panel(); pageSettings.Dock = DockStyle.Fill; pageSettings.AutoScroll = true;
            content.Controls.Add(pageHome); content.Controls.Add(pageApps); content.Controls.Add(pageSettings);
            BuildHome(); BuildApps(); BuildPreferences();
        }
        void BuildHome()
        {
            TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1; layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68)); pageHome.Controls.Add(layout);
            SoftPanel hero = new SoftPanel(); hero.Dock = DockStyle.Fill; hero.BackColor = Ui.Navy;
            hero.Margin = new Padding(0); layout.Controls.Add(hero, 0, 0);
            Caption(hero, "SCREEN OFF  /  保持在线", 9, Ui.Accent, 30, 25, 330, 24, true);
            Caption(hero, "屏幕休息一下。\r\n事情照常进行。", 25, Color.White, 28, 62, 465, 96, true);
            lblScreenHint = Caption(hero, "下载、音乐和运行中的程序保持原样。\r\n移动鼠标或按任意键，即可唤醒屏幕。", 10,
                Color.FromArgb(179, 192, 202), 31, 166, 465, 46, false);
            btnScreenOff = ActionButton(hero, "立即熄屏   →", 30, hero.Height - 88, 190, 52, true,
                delegate { QueueScreenOff(); });
            btnScreenOff.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Label key = Caption(hero, "或按  Ctrl + Alt + L", 9, Color.FromArgb(179, 192, 202),
                237, hero.Height - 72, 220, 27, false); key.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            MonitorArt art = new MonitorArt(); art.SetBounds(hero.Width - 234, 65, 205, 196);
            art.Anchor = AnchorStyles.Top | AnchorStyles.Right; hero.Controls.Add(art);
            hero.Resize += delegate { art.Visible = hero.ClientSize.Width >= 750; };
            TableLayoutPanel quick = new TableLayoutPanel(); quick.Dock = DockStyle.Fill;
            quick.ColumnCount = 2; quick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            quick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); quick.Margin = new Padding(0);
            layout.Controls.Add(quick, 0, 2);
            SoftPanel first = new SoftPanel(); first.Dock = DockStyle.Fill; first.Margin = new Padding(0, 0, 8, 0);
            quick.Controls.Add(first, 0, 0);
            Caption(first, "随手就能打开", 12, Ui.Ink, 20, 16, 270, 28, true);
            Caption(first, "Ctrl + Alt + K  唤出控制台", 9, Ui.Muted, 20, 51, 310, 24, false);
            ActionButton(first, "设置快捷入口  →", 16, 80, 163, 30, false, delegate { ShowPage(2); });
            SoftPanel second = new SoftPanel(); second.Dock = DockStyle.Fill; second.Margin = new Padding(8, 0, 0, 0);
            quick.Controls.Add(second, 1, 0);
            Caption(second, "需要时，再做清理", 12, Ui.Ink, 20, 16, 290, 28, true);
            Caption(second, "先看清应用，再决定是否关闭。", 9, Ui.Muted, 20, 51, 310, 24, false);
            ActionButton(second, "查看后台应用  →", 16, 80, 163, 30, false, delegate { ShowPage(1); });
            homeActivity = Caption(layout, "●  只熄屏，不会自动清理。所有操作都在本机完成。", 9, Ui.Muted,
                0, 0, 720, 28, false);
            homeActivity.Dock = DockStyle.Fill; homeActivity.TextAlign = ContentAlignment.MiddleLeft;
            layout.SetCellPosition(homeActivity, new TableLayoutPanelCellPosition(0, 3));
        }
        void BuildApps()
        {
            Panel top = new Panel(); top.Dock = DockStyle.Top; top.Height = 62;
            txtSearch = new TextBox(); txtSearch.SetBounds(0, 13, 250, 28); txtSearch.Font = Face(10, FontStyle.Regular);
            top.Controls.Add(txtSearch); txtSearch.TextChanged += delegate { RenderRows(); };
            Caption(top, "搜索应用 / 发布者", 8, Ui.Muted, 0, 42, 250, 19, false);
            btnRefresh = ActionButton(top, "刷新", 266, 9, 78, 34, false, delegate { RefreshApps(); });
            btnMode = ActionButton(top, "进程详情", 356, 9, 112, 34, false, delegate { ToggleMode(); });
            lblWarn = new Label(); lblWarn.Text = "进程详情：结束程序可能丢失未保存内容，请确认后操作。";
            lblWarn.Dock = DockStyle.Top; lblWarn.Height = 36; lblWarn.Padding = new Padding(10, 7, 0, 0);
            lblWarn.BackColor = Color.FromArgb(255, 244, 222); lblWarn.ForeColor = Color.FromArgb(145, 94, 32);
            lblWarn.Visible = false;
            lv = new ListView(); lv.Dock = DockStyle.Fill; lv.View = View.Details;
            lv.FullRowSelect = true; lv.MultiSelect = false; lv.HideSelection = false;
            lv.HeaderStyle = ColumnHeaderStyle.Clickable; lv.OwnerDraw = true; lv.CheckBoxes = false;
            lv.ShowItemToolTips = true; lv.BorderStyle = BorderStyle.None; lv.BackColor = Color.White;
            lv.ForeColor = Ui.Ink; lv.Font = Face(9, FontStyle.Regular);
            rowHeights = new ImageList(); rowHeights.ImageSize = new Size(1, 38); lv.SmallImageList = rowHeights;
            try
            {
                PropertyInfo pi = typeof(ListView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(lv, true, null);
            }
            catch { }
            lv.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(238, 242, 244))) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, Rectangle.Inflate(e.Bounds, -5, 0), Ui.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            lv.DrawSubItem += LvDrawSubItem; lv.ColumnClick += LvColumnClick; lv.MouseMove += LvMouseMove;
            lv.MouseLeave += delegate { SetHover(null, -1); }; lv.MouseUp += LvMouseUp;
            lv.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space && !busy && lv.SelectedItems.Count == 1)
                {
                    RowModel row = lv.SelectedItems[0].Tag as RowModel;
                    if (row != null && row.Checkable)
                    {
                        object key = row.InAdvanced ? (object)row.Proc : row.App;
                        row.Checked = !row.Checked;
                        if (row.Checked) checkedKeys.Add(key); else checkedKeys.Remove(key);
                        lv.Invalidate(); RefreshUiState();
                    }
                    e.Handled = true; e.SuppressKeyPress = true;
                }
            };
            lv.Resize += delegate { AdjustFlexColumn(); };
            Panel footer = new Panel(); footer.Dock = DockStyle.Bottom; footer.Height = 65;
            lblChecked = Caption(footer, "已选：0", 9, Ui.Muted, 0, 23, 260, 24, false);
            btnEndChecked = ActionButton(footer, "关闭选中", footer.Width - 278, 15, 122, 38, false,
                delegate { EndChecked(); });
            btnCleanRec = ActionButton(footer, "检查推荐项", footer.Width - 144, 15, 142, 38, true,
                delegate { CleanRecommended(); });
            btnEndChecked.Anchor = btnCleanRec.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            pageApps.Controls.Add(lv); pageApps.Controls.Add(lblWarn); pageApps.Controls.Add(top); pageApps.Controls.Add(footer);
            comparer = new RowComparer();
        }
        void BuildPreferences()
        {
            SoftPanel launch = new SoftPanel(); launch.SetBounds(0, 0, pageSettings.ClientSize.Width, 220);
            launch.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; pageSettings.Controls.Add(launch);
            Caption(launch, "打开它，不用费劲", 15, Ui.Ink, 24, 18, 650, 34, true);
            Caption(launch, "控制台和立即熄屏分成两个入口，不再多点一步。", 9, Ui.Muted, 25, 59, 690, 26, false);
            ActionButton(launch, "创建 / 修复快捷方式", 24, 99, 210, 40, true, delegate
            {
                string result = ShortcutHelper.EnsureShortcuts(); SetLast(result);
                MessageBox.Show(this, result, "快捷入口", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            autoStartCheck = PreferenceCheck(launch, "登录 Windows 后在托盘待命", 25, 162, SettingsStore.StartWithWindows);
            autoStartCheck.CheckedChanged += delegate
            {
                if (syncingPreferences) return;
                try { SettingsStore.SetStartWithWindows(autoStartCheck.Checked); SetLast(autoStartCheck.Checked ? "已开启登录后托盘启动" : "已关闭登录后托盘启动"); }
                catch (Exception ex) { SetLast("启动设置未保存：" + ex.Message); SyncPreferences(); }
            };
            SoftPanel behavior = new SoftPanel(); behavior.SetBounds(0, 237, pageSettings.ClientSize.Width, 143);
            behavior.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; pageSettings.Controls.Add(behavior);
            Caption(behavior, "安静待在后台", 15, Ui.Ink, 24, 16, 650, 34, true);
            closeTrayCheck = PreferenceCheck(behavior, "关闭窗口时收进托盘，而不是退出", 25, 65, SettingsStore.CloseToTray);
            closeTrayCheck.CheckedChanged += delegate
            {
                if (syncingPreferences) return;
                try { SettingsStore.SetCloseToTray(closeTrayCheck.Checked); }
                catch (Exception ex) { SetLast("设置未保存：" + ex.Message); SyncPreferences(); }
            };
            Caption(behavior, "右键托盘图标 → 退出，可完全关闭程序。", 9, Ui.Muted, 45, 103, 680, 24, false);
            SoftPanel safety = new SoftPanel(); safety.SetBounds(0, 397, pageSettings.ClientSize.Width, 154);
            safety.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; pageSettings.Controls.Add(safety);
            Caption(safety, "清理安全", 15, Ui.Ink, 24, 15, 650, 34, true);
            Caption(safety, "强制结束可能丢失未保存内容。只有开关开启且当次确认后才执行。", 9, Ui.Muted, 25, 57, 720, 27, false);
            forceCheck = PreferenceCheck(safety, "允许强制结束（仍需在每次操作时确认）", 25, 103, SettingsStore.AllowForceKill);
            forceCheck.CheckedChanged += delegate
            {
                if (syncingPreferences) return;
                SettingsStore.SetAllowForceKill(forceCheck.Checked); RefreshUiState();
            };
        }
        bool syncingPreferences;
        CheckBox PreferenceCheck(Control parent, string text, int x, int y, bool value)
        {
            CheckBox check = new CheckBox(); check.Text = text; check.SetBounds(x, y, 710, 29);
            check.Font = Face(9.5f, FontStyle.Regular); check.ForeColor = Ui.Ink; check.Checked = value;
            parent.Controls.Add(check); return check;
        }
        void SyncPreferences()
        {
            syncingPreferences = true;
            try
            {
                autoStartCheck.Checked = SettingsStore.StartWithWindows;
                closeTrayCheck.Checked = SettingsStore.CloseToTray;
                forceCheck.Checked = SettingsStore.AllowForceKill;
            }
            finally { syncingPreferences = false; }
        }
        void ShowPage(int index)
        {
            pageHome.Visible = index == 0; pageApps.Visible = index == 1; pageSettings.Visible = index == 2;
            Button[] nav = { navHome, navApps, navSettings };
            for (int i = 0; i < nav.Length; i++)
            {
                nav[i].BackColor = i == index ? Color.FromArgb(40, 60, 69) : Ui.Navy;
                nav[i].ForeColor = i == index ? Ui.Accent : Color.FromArgb(184, 197, 204); nav[i].Invalidate();
            }
            pageTitle.Text = index == 0 ? "给屏幕放个假" : index == 1 ? "后台应用，心里有数" : "让它更顺手";
            pageSubtitle.Text = index == 0 ? "暂时关掉亮光，不打断正在进行的事。" :
                index == 1 ? "只读扫描不会关闭应用；任何批量关闭都需要你确认。" : "快捷入口、托盘和启动方式，由你决定。";
            if (index == 1 && model == null && !busy) RefreshApps();
            if (index == 2) SyncPreferences();
        }

        void BuildTray()
        {
            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem miOff = new ToolStripMenuItem("熄屏");
            miOff.Click += delegate { QueueScreenOff(); };
            ToolStripMenuItem miShow = new ToolStripMenuItem("显示窗口");
            miShow.Click += delegate { ShowMainWindow(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { exitRequested = true; Close(); };
            trayMenu.Items.Add(miOff);
            trayMenu.Items.Add(miShow);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miExit);

            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.Text = "EyesOff · Ctrl+Alt+L 熄屏";
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowMainWindow(); };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
        }

        void BuildMenu()
        {
            mainMenu = new ContextMenuStrip();
            ToolStripMenuItem miSetting = new ToolStripMenuItem("设置（允许强制结束…）");
            miSetting.Click += delegate { ShowSettings(); };
            ToolStripMenuItem miProtect = new ToolStripMenuItem("打开保护名单");
            miProtect.Click += delegate { OpenProtectFile(); };
            ToolStripMenuItem miFolder = new ToolStripMenuItem("打开所在文件夹");
            miFolder.Click += delegate { OpenFolder(); };
            ToolStripMenuItem miLnk = new ToolStripMenuItem("创建/修复桌面快捷方式");
            miLnk.Click += delegate
            {
                string r = ShortcutHelper.EnsureShortcuts();
                MessageBox.Show(this, r, "快捷方式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            mainMenu.Items.Add(miSetting);
            mainMenu.Items.Add(new ToolStripSeparator());
            mainMenu.Items.Add(miProtect);
            mainMenu.Items.Add(miFolder);
            mainMenu.Items.Add(miLnk);
        }

        // ====================================================================
        // 模式切换：简洁模式（应用视图，三区） / 高级模式（进程视图）
        // ====================================================================
        void ToggleMode()
        {
            SetMode(!advancedMode);
        }

        void SetMode(bool adv)
        {
            advancedMode = adv;
            btnMode.Text = adv ? "返回应用视图" : "进程详情";
            lblWarn.Visible = adv;
            lv.Groups.Clear();
            lv.Columns.Clear();

            if (advancedMode)
            {
                lv.Columns.Add("应用名称", 150);
                lv.Columns.Add("原始进程名", 95);
                lv.Columns.Add("PID", 55);
                lv.Columns.Add("内存(MB)", 65);
                lv.Columns.Add("会话ID", 45);
                lv.Columns.Add("可见窗口", 60);
                lv.Columns.Add("当前状态", 90);
                lv.Columns.Add("完整路径", 210);
                lv.Columns.Add("详情", 46);
                lv.Columns.Add("结束", 46);
            }
            else
            {
                lv.Columns.Add("应用名称", 175);
                lv.Columns.Add("发布者", 95);
                lv.Columns.Add("用途或说明", 150);
                lv.Columns.Add("当前状态", 90);
                lv.Columns.Add("内存(MB)", 65);
                lv.Columns.Add("进程数", 45);
                lv.Columns.Add("风险提示", 150);
                lv.Columns.Add("详情", 46);
                lv.Columns.Add("结束", 46);
            }
            AdjustFlexColumn();
            RenderRows();
        }

        // ====================================================================
        // 刷新（后台线程扫描，UI 线程渲染）
        // ====================================================================
        void RefreshApps()
        {
            if (busy) return;
            busy = true;
            UseWaitCursor = true;
            RefreshUiState();
            SetLast("正在扫描应用…");

            Task.Run(delegate
            {
                AppModel.Model m = null;
                string scanError = null;
                try { m = AppModel.Build(ProcessScanner.ScanAll()); }
                catch (Exception ex) { scanError = ex.Message; }
                if (IsHandleCreated && !IsDisposed)
                {
                    try { BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        if (m == null)
                        {
                            busy = false; UseWaitCursor = false; RefreshUiState();
                            SetLast("扫描未完成：" + scanError + "。可以点击刷新重试。"); return;
                        }
                        model = m;
                        checkedKeys.Clear();     // 需求 11：刷新后不沿用旧选择
                        busy = false;
                        UseWaitCursor = false;
                        RenderRows();
                        RefreshUiState();

                        int inUse = 0, bg = 0, prot = 0, unk = 0, up = 0;
                        foreach (AppEntry a in m.Apps)
                        {
                            if (a.Status == AppStatus.InUse) inUse++;
                            else if (a.Status == AppStatus.Background) bg++;
                            else if (a.Status == AppStatus.UserProtected) up++;
                            else if (a.Status == AppStatus.Unknown) unk++;
                            else prot++;
                        }
                        SetLast("已刷新（" + DateTime.Now.ToString("HH:mm:ss") + "）：系统共 " + m.TotalProcCount +
                            " 个进程；正在使用 " + inUse + "，后台可清理 " + bg + "，用户保护 " + up +
                            "，受保护/未知 " + (prot + unk) + "（仅在高级模式查看）");
                    }); } catch (InvalidOperationException) { }
                }
            });
        }

        // ====================================================================
        // 行渲染：按模式 + 搜索框过滤，重建 ListView
        // ====================================================================
        void RenderRows()
        {
            if (model == null) { lv.BeginUpdate(); lv.Items.Clear(); lv.EndUpdate(); RefreshUiState(); return; }
            string q = (txtSearch.Text ?? "").Trim().ToLowerInvariant();

            rows.Clear();
            List<ListViewGroup> groups = new List<ListViewGroup>();
            ListViewGroup gInUse = null, gBg = null, gRec = null, gOther = null;

            if (!advancedMode)
            {
                gInUse = new ListViewGroup("正在使用（已保护）");
                gBg = new ListViewGroup("后台运行（受保护，不可清理）");
                gRec = new ListViewGroup("可清理应用（后台，默认全部未勾选）");
                gOther = new ListViewGroup("待确认/受规则影响（请查看详情）");
                groups.Add(gInUse); groups.Add(gBg); groups.Add(gRec); groups.Add(gOther);
            }

            foreach (AppEntry app in model.Apps)
            {
                if (advancedMode)
                {
                    // 高级模式：一行一个进程（需求：显示 PID/路径/会话等技术信息）
                    foreach (ProcSnapshot p in app.Procs)
                    {
                        RowModel r = new RowModel();
                        r.App = app; r.Proc = p; r.InAdvanced = true;
                        r.Checkable = p.CanEnd && p.Status != AppStatus.InUse;
                        r.Endable = p.CanEnd && p.Status != AppStatus.InUse;
                        r.Checked = r.Checkable && checkedKeys.Contains(p);
                        if (MatchSearch(app, p)) rows.Add(r);
                    }
                }
                else
                {
                    // 简洁模式：一行一个应用，按三分区归类
                    RowModel r = new RowModel();
                    r.App = app; r.InAdvanced = false;
                    if (app.Status == AppStatus.InUse) { r.Group = gInUse; r.Checkable = false; r.Endable = false; }
                    else if (app.Status == AppStatus.Background) { r.Group = gRec; r.Checkable = app.AllCanEnd; r.Endable = app.AllCanEnd; }
                    else { r.Group = gOther; r.Checkable = app.AllCanEnd; r.Endable = app.AllCanEnd; }
                    r.Checked = r.Checkable && checkedKeys.Contains(app);
                    if (MatchSearch(app, null)) rows.Add(r);
                }
            }

            lv.BeginUpdate();
            lv.Items.Clear();
            lv.Groups.Clear();
            if (!advancedMode) lv.Groups.AddRange(groups.ToArray());

            foreach (RowModel r in rows)
            {
                ListViewItem it = new ListViewItem();
                it.Tag = r;
                if (r.Group != null) it.Group = r.Group;
                it.UseItemStyleForSubItems = false;

                if (r.InAdvanced)
                {
                    ProcSnapshot p = r.Proc;
                    it.Text = r.App.DisplayName;
                    it.SubItems.Add(p.Name);
                    it.SubItems.Add(p.Pid.ToString());
                    it.SubItems.Add((p.Memory / 1048576.0).ToString("F1"));
                    it.SubItems.Add(p.SessionId.ToString());
                    it.SubItems.Add((p.HasWindow || p.IsForeground) ? "有" : "无");
                    it.SubItems.Add(StatusTextOf(p));
                    it.SubItems.Add(p.ExePath ?? "（无法读取）");
                    it.SubItems.Add("详情");
                    it.SubItems.Add(r.Endable ? "结束" : "—");
                    it.ToolTipText = p.ExePath ?? "";
                }
                else
                {
                    AppEntry a = r.App;
                    it.Text = a.DisplayName;
                    it.SubItems.Add(a.Publisher);
                    it.SubItems.Add(a.Desc);
                    it.SubItems.Add(a.StatusText);
                    it.SubItems.Add((a.TotalMem / 1048576.0).ToString("F1"));
                    it.SubItems.Add(a.Procs.Count.ToString());
                    it.SubItems.Add(a.RiskHint);
                    it.SubItems.Add("详情");
                    it.SubItems.Add(r.Endable ? "结束" : "—");
                    it.ToolTipText = a.Procs.Count > 0 ? (a.Procs[0].ExePath ?? "") : "";
                }
                lv.Items.Add(it);
            }

            // 默认按内存从高到低，方便先看到最占资源的
            comparer.SortKey = RowComparer.ByMem; comparer.Desc = true;
            lv.ListViewItemSorter = null;
            lv.ListViewItemSorter = comparer;
            lv.Sort();
            lv.EndUpdate();
            AdjustFlexColumn();
            RefreshUiState();
        }

        static string StatusTextOf(ProcSnapshot p)
        {
            switch (p.Status)
            {
                case AppStatus.InUse: return "正在使用";
                case AppStatus.UserProtected: return "用户保护";
                case AppStatus.Background: return "后台运行";
                case AppStatus.Unknown: return "未知应用";
                case AppStatus.Blacklisted: return "系统关键进程";
                default: return "受保护" + (string.IsNullOrEmpty(p.Reason) ? "" : "（" + p.Reason + "）");
            }
        }

        // 搜索：匹配 应用名称 / 发布者 / 原始进程名（不区分大小写，子串匹配）
        bool MatchSearch(AppEntry app, ProcSnapshot p)
        {
            string q = (txtSearch.Text ?? "").Trim();
            if (q.Length == 0) return true;
            q = q.ToLowerInvariant();
            if (app.DisplayName != null && app.DisplayName.ToLowerInvariant().Contains(q)) return true;
            if (app.Publisher != null && app.Publisher.ToLowerInvariant().Contains(q)) return true;
            foreach (ProcSnapshot pp in app.Procs)
            {
                if (pp.Name != null && pp.Name.ToLowerInvariant().Contains(q)) return true;
            }
            return false;
        }

        // 路径列（高级模式）吃掉剩余宽度，避免横向滚动
        void AdjustFlexColumn()
        {
            if (lv.Columns.Count == 0 || lv.ClientSize.Width <= 0) return;
            if (advancedMode)
            {
                int[] widths = { 152, 104, 56, 74, 56, 68, 100, 200, 50, 50 };
                for (int i = 0; i < widths.Length && i < lv.Columns.Count; i++) lv.Columns[i].Width = widths[i];
            }
            else
            {
                int width = Math.Max(650, lv.ClientSize.Width - 4);
                int[] widths = { Math.Max(168, width - 482), 120, 0, 116, 82, 60, 0, 52, 52 };
                for (int i = 0; i < widths.Length && i < lv.Columns.Count; i++) lv.Columns[i].Width = widths[i];
            }
        }

        // 表头点击排序（按当前模式的列映射到排序键）
        void LvColumnClick(object sender, ColumnClickEventArgs e)
        {
            int key;
            if (advancedMode)
            {
                if (e.Column == A_PID) key = RowComparer.ByPid;
                else if (e.Column == A_MEM) key = RowComparer.ByMem;
                else key = RowComparer.ByName;
            }
            else
            {
                if (e.Column == C_MEM) key = RowComparer.ByMem;
                else if (e.Column == C_PUB) key = RowComparer.ByPub;
                else key = RowComparer.ByName;
            }
            if (comparer.SortKey == key) comparer.Desc = !comparer.Desc;
            else { comparer.SortKey = key; comparer.Desc = (key == RowComparer.ByMem); }
            lv.ListViewItemSorter = comparer;
            lv.Sort();
        }

        // ====================================================================
        // 自绘：勾选框 + 图标 + 名称（第 0 列），其余列系统默认；详情/结束为按钮列
        // 为什么自绘勾选框：ListView 自带勾选框与图标、分区的组合行为不可控，
        // 自绘可以保证"受保护项的勾选框禁用"等交互确定无误。
        // ====================================================================
        void LvDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            RowModel r = e.Item.Tag as RowModel;
            if (e.ColumnIndex == 0 && r != null)
            {
                bool selected = e.Item.Selected;
                Color back = selected ? Color.FromArgb(225, 245, 240) : Color.White;
                using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
                Color fore = Ui.Ink;

                int y = e.Bounds.Y + (e.Bounds.Height - 13) / 2;
                Point cp = new Point(e.Bounds.X + 3, y);
                System.Windows.Forms.VisualStyles.CheckBoxState cs;
                if (!r.Checkable) cs = System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled;
                else cs = r.Checked
                    ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal
                    : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal;
                CheckBoxRenderer.DrawCheckBox(e.Graphics, cp, cs);

                int ix = e.Bounds.X + 20;
                int iy = e.Bounds.Y + (e.Bounds.Height - 16) / 2;
                if (r.App.AppIcon != null)
                    e.Graphics.DrawIcon(r.App.AppIcon, new Rectangle(ix, iy, 16, 16));

                string text = r.InAdvanced ? r.App.DisplayName : r.App.DisplayName;
                Rectangle tr = new Rectangle(e.Bounds.X + 40, e.Bounds.Y, e.Bounds.Width - 44, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, text, lv.Font, tr, fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            if (r != null && e.ColumnIndex == ((r.InAdvanced) ? A_DETAIL : C_DETAIL))
            {
                DrawMiniButton(e, "详情", true);
                return;
            }
            if (r != null && e.ColumnIndex == ((r.InAdvanced) ? A_END : C_END))
            {
                DrawMiniButton(e, "结束", r.Endable && !busy);
                return;
            }
            using (SolidBrush brush = new SolidBrush(e.Item.Selected ? Color.FromArgb(225, 245, 240) : Color.White))
                e.Graphics.FillRectangle(brush, e.Bounds);
            bool isStatus = e.ColumnIndex == (advancedMode ? A_STATUS : C_STATUS);
            Color color = isStatus && r != null && !r.Endable ? Ui.Muted : Ui.Ink;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, Rectangle.Inflate(e.Bounds, -4, 0), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        void DrawMiniButton(DrawListViewSubItemEventArgs e, string text, bool enabled)
        {
            e.Graphics.FillRectangle(Brushes.White, e.Bounds);
            Rectangle rect = Rectangle.Inflate(e.Bounds, -4, -6);
            bool hover = enabled && hoverItem == e.Item && hoverCol == e.ColumnIndex;
            Color back = hover ? Color.FromArgb(224, 246, 239) : Color.FromArgb(244, 247, 248);
            using (GraphicsPath path = Ui.Round(rect, 6))
            using (SolidBrush brush = new SolidBrush(back))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
            TextRenderer.DrawText(e.Graphics, enabled ? text : "—", lv.Font, rect,
                enabled ? Ui.Ink : Ui.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void SetHover(ListViewItem item, int col)
        {
            if (hoverItem == item && hoverCol == col) return;
            hoverItem = item; hoverCol = col;
            lv.Invalidate();
        }

        void LvMouseMove(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo h = lv.HitTest(e.Location);
            RowModel r = (h.Item != null) ? h.Item.Tag as RowModel : null;
            int col = (h.SubItem != null && h.Item != null) ? h.Item.SubItems.IndexOf(h.SubItem) : -1;
            bool btn = false;
            if (r != null && (col == (r.InAdvanced ? A_DETAIL : C_DETAIL))) btn = true;
            if (r != null && (col == (r.InAdvanced ? A_END : C_END)) && r.Endable && !busy) btn = true;
            if (r != null && col == 0 && r.Checkable) btn = true;
            SetHover(btn ? h.Item : null, btn ? col : -1);
            lv.Cursor = btn ? Cursors.Hand : Cursors.Default;
        }

        void LvMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || busy) return;
            ListViewHitTestInfo h = lv.HitTest(e.Location);
            if (h.Item == null) return;
            RowModel r = h.Item.Tag as RowModel;
            if (r == null) return;
            int col = (h.SubItem != null) ? h.Item.SubItems.IndexOf(h.SubItem) : -1;

            if (col == 0 && r.Checkable)
            {
                // Clicking the app-name cell toggles selection; Space also works.
                if (col == 0)
                {
                    object key = r.InAdvanced ? (object)r.Proc : (object)r.App;
                    if (r.Checked) checkedKeys.Remove(key);
                    else checkedKeys.Add(key);
                    r.Checked = !r.Checked;
                    lv.Invalidate(h.Item.Bounds);
                    RefreshUiState();
                }
                return;
            }
            if (col == (r.InAdvanced ? A_DETAIL : C_DETAIL))
            {
                ShowDetails(r.App);
                return;
            }
            if (col == (r.InAdvanced ? A_END : C_END) && r.Endable)
            {
                EndTask t = BuildTaskFromRow(r);
                RunEndFlow(new List<EndTask> { t }, "结束应用");
                return;
            }
        }

        // 由行构造结束任务。简洁模式 = 整个应用；高级模式 = 该行对应的那一个进程。
        EndTask BuildTaskFromRow(RowModel r)
        {
            EndTask t = new EndTask();
            t.AppName = r.App.DisplayName;
            List<ProcSnapshot> procs = r.InAdvanced ? new List<ProcSnapshot> { r.Proc } : r.App.Procs;
            t.Procs = procs;
            long mem = 0;
            foreach (ProcSnapshot p in procs) mem += p.Memory;
            t.Mem = mem;
            t.StatusText = r.InAdvanced ? StatusTextOf(r.Proc) : r.App.StatusText;
            foreach (ProcSnapshot p in procs) if (p.HasWindow || p.IsForeground) { t.HadWindow = true; break; }
            return t;
        }

        // ====================================================================
        // 底部操作
        // ====================================================================
        void RefreshUiState()
        {
            int n = 0;
            foreach (RowModel r in rows) if (r.Checked) n++;
            lblChecked.Text = "已选：" + n;
            btnRefresh.Enabled = !busy;
            btnEndChecked.Enabled = !busy && n > 0;
            btnCleanRec.Enabled = !busy && model != null;
            btnScreenOff.Enabled = !offTimer.Enabled;     // 熄屏与清理互不干扰
        }

        // 收集勾选的行 → 构造结束任务
        List<EndTask> CollectCheckedTasks()
        {
            List<EndTask> list = new List<EndTask>();
            if (!advancedMode)
            {
                // 简洁模式：勾选的应用整体作为一个任务
                foreach (RowModel r in rows)
                    if (r.Checked) list.Add(BuildTaskFromRow(r));
                return list;
            }
            // 高级模式：勾选的进程按应用聚合（一个应用的多个勾选进程合为一个任务）
            Dictionary<string, EndTask> byApp = new Dictionary<string, EndTask>(StringComparer.OrdinalIgnoreCase);
            foreach (RowModel r in rows)
            {
                if (!r.Checked) continue;
                EndTask t;
                if (!byApp.TryGetValue(r.App.Key, out t))
                {
                    t = new EndTask();
                    t.AppName = r.App.DisplayName;
                    t.StatusText = StatusTextOf(r.Proc);
                    byApp[r.App.Key] = t;
                }
                t.Procs.Add(r.Proc);
            }
            List<EndTask> result = new List<EndTask>(byApp.Values);
            foreach (EndTask t in result)
            {
                long mem = 0;
                foreach (ProcSnapshot p in t.Procs) mem += p.Memory;
                t.Mem = mem;
                foreach (ProcSnapshot p in t.Procs) if (p.HasWindow || p.IsForeground) { t.HadWindow = true; break; }
            }
            return result;
        }

        void EndChecked()
        {
            if (busy) return;
            List<EndTask> tasks = CollectCheckedTasks();
            if (tasks.Count == 0)
            {
                MessageBox.Show(this, "请先在\"可清理应用\"区勾选要关闭的应用。", "结束选中",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RunEndFlow(tasks, "结束选中");
        }

        // 清理推荐项：只处理简洁模式"可清理应用"区里的应用（需求硬性约束）
        void CleanRecommended()
        {
            if (busy || model == null) return;
            List<EndTask> tasks = new List<EndTask>();
            foreach (AppEntry app in model.Apps)
            {
                if (app.Status != AppStatus.Background) continue;   // 只处理推荐区域
                EndTask t = new EndTask();
                t.AppName = app.DisplayName;
                t.Procs = app.Procs;
                t.Mem = app.TotalMem;
                t.StatusText = app.StatusText;
                t.HadWindow = app.HasWindow;
                tasks.Add(t);
            }
            if (tasks.Count == 0)
            {
                MessageBox.Show(this, "当前没有推荐清理的应用。", "清理推荐项",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RunEndFlow(tasks, "清理推荐项");
        }

        // ====================================================================
        // 结束流程：人话确认框 → 温和关闭 → （可选）强制结束 → 按应用汇总
        // ====================================================================
        void RunEndFlow(List<EndTask> tasks, string opName)
        {
            if (busy || tasks.Count == 0) return;
            bool allowForce = SettingsStore.AllowForceKill;

            // 需求第九条：批量操作必须展示应用清单并弹确认框（单个也一样，统一交互）
            bool forceOn = allowForce;   // 设置开启时，确认框要求勾选"允许强制结束"再确认
            DialogResult dr = ConfirmDialog.Show(this, opName, tasks, forceOn);
            if (dr != DialogResult.OK)
            {
                SetLast("已取消：" + opName + "（" + DateTime.Now.ToString("HH:mm:ss") + "），未关闭任何应用");
                return;
            }
            bool userForce = forceOn;    // 走到这里说明确认框里已勾选（确认按钮受勾选联动控制）

            busy = true;
            RefreshUiState();
            SetLast(opName + "执行中…（共 " + tasks.Count + " 个应用）");

            Task.Run(delegate
            {
                List<EndOutcome> outcomes = new List<EndOutcome>();
                List<EndTask> stubborn = new List<EndTask>();

                // 第一阶段：温和关闭（有窗口 → CloseMainWindow → 等待 3 秒复查）
                try { AppEndRunner.GentlePhase(tasks, outcomes, stubborn); }
                catch (Exception ex)
                {
                    outcomes.Add(new EndOutcome { AppName = opName, Text = "温和关闭阶段异常：" + ex.Message, Ok = false });
                }

                // 第二阶段：强制结束决策。
                // 默认不 Kill；只有"设置已开启 + 确认框已勾选"才执行强制结束。
                if (stubborn.Count > 0)
                {
                    if (userForce)
                    {
                        foreach (EndTask t in stubborn)
                        {
                            try { outcomes.Add(AppEndRunner.ForceEnd(t)); }
                            catch (Exception ex)
                            {
                                outcomes.Add(new EndOutcome { AppName = t.AppName, Text = "强制结束异常：" + ex.Message, Ok = false });
                            }
                        }
                    }
                    else
                    {
                        foreach (EndTask t in stubborn)
                            outcomes.Add(AppEndRunner.NoForceOutcome(t));
                    }
                }

                List<EndOutcome> done = outcomes;
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false;
                        RefreshApps();     // 结束后自动刷新（重建模型，不沿用旧选择）
                        ShowReport(opName, done);
                    });
                }
            });
        }

        void ShowReport(string opName, List<EndOutcome> outcomes)
        {
            int ok = 0, fail = 0;
            foreach (EndOutcome o in outcomes) if (o.Ok) ok++; else fail++;
            SetLast(opName + " 完成：成功 " + ok + " 个，失败/未结束 " + fail + " 个（" + DateTime.Now.ToString("HH:mm:ss") + "）");

            StringBuilder sb = new StringBuilder();
            sb.Append(opName).Append(" 完成：成功 ").Append(ok).Append(" 个，失败/未结束 ").Append(fail).Append(" 个。\r\n\r\n");
            foreach (EndOutcome o in outcomes)
            {
                sb.Append("• ").Append(o.AppName).Append("：").Append(o.Text).Append("\r\n");
            }
            MessageBox.Show(this, sb.ToString(), opName + " 结果",
                MessageBoxButtons.OK, fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        // ====================================================================
        // 确认框 / 详情 / 设置
        // ====================================================================
        void ShowDetails(AppEntry app)
        {
            DetailsDialog.Show(this, app);
        }

        void ShowSettings()
        {
            ShowPage(2);
        }

        void OpenProtectFile()
        {
            ProtectList.EnsureLoaded();
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ProtectList.FilePath,
                    UseShellExecute = true
                });
            }
            catch
            {
                // 关联程序异常时退回记事本
                try { Process.Start("notepad.exe", "\"" + ProtectList.FilePath + "\""); } catch { }
            }
        }

        void OpenFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        // ====================================================================
        // 熄屏（逻辑与 v1 一致，主方案 + 两级备用）
        // ====================================================================
        public void QueueScreenOff()
        {
            btnScreenOff.Enabled = false;
            SetLast("即将熄屏 · 移开鼠标后保持不动，按键或移动鼠标可唤醒");
            offTimer.Stop();
            if (!testMode) offTimer.Start();
        }

        void DoScreenOff()
        {
            if (testMode) return;
            // 先写状态栏：屏幕黑掉之后用户看不到任何界面，恢复后能知道发生了什么
            SetLast("已发送熄屏指令（" + DateTime.Now.ToString("HH:mm:ss") +
                "）：熄屏不会关闭程序，移动鼠标或按键即可恢复");
            if (NativeMethods.TurnOffMonitorByBroadcast()) return;
            NativeMethods.TurnOffMonitorByOwnWindow(Handle);
            NativeMethods.TurnOffMonitorByPowerShell();
        }

        // ====================================================================
        // 全局热键（P1，保留）：Ctrl+Alt+L 熄屏 / Ctrl+Alt+K 唤出窗口
        // ====================================================================
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (testMode) return;
            bool r1 = NativeMethods.RegisterHotKey(Handle, HOTKEY_ID_SCREEN, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 'L');
            bool r2 = NativeMethods.RegisterHotKey(Handle, HOTKEY_ID_SHOW, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 'K');
            if (!r1 || !r2)
                SetLast("就绪。提示：部分全局热键注册失败（可能已被其他软件占用），按钮功能不受影响");
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (!testMode)
            {
                try { NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_SCREEN); } catch { }
                try { NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID_SHOW); } catch { }
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            // 先交给基类：备用熄屏方案依赖 DefWindowProc 对 WM_SYSCOMMAND 的默认处理
            base.WndProc(ref m);
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_ID_SCREEN) QueueScreenOff();
                else if (id == HOTKEY_ID_SHOW) ShowMainWindow();
            }
        }

        public void Present() { ShowMainWindow(); }
        void ShowMainWindow()
        {
            suppressFirstShow = false;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
        protected override void SetVisibleCore(bool value)
        {
            if (value && suppressFirstShow) { suppressFirstShow = false; value = false; }
            base.SetVisibleCore(value);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) HideToTray();
        }
        void HideToTray()
        {
            Hide();
            if (!balloonShown && tray != null)
            {
                tray.ShowBalloonTip(2000, "EyesOff 仍在待命",
                    "Ctrl+Alt+L 熄屏，Ctrl+Alt+K 打开。右键托盘图标可以退出。", ToolTipIcon.Info);
                balloonShown = true;
            }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !exitRequested && SettingsStore.CloseToTray)
            { e.Cancel = true; HideToTray(); return; }
            offTimer.Stop();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                offTimer.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                if (trayMenu != null) trayMenu.Dispose();
                if (mainMenu != null) mainMenu.Dispose();
                if (rowHeights != null) rowHeights.Dispose();
                foreach (Font font in ownedFonts) font.Dispose();
                if (ownedIcon != null) ownedIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        void SetLast(string text)
        {
            if (lblLast != null) lblLast.Text = text;
        }
    }

    // ========================================================================
    // 确认对话框：人话清单 + 三按钮（取消 / 返回检查 / 确认结束）
    // 需求：不能只显示"即将结束 N 个进程"，必须列出应用名、状态、进程数、内存。
    // ========================================================================
    internal class ConfirmDialog : Form
    {
        CheckBox chkForce;
        Button btnOk;

        // 返回 OK = 确认结束；其他 = 取消/返回检查（两者都不执行任何操作）。
        // needForceConfirm=true 表示用户已在设置中开启"允许强制结束"，
        // 此时必须勾选强制结束确认才能点"确认结束"——这就是 Kill 前的"再次确认"。
        public static DialogResult Show(IWin32Window owner, string opName, List<EndTask> tasks, bool needForceConfirm)
        {
            using (ConfirmDialog dlg = new ConfirmDialog(opName, tasks, needForceConfirm))
            {
                return dlg.ShowDialog(owner);
            }
        }

        ConfirmDialog(string opName, List<EndTask> tasks, bool needForceConfirm)
        {
            Text = opName + " — 确认";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 330);
            ShowInTaskbar = false;

            Label intro = new Label();
            intro.Text = "即将关闭以下应用：";
            intro.SetBounds(14, 12, 480, 20);
            Controls.Add(intro);

            ListBox list = new ListBox();
            list.SetBounds(14, 38, 490, 160);
            list.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f);
            foreach (EndTask t in tasks)
            {
                // 人话格式："微信：后台运行，2 个进程，占用 320 MB"
                list.Items.Add("• " + t.AppName + "：" + t.StatusText + "，" +
                    t.Procs.Count + " 个进程，占用 " + (t.Mem / 1048576.0).ToString("F0") + " MB");
            }
            Controls.Add(list);

            Label warn = new Label();
            warn.Text = "提醒：如果这些应用有未保存内容，关闭后可能丢失。";
            warn.ForeColor = Color.FromArgb(160, 60, 0);
            warn.SetBounds(14, 204, 490, 20);
            Controls.Add(warn);

            chkForce = new CheckBox();
            chkForce.Text = "允许对未退出的程序强制结束（强制结束可能造成未保存内容丢失，只建议处理明确无关紧要的后台程序）";
            chkForce.SetBounds(14, 230, 490, 40);
            chkForce.Visible = needForceConfirm;
            Controls.Add(chkForce);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(230, 284, 84, 28);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            Button btnBack = new Button();
            btnBack.Text = "返回检查";
            btnBack.SetBounds(322, 284, 84, 28);
            btnBack.DialogResult = DialogResult.Cancel;
            Controls.Add(btnBack);

            btnOk = new Button();
            btnOk.Text = "确认结束";
            btnOk.SetBounds(420, 284, 90, 28);
            btnOk.DialogResult = DialogResult.OK;
            // 安全默认：需要强制确认时，先不允许点"确认结束"
            btnOk.Enabled = !needForceConfirm;
            Controls.Add(btnOk);

            chkForce.CheckedChanged += delegate { btnOk.Enabled = chkForce.Checked; };
            AcceptButton = btnCancel;   // 回车默认落在"取消"上：手一抖按回车不会执行
        }
    }

    // ========================================================================
    // 应用详情：原始进程名 / PID / 会话 / 内存 / 可见窗口 / 完整路径
    // （技术信息只在这里和高级模式出现，简洁模式保持"人话"）
    // ========================================================================
    internal class DetailsDialog : Form
    {
        public static void Show(IWin32Window owner, AppEntry app)
        {
            using (DetailsDialog dlg = new DetailsDialog(app))
            {
                dlg.ShowDialog(owner);
            }
        }

        DetailsDialog(AppEntry app)
        {
            Text = "应用详情 — " + app.DisplayName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 420);
            ShowInTaskbar = false;

            PictureBox pic = new PictureBox();
            pic.SetBounds(14, 12, 32, 32);
            pic.SizeMode = PictureBoxSizeMode.CenterImage;
            if (app.AppIcon != null) pic.Image = app.AppIcon.ToBitmap();
            Controls.Add(pic);

            Label head = new Label();
            head.Text = app.DisplayName + "\r\n" + app.Publisher + "　|　" + app.Desc +
                "\r\n状态：" + app.StatusText + "　|　" + app.RiskHint +
                "　|　共 " + app.Procs.Count + " 个进程，占用 " + (app.TotalMem / 1048576.0).ToString("F1") + " MB" +
                "\r\n识别来源：" + app.IdentifySource;
            head.SetBounds(56, 10, 560, 72);
            Controls.Add(head);

            ListView lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.SetBounds(14, 92, 610, 270);
            lv.Columns.Add("原始进程名", 110);
            lv.Columns.Add("PID", 55);
            lv.Columns.Add("会话ID", 50);
            lv.Columns.Add("内存(MB)", 65);
            lv.Columns.Add("可见窗口", 55);
            lv.Columns.Add("当前状态", 90);
            lv.Columns.Add("完整路径", 175);
            foreach (ProcSnapshot p in app.Procs)
            {
                ListViewItem it = new ListViewItem(p.Name);
                it.SubItems.Add(p.Pid.ToString());
                it.SubItems.Add(p.SessionId.ToString());
                it.SubItems.Add((p.Memory / 1048576.0).ToString("F1"));
                it.SubItems.Add((p.HasWindow || p.IsForeground) ? "有" : "无");
                it.SubItems.Add(MainFormStatusText(p));
                it.SubItems.Add(p.ExePath ?? "（无法读取）");
                lv.Items.Add(it);
            }
            Controls.Add(lv);

            Button btnClose = new Button();
            btnClose.Text = "关闭";
            btnClose.SetBounds(538, 374, 86, 28);
            btnClose.DialogResult = DialogResult.Cancel;
            Controls.Add(btnClose);
            CancelButton = btnClose;
        }

        static string MainFormStatusText(ProcSnapshot p)
        {
            switch (p.Status)
            {
                case AppStatus.InUse: return "正在使用";
                case AppStatus.UserProtected: return "用户保护";
                case AppStatus.Background: return "后台运行";
                case AppStatus.Unknown: return "未知应用";
                case AppStatus.Blacklisted: return "系统关键进程";
                default: return "受保护" + (string.IsNullOrEmpty(p.Reason) ? "" : "（" + p.Reason + "）");
            }
        }
    }

    // ========================================================================
    // 设置对话框：唯一能打开"允许强制结束"开关的地方（默认关闭）
    // ========================================================================
    internal class SettingsDialog : Form
    {
        public static new void Show(IWin32Window owner)
        {
            using (SettingsDialog dlg = new SettingsDialog())
            {
                dlg.ShowDialog(owner);
            }
        }

        SettingsDialog()
        {
            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(480, 190);
            ShowInTaskbar = false;

            CheckBox chk = new CheckBox();
            chk.Text = "允许强制结束未正常退出的程序";
            chk.Checked = SettingsStore.AllowForceKill;
            chk.SetBounds(16, 16, 440, 24);
            Controls.Add(chk);

            Label warn = new Label();
            // 需求指定文案
            warn.Text = "强制结束可能造成未保存内容丢失，只建议处理明确无关紧要的后台程序。\r\n\r\n" +
                "说明：关闭该开关时，正常关闭失败的应用只会报告\"未退出，未强制结束\"，\r\n绝不会直接杀掉进程。修改后立即生效。";
            warn.ForeColor = Color.FromArgb(140, 76, 0);
            warn.SetBounds(34, 48, 430, 90);
            Controls.Add(warn);

            Button btnOk = new Button();
            btnOk.Text = "确定";
            btnOk.SetBounds(290, 146, 80, 28);
            btnOk.Click += delegate
            {
                SettingsStore.SetAllowForceKill(chk.Checked);
                DialogResult = DialogResult.OK;
            };
            Controls.Add(btnOk);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(378, 146, 80, 28);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
        }
    }

    // ========================================================================
    // 列表排序器
    // ========================================================================
    internal class RowComparer : System.Collections.IComparer
    {
        public const int ByName = 0, ByPub = 1, ByMem = 2, ByPid = 3;
        public int SortKey = ByMem;
        public bool Desc = true;

        public int Compare(object a, object b)
        {
            RowModel ra = ((ListViewItem)a).Tag as RowModel;
            RowModel rb = ((ListViewItem)b).Tag as RowModel;
            if (ra == null || rb == null) return 0;
            int r = 0;
            switch (SortKey)
            {
                case ByPub: r = string.Compare(ra.App.Publisher, rb.App.Publisher, StringComparison.OrdinalIgnoreCase); break;
                case ByMem: long ma = ra.InAdvanced ? ra.Proc.Memory : ra.App.TotalMem;
                            long mb = rb.InAdvanced ? rb.Proc.Memory : rb.App.TotalMem;
                            r = ma.CompareTo(mb); break;
                case ByPid: int pa = ra.Proc != null ? ra.Proc.Pid : 0;
                            int pb = rb.Proc != null ? rb.Proc.Pid : 0;
                            r = pa.CompareTo(pb); break;
                default: r = string.Compare(ra.App.DisplayName, rb.App.DisplayName, StringComparison.OrdinalIgnoreCase); break;
            }
            return Desc ? -r : r;
        }
    }

    // ========================================================================
    // 只读自检：验证"识别 / 分组 / 保护"逻辑，写入 selftest_result.txt。
    // 绝不结束任何进程。
    // ========================================================================
    internal static class SelfTest
    {
        public static void Run()
        {
            StringBuilder log = new StringBuilder();
            log.AppendLine("EyesOff 应用分类自检（只读模式，未结束任何进程）");
            log.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            ProcessScanner.ScanResult scan = ProcessScanner.ScanAll();
            AppModel.Model model = AppModel.Build(scan);
            log.AppendLine("本进程 PID：" + ProcessScanner.MyPid + "，会话 ID：" + ProcessScanner.CurrentSessionId);
            log.AppendLine();

            int nInUse = 0, nBg = 0, nUp = 0, nUnk = 0, nProt = 0;
            foreach (AppEntry a in model.Apps)
            {
                if (a.Status == AppStatus.InUse) nInUse++;
                else if (a.Status == AppStatus.Background) nBg++;
                else if (a.Status == AppStatus.UserProtected) nUp++;
                else if (a.Status == AppStatus.Unknown) nUnk++;
                else nProt++;
            }

            log.AppendLine("系统进程总数：" + scan.TotalCount);
            log.AppendLine("合并后应用数：" + model.Apps.Count +
                "（正在使用 " + nInUse + "，可清理 " + nBg + "，用户保护 " + nUp + "，未知 " + nUnk + "，受保护 " + nProt + "）");
            log.AppendLine();

            log.AppendLine("== 逐项断言（每一条都必须 PASS）==");
            bool allPass = true;
            foreach (AppEntry app in model.Apps)
            {
                foreach (ProcSnapshot p in app.Procs)
                {
                    // 断言 1：可清理候选必须满足全部硬性安全规则
                    if (app.Status == AppStatus.Background)
                    {
                        if (p.Pid <= 100) { allPass = false; log.AppendLine("FAIL：可清理列表出现 PID≤100：" + p.Name); }
                        if (ProcessScanner.IsBlacklisted(p.Name)) { allPass = false; log.AppendLine("FAIL：可清理列表出现黑名单进程：" + p.Name); }
                        if (ProcessScanner.IsUnderWindowsDir(p.ExePath ?? "")) { allPass = false; log.AppendLine("FAIL：可清理列表出现系统目录进程：" + p.ExePath); }
                        if (p.HasWindow) { allPass = false; log.AppendLine("FAIL：可清理列表出现有窗口的应用：" + app.DisplayName); }
                        if (p.Id == null || p.Id.Level < 1) { allPass = false; log.AppendLine("FAIL：可清理列表出现未识别应用：" + p.Name); }
                        if (p.SessionId != ProcessScanner.CurrentSessionId) { allPass = false; log.AppendLine("FAIL：可清理列表出现其他会话进程：" + p.Name); }
                    }
                    // 断言 2：黑名单进程绝不允许出现在任何应用视图
                    if (ProcessScanner.IsBlacklisted(p.Name))
                    { allPass = false; log.AppendLine("FAIL：黑名单进程出现在应用模型：" + p.Name); }
                    // 断言 3：未知应用不允许被标记为可清理
                    if (app.Status == AppStatus.Background && app.Level < 1)
                    { allPass = false; log.AppendLine("FAIL：未识别应用进入可清理列表：" + app.DisplayName); }
                }
            }
            // 断言 4：点名系统进程不得出现在简洁模式（应用模型的非受保护状态里）
            string[] mustAbsent = new string[] { "explorer", "svchost", "system", "csrss", "lsass", "dwm", "winlogon", "services", "ctfmon" };
            foreach (string must in mustAbsent)
            {
                bool found = false;
                foreach (AppEntry app in model.Apps)
                    foreach (ProcSnapshot p in app.Procs)
                        if (string.Equals(ProcessScanner.Normalize(p.Name), ProcessScanner.Normalize(must), StringComparison.OrdinalIgnoreCase)
                            && app.Status != AppStatus.Protected)
                        { found = true; break; }
                if (found) { allPass = false; log.AppendLine("FAIL：系统进程 \"" + must + "\" 以非受保护状态出现在简洁模式"); }
                else log.AppendLine("PASS：系统进程 \"" + must + "\" 不会出现在简洁模式");
            }
            log.AppendLine(allPass ? "结果：全部断言 PASS" : "结果：存在 FAIL，请检查！");
            log.AppendLine();

            log.AppendLine("== 正在使用（已保护）应用列表 ==");
            foreach (AppEntry a in model.Apps)
                if (a.Status == AppStatus.InUse)
                    log.AppendLine("  " + a.DisplayName + "　" + a.Publisher + "　" + a.Procs.Count + " 个进程　" + (a.TotalMem / 1048576.0).ToString("F1") + " MB");
            log.AppendLine();
            log.AppendLine("== 可清理应用列表（推荐清理范围）==");
            foreach (AppEntry a in model.Apps)
                if (a.Status == AppStatus.Background)
                    log.AppendLine("  " + a.DisplayName + "　" + a.Publisher + "　" + a.Procs.Count + " 个进程　" + (a.TotalMem / 1048576.0).ToString("F1") + " MB　" + a.Procs[0].ExePath);
            log.AppendLine();
            log.AppendLine("== 未知应用（仅高级模式可见，禁止批量结束）==");
            foreach (AppEntry a in model.Apps)
                if (a.Status == AppStatus.Unknown)
                    log.AppendLine("  " + a.DisplayName + "　" + a.Procs[0].ExePath);
            log.AppendLine();
            log.AppendLine("== 受保护应用（仅高级模式可见，禁止结束）==");
            foreach (AppEntry a in model.Apps)
                if (a.Status == AppStatus.Protected)
                    log.AppendLine("  " + a.DisplayName + "　原因：" + a.RiskHint);

            string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_result.txt");
            try { File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(true)); }
            catch { outPath = Path.Combine(Path.GetTempPath(), "EyesOff_selftest_result.txt"); File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(true)); }
        }
    }
}
