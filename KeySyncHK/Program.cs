#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HollowKnightKeySync
{
    public static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private readonly TextBox _filterBox = new TextBox();
        private readonly Button _refreshButton = new Button();
        private readonly Button _injectButton = new Button();
        private readonly CheckBox _enabledCheckBox = new CheckBox();
        private readonly ComboBox _presetBox = new ComboBox();
        private readonly Button _applyLayoutButton = new Button();
        private readonly CheckBox _forceInjectBox = new CheckBox();

        private readonly Label[] _slotLabels = new Label[3];
        private readonly TextBox[] _slotBoxes = new TextBox[3];
        private readonly Button[] _recordButtons = new Button[3];
        private readonly Button[] _clearButtons = new Button[3];

        private readonly ListBox _windowList = new ListBox();
        private readonly Label _statusLabel = new Label();

        private List<IntPtr> _targets = new List<IntPtr>();
        private readonly List<uint>[] _slotKeys = { new List<uint>(), new List<uint>(), new List<uint>() };
        private readonly Dictionary<IntPtr, HashSet<uint>> _held = new Dictionary<IntPtr, HashSet<uint>>();
        private readonly HashSet<IntPtr> _fakeFocused = new HashSet<IntPtr>();
        private readonly KeyboardHook _hook = new KeyboardHook();

        private int _recordSlot = -1;
        private volatile bool _enabled = true;

        private static void FakeFocus(IntPtr hwnd)
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_ACTIVATEAPP, (IntPtr)1, IntPtr.Zero);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_ACTIVATE, (IntPtr)NativeMethods.WA_ACTIVE, IntPtr.Zero);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
        }

        public MainForm()
        {
            Text = "空洞骑士多窗口键盘同步 - 分键路由版";
            Width = 780;
            Height = 500;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;

            // 第一行
            _filterBox.SetBounds(12, 12, 320, 24);
            _filterBox.Text = "Hollow Knight";

            _refreshButton.SetBounds(340, 12, 100, 24);
            _refreshButton.Text = "刷新窗口";
            _refreshButton.Click += (s, e) => RefreshWindows();

            _injectButton.SetBounds(448, 12, 110, 24);
            _injectButton.Text = "注入后台Hook";
            _injectButton.Click += (s, e) => InjectAll();

            _enabledCheckBox.SetBounds(566, 12, 190, 24);
            _enabledCheckBox.Text = "启用同步";
            _enabledCheckBox.Checked = true;
            _enabledCheckBox.CheckedChanged += (s, e) => _enabled = _enabledCheckBox.Checked;

            // 第二行
            _presetBox.SetBounds(12, 44, 140, 24);
            _presetBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _presetBox.Items.Add("空预设");
            _presetBox.Items.Add("上下双人");
            _presetBox.Items.Add("左右双人");
            _presetBox.SelectedIndex = 0;
            _presetBox.SelectedIndexChanged += (s, e) => ApplyLayout();

            _applyLayoutButton.SetBounds(160, 44, 110, 24);
            _applyLayoutButton.Text = "应用布局";
            _applyLayoutButton.Click += (s, e) => ApplyLayout();

            _forceInjectBox.SetBounds(280, 44, 180, 24);
            _forceInjectBox.Text = "强制重注(忽略跳过)";
            _forceInjectBox.Checked = false;

            // 第三~五行：窗口2/3/4 的专属键位
            for (int i = 0; i < 3; i++)
            {
                int y = 76 + i * 30;
                int idx = i;

                _slotLabels[i] = new Label();
                _slotLabels[i].SetBounds(12, y + 3, 150, 24);
                _slotLabels[i].Text = "窗口" + (i + 2) + ": (无窗口)";

                _slotBoxes[i] = new TextBox();
                _slotBoxes[i].SetBounds(170, y, 330, 24);
                _slotBoxes[i].ReadOnly = true;

                _recordButtons[i] = new Button();
                _recordButtons[i].SetBounds(510, y, 70, 24);
                _recordButtons[i].Text = "录键";
                _recordButtons[i].Click += (s, e) => ToggleRecord(idx);

                _clearButtons[i] = new Button();
                _clearButtons[i].SetBounds(590, y, 70, 24);
                _clearButtons[i].Text = "清空";
                _clearButtons[i].Click += (s, e) => ClearSlot(idx);

                Controls.Add(_slotLabels[i]);
                Controls.Add(_slotBoxes[i]);
                Controls.Add(_recordButtons[i]);
                Controls.Add(_clearButtons[i]);
            }

            // 列表 + 状态栏
            _windowList.SetBounds(12, 168, 744, 240);
            _statusLabel.SetBounds(12, 416, 744, 24);
            _statusLabel.Text = "刷新窗口 → 给窗口2/3/4录键 → 焦点放主窗口或桌面 → 测试。Esc 所有人共用。";

            Controls.Add(_filterBox);
            Controls.Add(_refreshButton);
            Controls.Add(_injectButton);
            Controls.Add(_enabledCheckBox);
            Controls.Add(_presetBox);
            Controls.Add(_applyLayoutButton);
            Controls.Add(_forceInjectBox);
            Controls.Add(_windowList);
            Controls.Add(_statusLabel);

            _hook.Processor = HandleKey;
            _hook.Install();

            FormClosed += (s, e) =>
            {
                // 退出前补发松开，防止后台窗口卡键
                foreach (var kv in _held)
                    foreach (uint vk in kv.Value)
                        SendKeyUp(kv.Key, vk);
                _held.Clear();
                _hook.Uninstall();
            };

            Load += (s, e) => RefreshWindows();
        }

        // ========== 录键 ==========
        private void ToggleRecord(int slot)
        {
            if (_recordSlot == slot)
            {
                _recordSlot = -1;
                _recordButtons[slot].Text = "录键";
                _statusLabel.Text = "录键结束。";
            }
            else
            {
                if (_recordSlot >= 0)
                    _recordButtons[_recordSlot].Text = "录键";
                _recordSlot = slot;
                _recordButtons[slot].Text = "停止";
                _statusLabel.Text = "正在给窗口" + (slot + 2) + "录键：直接按键盘添加，点“停止”结束。录键期间所有按键被吞掉。";
            }
        }

        private void ClearSlot(int slot)
        {
            _slotKeys[slot].Clear();
            UpdateSlotText(slot);
        }

        private void RecordKey(uint vk)
        {
            int s = _recordSlot;
            if (s < 0)
                return;

            if (vk == 0x1B)
            {
                _statusLabel.Text = "Esc 是公共键，不能分配。";
                return;
            }

            for (int i = 0; i < 3; i++)
            {
                if (i != s && _slotKeys[i].Contains(vk))
                {
                    _statusLabel.Text = "该键已被窗口" + (i + 2) + "占用，不能重复。";
                    return;
                }
            }

            if (_slotKeys[s].Contains(vk))
                return;

            _slotKeys[s].Add(vk);
            UpdateSlotText(s);
        }

        private void UpdateSlotText(int slot)
        {
            _slotBoxes[slot].Text = string.Join(", ", _slotKeys[slot].ConvertAll(VkName));
        }

        private static string VkName(uint vk)
        {
            return ((Keys)vk).ToString();
        }

        // ========== 核心路由：返回 true = 吞掉物理按键 ==========
        private bool HandleKey(KeyEventInfo info)
        {
            if (!_enabled)
                return false;

            // 录键模式：吞掉所有键，只记录
            if (_recordSlot >= 0)
            {
                if (info.Down)
                    BeginInvoke((Action)(() => RecordKey(info.VkCode)));
                return true;
            }

            IntPtr fg = NativeMethods.GetForegroundWindow();

            // 焦点在工具上：不干预
            if (IsSelfWindow(fg))
                return false;

            // Esc 公共：发给所有后台窗口，物理键照常给焦点窗口
            if (info.VkCode == 0x1B)
            {
                foreach (IntPtr hwnd in _targets)
                    if (hwnd != fg)
                        SendKey(hwnd, info);
                return false;
            }

            // 查归属槽
            int slot = -1;
            for (int i = 0; i < 3; i++)
            {
                if (_slotKeys[i].Contains(info.VkCode))
                {
                    slot = i;
                    break;
                }
            }

            // 未分配：物理键给焦点窗口（主窗口），后台不收
            if (slot < 0)
                return false;

            IntPtr owner = (_targets.Count > slot + 1) ? _targets[slot + 1] : IntPtr.Zero;
            if (owner == IntPtr.Zero)
                return false;

            // 归属窗口自己就是焦点：走物理输入，不吞不发
            if (owner == fg)
                return false;

            // 归属窗口在后台：吞掉物理键，只发给它
            if (info.Down)
            {
                HashSet<uint> held;
                if (!_held.TryGetValue(owner, out held))
                {
                    held = new HashSet<uint>();
                    _held[owner] = held;
                }

                if (held.Contains(info.VkCode))
                    return true; // 自动重复，忽略

                FakeFocus(owner);
                held.Add(info.VkCode);

                // 重发所有按住中的键，防止假焦点重置丢键
                foreach (uint vk in held)
                    SendKeyDown(owner, vk);
            }
            else
            {
                HashSet<uint> held;
                if (_held.TryGetValue(owner, out held))
                    held.Remove(info.VkCode);

                SendKeyUp(owner, info.VkCode);
            }

            return true;
        }

        private static void SendKeyDown(IntPtr hwnd, uint vk)
        {
            uint scan = NativeMethods.MapVirtualKey(vk, 0);
            IntPtr lp = MakeLParam(scan, false, IsExtended(vk));
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)vk, lp);
        }

        private static void SendKeyUp(IntPtr hwnd, uint vk)
        {
            uint scan = NativeMethods.MapVirtualKey(vk, 0);
            IntPtr lp = MakeLParam(scan, true, IsExtended(vk));
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, lp);
        }

        private static bool IsExtended(uint vk)
        {
            return vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28 ||
                   vk == 0x2D || vk == 0x2E || vk == 0x24 || vk == 0x23 ||
                   vk == 0x21 || vk == 0x22;
        }

        private static void SendKey(IntPtr hwnd, KeyEventInfo info)
        {
            bool sys = info.Sys || info.VkCode == 0x12;

            int msg;
            if (info.Down)
                msg = sys ? NativeMethods.WM_SYSKEYDOWN : NativeMethods.WM_KEYDOWN;
            else
                msg = sys ? NativeMethods.WM_SYSKEYUP : NativeMethods.WM_KEYUP;

            IntPtr lParam = MakeLParam(info.ScanCode, !info.Down, info.Extended);
            NativeMethods.PostMessage(hwnd, msg, (IntPtr)info.VkCode, lParam);
        }

        private static IntPtr MakeLParam(uint scanCode, bool keyUp, bool extended)
        {
            uint lParam = 1;
            lParam |= (scanCode & 0xFF) << 16;
            if (extended)
                lParam |= 1u << 24;
            if (keyUp)
            {
                lParam |= 1u << 30;
                lParam |= 1u << 31;
            }
            return (IntPtr)lParam;
        }

        // ========== 预设布局 ==========
        private void ApplyLayout()
        {
            string preset = _presetBox.SelectedItem as string;

            if (preset == null || preset == "空预设")
                return;

            if (_targets.Count == 0)
            {
                _statusLabel.Text = "没有目标窗口可布局，请先刷新窗口。";
                return;
            }

            Rectangle scr = Screen.PrimaryScreen.Bounds;
            int n = _targets.Count;

            for (int i = 0; i < n; i++)
            {
                int x, y, w, h;

                if (preset == "上下双人")
                {
                    w = scr.Width;
                    h = scr.Height / n;
                    x = scr.X;
                    y = scr.Y + i * h;
                }
                else
                {
                    w = scr.Width / n;
                    h = scr.Height;
                    x = scr.X + i * w;
                    y = scr.Y;
                }

                MakeBorderlessAndMove(_targets[i], x, y, w, h);
            }

            _statusLabel.Text = "已应用预设：" + preset + "（" + n + " 个窗口）。";
        }

        private static void MakeBorderlessAndMove(IntPtr hwnd, int x, int y, int w, int h)
        {
            long s = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            s &= ~0x00CF0000L;
            s |= 0x80000000L;
            s |= 0x10000000L;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(s));
            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero, x, y, w, h,
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOOWNERZORDER);
        }

        // ========== 注入（验货版） ==========
        private void InjectAll()
        {
            string dllPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HookDLL.dll");

            if (!System.IO.File.Exists(dllPath))
            {
                _statusLabel.Text = "找不到 HookDLL.dll！请把它放到 exe 同目录。";
                return;
            }

            RefreshWindows();

            if (_targets.Count == 0)
            {
                _statusLabel.Text = "没找到游戏窗口，请先打开游戏。";
                return;
            }

            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");
            bool force = _forceInjectBox.Checked;

            var pids = new List<int>();
            foreach (IntPtr hwnd in _targets)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                int id = (int)pid;
                if (!pids.Contains(id))
                    pids.Add(id);
            }

            var log = new List<string>();
            int injected = 0;
            int skipped = 0;

            foreach (int pid in pids)
            {
                if (!force && _injectedPids.Contains(pid))
                {
                    skipped++;
                    continue;
                }

                IntPtr hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_ALL_ACCESS, false, pid);
                if (hProc == IntPtr.Zero)
                {
                    log.Add("pid" + pid + " OpenProcess失败 错误码" + Marshal.GetLastWin32Error());
                    continue;
                }

                IntPtr mem = NativeMethods.VirtualAllocEx(
                    hProc, IntPtr.Zero, (uint)pathBytes.Length,
                    NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                    NativeMethods.PAGE_READWRITE);
                if (mem == IntPtr.Zero)
                {
                    log.Add("pid" + pid + " VirtualAllocEx失败 错误码" + Marshal.GetLastWin32Error());
                    NativeMethods.CloseHandle(hProc);
                    continue;
                }

                UIntPtr written;
                if (!NativeMethods.WriteProcessMemory(hProc, mem, pathBytes, (uint)pathBytes.Length, out written))
                {
                    log.Add("pid" + pid + " WriteProcessMemory失败 错误码" + Marshal.GetLastWin32Error());
                    NativeMethods.CloseHandle(hProc);
                    continue;
                }

                IntPtr kernel32 = NativeMethods.GetModuleHandle("kernel32.dll");
                IntPtr loadLib = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");

                IntPtr hThread = NativeMethods.CreateRemoteThread(hProc, IntPtr.Zero, 0, loadLib, mem, 0, IntPtr.Zero);
                if (hThread == IntPtr.Zero)
                {
                    log.Add("pid" + pid + " CreateRemoteThread失败 错误码" + Marshal.GetLastWin32Error());
                    NativeMethods.CloseHandle(hProc);
                    continue;
                }

                NativeMethods.WaitForSingleObject(hThread, 5000);
                IntPtr exitCode;
                NativeMethods.GetExitCodeThread(hThread, out exitCode);
                NativeMethods.CloseHandle(hThread);

                if (exitCode == IntPtr.Zero)
                {
                    log.Add("pid" + pid + " DLL在游戏里加载失败(退出码0)：检查 HookDLL 是否 x64、是否损坏或被占用");
                    NativeMethods.CloseHandle(hProc);
                    continue;
                }

                NativeMethods.CloseHandle(hProc);
                _injectedPids.Add(pid);
                injected++;
            }

            _statusLabel.Text = "新注入 " + injected + " 个，跳过 " + skipped + " 个。";

            if (log.Count > 0)
                MessageBox.Show(string.Join("\n", log), "注入失败详情");
        }

        private readonly HashSet<int> _injectedPids = new HashSet<int>();

        // ========== 窗口查找（最多4个） ==========
        private void RefreshWindows()
        {
            var newTargets = new List<IntPtr>();
            string filter = _filterBox.Text.Trim();

            NativeMethods.EnumWindows((hwnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                    return true;

                string title = NativeMethods.GetWindowText(hwnd);
                string className = NativeMethods.GetClassName(hwnd);
                string processName = GetProcessName(hwnd);

                bool match = false;

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    if (title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        match = true;
                    if (className.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        match = true;
                    if (processName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        match = true;
                }
                else
                {
                    if (className == "UnityWndClass" && !string.IsNullOrWhiteSpace(title))
                        match = true;
                }

                if (match && IsSelfWindow(hwnd))
                    match = false;

                if (match && !newTargets.Contains(hwnd))
                    newTargets.Add(hwnd);

                return true;
            }, IntPtr.Zero);

            // 最多四个窗口
            if (newTargets.Count > 4)
                newTargets = newTargets.GetRange(0, 4);

            _targets = newTargets;

            // 清掉已消失窗口的按住记录
            var alive = new HashSet<IntPtr>(newTargets);
            foreach (IntPtr k in new List<IntPtr>(_held.Keys))
                if (!alive.Contains(k))
                    _held.Remove(k);

            foreach (IntPtr hwnd in _targets)
            {
                if (!_fakeFocused.Contains(hwnd))
                {
                    _fakeFocused.Add(hwnd);
                    FakeFocus(hwnd);
                }
            }

            _windowList.Items.Clear();
            foreach (IntPtr hwnd in _targets)
            {
                string title = NativeMethods.GetWindowText(hwnd);
                string proc = GetProcessName(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                    title = "(无标题)";
                _windowList.Items.Add("[" + proc + "] " + title + "  hwnd=" + hwnd);
            }

            // 更新槽位标签
            for (int i = 0; i < 3; i++)
            {
                IntPtr hwnd = (_targets.Count > i + 1) ? _targets[i + 1] : IntPtr.Zero;
                string name = "(无窗口)";
                if (hwnd != IntPtr.Zero)
                {
                    name = NativeMethods.GetWindowText(hwnd);
                    if (string.IsNullOrWhiteSpace(name))
                        name = "(无标题)";
                    if (name.Length > 18)
                        name = name.Substring(0, 18) + "..";
                }
                _slotLabels[i].Text = "窗口" + (i + 2) + ": " + name;
            }

            _statusLabel.Text = "找到 " + _targets.Count + " 个目标窗口（最多4个）。";

            string preset = _presetBox.SelectedItem as string;
            if (preset != null && preset != "空预设" && _targets.Count > 0)
                ApplyLayout();
        }

        private static string GetProcessName(IntPtr hwnd)
        {
            Process p = null;
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch
            {
                return "";
            }
            finally
            {
                if (p != null)
                    p.Dispose();
            }
        }

        private static bool IsSelfWindow(IntPtr hwnd)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                return pid == GetCurrentProcessId();
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
    }

    public struct KeyEventInfo
    {
        public uint VkCode;
        public uint ScanCode;
        public bool Down;
        public bool Sys;
        public bool Extended;
    }

    public class KeyboardHook
    {
        private const int WH_KEYBOARD_LL = 13;

        // 返回 true = 吞掉这个物理按键
        public Func<KeyEventInfo, bool> Processor;

        private IntPtr _hookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _proc;

        public void Install()
        {
            _proc = HookCallback;
            _hookId = NativeMethods.SetWindowsHookEx(
                WH_KEYBOARD_LL,
                _proc,
                NativeMethods.GetModuleHandle(null),
                0
            );
        }

        public void Uninstall()
        {
            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;

                bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

                if (down || up)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                    var info = new KeyEventInfo
                    {
                        VkCode = data.vkCode,
                        ScanCode = data.scanCode,
                        Down = down,
                        Sys = msg == NativeMethods.WM_SYSKEYDOWN || msg == NativeMethods.WM_SYSKEYUP,
                        Extended = (data.flags & 0x01) != 0
                    };

                    if (Processor != null && Processor(info))
                        return (IntPtr)1; // 吞掉
                }
            }

            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
    }

    public static class NativeMethods
    {
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int WM_ACTIVATE = 0x0006;
        public const int WM_SETFOCUS = 0x0007;
        public const int WM_ACTIVATEAPP = 0x001C;
        public const int WM_NCACTIVATE = 0x0086;

        public const int WA_ACTIVE = 1;

        public const int GWL_STYLE = -16;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint PAGE_READWRITE = 0x04;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        public static extern bool GetExitCodeThread(IntPtr hThread, out IntPtr lpExitCode);

        public static string GetWindowText(IntPtr hWnd)
        {
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClassName(IntPtr hWnd)
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}