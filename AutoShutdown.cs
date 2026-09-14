// 自动关机助手 —— 下载完了 / 程序跑完了（含 DSH、MAA），自动关机
// 单文件原生小程序，只依赖 Windows 自带的 .NET Framework 4.x
// 重新编译：csc /target:winexe /optimize+ /codepage:65001 /out:自动关机助手.exe AutoShutdown.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AutoShutdown
{
    enum PowerAction { None, Shutdown, Restart, Hibernate, Logoff }

    interface IReporter
    {
        void Info(string message);
        void Progress(string text);
    }

    class NullReporter : IReporter
    {
        public void Info(string message) { }

        public void Progress(string text) { }
    }

    class FileReporter : IReporter
    {
        public void Progress(string text) { }
        readonly StreamWriter writer;
        public FileReporter(string path)
        {
            try
            {
                writer = new StreamWriter(path, true, new UTF8Encoding(false));
                writer.AutoFlush = true;
                writer.WriteLine();
                writer.WriteLine("===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====");
            }
            catch (Exception) { writer = null; }
        }
        public void Info(string message) { Write("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message); }
        void Write(string line)
        {
            if (writer == null) return;
            try { writer.WriteLine(line); } catch (Exception) { }
        }
        public void Close()
        {
            if (writer == null) return;
            try { writer.Flush(); writer.Close(); } catch (Exception) { }
        }
    }

    class Settings
    {
        public string Mode = "download";
        public List<string> DownloadDirs = new List<string>();
        public double Idle = 30;
        public double MinMb = 1;
        public double StartTimeout = 1800;
        public double DownloadPoll = 3;
        public bool Recurse = true;
        public int Delay = 60;
        public PowerAction Action = PowerAction.Shutdown;
        public bool Force;
        public bool DryRun;
        public List<string> TaskNames = new List<string>();
        public string TaskLogPath = "";
        public List<string> TaskLogKeywords = new List<string>();
        public string TaskNotifyApp = "";
        public string TaskNotifyText = "";
        public string TaskNotifyDir = "";
        public double TaskCpuIdle;
        public double TaskCpuPercent = 3;
        public double TaskCpuActivePercent = 10;
        public double TaskPoll = 2;
        public bool TimerUseClock;
        public double TimerMinutes = 30;
        public string TimerClock = "23:30";
        public int TimerSeconds;
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string application, string commandLine, IntPtr pa, IntPtr ta,
            bool inherit, uint flags, IntPtr env, string currentDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        const uint DETACHED_PROCESS = 0x00000008;
        const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

        public static int StartDetached(string exePath, string arguments, out bool brokeAway)
        {
            brokeAway = false;
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;
            string commandLine = "\"" + exePath + "\" " + arguments;
            uint flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB;
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                    Path.GetDirectoryName(exePath), ref si, out pi))
            {
                flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP;
                if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                        Path.GetDirectoryName(exePath), ref si, out pi))
                    return -1;
            }
            else brokeAway = true;
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return pi.dwProcessId;
        }
    }

    static class Core
    {
        public const string App = "自动关机助手";
        static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static double Now() { return Clock.Elapsed.TotalSeconds; }

        public static readonly string[] TempSuffixes = new string[]
        {
            ".crdownload", ".part", ".partial", ".download", ".downloading", ".tmp", ".td", ".xltd",
            ".!qb", ".aria2", ".filepart", ".opdownload", ".!ut", ".bc!", ".dltemp", ".pcap", ".mtd",
            ".fdown", ".dlt"
        };

        public static string ActionText(PowerAction action)
        {
            switch (action)
            {
                case PowerAction.Shutdown: return "关机";
                case PowerAction.Restart: return "重启";
                case PowerAction.Hibernate: return "休眠";
                case PowerAction.Logoff: return "注销";
            }
            return "不操作";
        }

        public static PowerAction ParseAction(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "restart": return PowerAction.Restart;
                case "hibernate": return PowerAction.Hibernate;
                case "logoff": return PowerAction.Logoff;
                case "none": return PowerAction.None;
            }
            return PowerAction.Shutdown;
        }

        public static string FormatHms(double seconds)
        {
            int total = (int)Math.Ceiling(Math.Max(0, seconds));
            return string.Format("{0:00}:{1:00}:{2:00}", total / 3600, (total % 3600) / 60, total % 60);
        }

        public static string FormatElapsed(double seconds)
        {
            if (seconds < 60) return seconds.ToString("0.0") + " 秒";
            int total = (int)Math.Round(Math.Max(0, seconds));
            return string.Format("{0:00}:{1:00}:{2:00}", total / 3600, (total % 3600) / 60, total % 60);
        }

        public static string FormatSize(double bytes)
        {
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            double size = Math.Max(0, bytes);
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return unit == 0 ? ((long)size).ToString() + " B" : size.ToString("0.0") + " " + units[unit];
        }

        public static string ActionArgs(PowerAction action, int seconds, bool force)
        {
            int delay = Math.Max(0, seconds);
            switch (action)
            {
                case PowerAction.Shutdown: return "/s /t " + delay + (force ? " /f" : "");
                case PowerAction.Restart: return "/r /t " + delay + (force ? " /f" : "");
                case PowerAction.Logoff: return "/l";
                case PowerAction.Hibernate: return "/h";
            }
            return null;
        }

        public static bool HasOsTimer(PowerAction action)
        {
            return action == PowerAction.Shutdown || action == PowerAction.Restart;
        }

        public static Dictionary<string, string> ProcessMap()
        {
            var map = new Dictionary<string, string>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception) { return map; }
            foreach (var process in all)
            {
                try
                {
                    string name = process.ProcessName;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
                    string key = name.ToLowerInvariant();
                    if (!map.ContainsKey(key)) map[key] = name;
                }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
            return map;
        }


        public static List<string> SplitList(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            foreach (string piece in text.Replace(";", ",").Split(','))
            {
                string item = piece.Trim();
                if (item.Length > 0) result.Add(item);
            }
            return result;
        }

        public static List<string> DefaultDownloadDirs()
        {
            var dirs = new List<string>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string name in new string[] { "Downloads", "下载" })
            {
                string path = Path.Combine(home, name);
                if (Directory.Exists(path)) { dirs.Add(path); return dirs; }
            }
            dirs.Add(home);
            return dirs;
        }

        public static string DefaultLogPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "自动关机助手.log");
        }

        public class Scan
        {
            public long Total;
            public int Files;
            public int Temps;
        }

        static IEnumerable<string> EnumerateFilesSafe(string root, bool recurse)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = null;
                string[] subs = null;
                try { files = Directory.GetFiles(dir); } catch (Exception) { }
                try { subs = Directory.GetDirectories(dir); } catch (Exception) { }
                if (files != null)
                    foreach (string file in files) yield return file;
                if (recurse && subs != null)
                    foreach (string sub in subs) stack.Push(sub);
            }
        }

        public static Scan ScanDownloads(IEnumerable<string> dirs, bool recurse)
        {
            var scan = new Scan();
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in EnumerateFilesSafe(dir, recurse))
                {
                    try
                    {
                        scan.Total += new FileInfo(file).Length;
                        scan.Files++;
                    }
                    catch (Exception) { continue; }
                    string name = Path.GetFileName(file).ToLowerInvariant();
                    bool temp = name.StartsWith(".");
                    if (!temp)
                        foreach (string suffix in TempSuffixes)
                            if (name.EndsWith(suffix)) { temp = true; break; }
                    if (temp) scan.Temps++;
                }
            }
            return scan;
        }

        public static bool TryParseClock(string text, out int hour, out int minute)
        {
            hour = 0;
            minute = 0;
            string raw = (text ?? "").Trim().Replace("：", ":");
            if (raw.Length == 0) return false;
            string hh, mm;
            int colon = raw.IndexOf(':');
            if (colon >= 0)
            {
                hh = raw.Substring(0, colon);
                mm = raw.Substring(colon + 1);
            }
            else if (raw.Length == 3 || raw.Length == 4)
            {
                hh = raw.Substring(0, raw.Length - 2);
                mm = raw.Substring(raw.Length - 2);
            }
            else return false;
            if (!int.TryParse(hh, out hour) || !int.TryParse(mm, out minute)) return false;
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
            {
                hour = 0;
                minute = 0;
                return false;
            }
            return true;
        }

        public static int SecondsUntilClock(int hour, int minute)
        {
            DateTime now = DateTime.Now;
            DateTime target = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
            if ((target - now).TotalSeconds < 5) target = target.AddDays(1);
            int seconds = (int)Math.Round((target - now).TotalSeconds);
            return Math.Max(1, seconds);
        }

        public static bool Wait(double seconds, CancellationToken token)
        {
            double end = Now() + Math.Max(0, seconds);
            while (true)
            {
                if (token.IsCancellationRequested) return false;
                double remain = end - Now();
                if (remain <= 0) return true;
                Thread.Sleep((int)Math.Min(200, Math.Max(20, remain * 1000)));
            }
        }

        public static bool RunShutdown(string args, IReporter log, bool dryRun, out string output)
        {
            output = "";
            if (dryRun)
            {
                log.Info("演练模式，将要执行：shutdown " + args);
                return true;
            }
            log.Info("执行：shutdown " + args);
            try
            {
                var info = new ProcessStartInfo("shutdown.exe", args);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = Process.Start(info))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    output = (stdout + " " + stderr).Trim();
                    if (process.ExitCode != 0)
                    {
                        if (output.Length == 0) output = "返回码 " + process.ExitCode;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        static bool Countdown(double seconds, string label, IReporter log, CancellationToken token)
        {
            if (seconds <= 0) return true;
            int last = -1;
            double end = Now() + seconds;
            while (true)
            {
                if (token.IsCancellationRequested) { log.Progress(""); return false; }
                double remain = end - Now();
                if (remain <= 0) { log.Progress(""); return true; }
                int shown = (int)Math.Ceiling(remain);
                if (shown != last)
                {
                    last = shown;
                    log.Progress(label + "将在 " + FormatHms(shown) + " 后执行（点“停止”可取消）");
                }
                Thread.Sleep(200);
            }
        }

        public static int Fire(Settings s, IReporter log, CancellationToken token)
        {
            string label = ActionText(s.Action);
            int delay = Math.Max(0, s.Delay);
            if (HasOsTimer(s.Action))
            {
                string output;
                if (!RunShutdown(ActionArgs(s.Action, delay, s.Force), log, s.DryRun, out output))
                {
                    log.Info("安排" + label + "失败：" + output);
                    return 1;
                }
                if (output.Length > 0) log.Info(output);
                if (delay <= 0) { log.Info("已发出" + label + "指令。"); return 0; }
                log.Info("已交给系统计时：" + label + "将在 " + FormatHms(delay) + " 后执行（可以取消）。");
                if (s.DryRun)
                {
                    log.Info("演练模式：跳过 " + FormatHms(delay) + " 的等待时间。");
                    return 0;
                }
                if (!Countdown(delay, label, log, token))
                {
                    string ignored;
                    RunShutdown("/a", log, false, out ignored);
                    log.Info("已取消" + label + "。");
                    return 130;
                }
                log.Info("倒计时结束，等待系统完成" + label + "。");
                return 0;
            }
            log.Info(label + "将在 " + FormatHms(delay) + " 后执行。");
            if (s.DryRun) log.Info("演练模式：跳过 " + FormatHms(delay) + " 的等待时间。");
            else if (!Countdown(delay, label, log, token))
            {
                log.Info("已取消" + label + "。");
                return 130;
            }
            string result;
            if (!RunShutdown(ActionArgs(s.Action, 0, s.Force), log, s.DryRun, out result))
            {
                log.Info(label + "失败：" + result);
                return 1;
            }
            if (result.Length > 0) log.Info(result);
            log.Info("已完成：" + label + "。");
            return 0;
        }

        static string WatchDownloads(Settings s, IReporter log, CancellationToken token)
        {
            var dirs = s.DownloadDirs.Where(Directory.Exists).ToList();
            if (dirs.Count == 0) return "nodir";
            log.Info("监控下载文件夹：" + string.Join("、", dirs.ToArray()));
            double minBytes = Math.Max(0, s.MinMb) * 1024.0 * 1024.0;
            double poll = Math.Max(0.2, s.DownloadPoll);
            Core.Scan baseScan = ScanDownloads(dirs, s.Recurse);
            long prevTotal = baseScan.Total;
            double prevTime = Now();
            double started = prevTime;
            double lastActive = started;
            bool downloading = false;
            bool warnedSmall = false;

            while (true)
            {
                if (token.IsCancellationRequested) { log.Progress(""); return "stopped"; }
                double now = Now();
                Scan scan = ScanDownloads(dirs, s.Recurse);
                long delta = scan.Total - baseScan.Total;
                bool grew = scan.Total > prevTotal;
                double speed = grew && now > prevTime ? (scan.Total - prevTotal) / (now - prevTime) : 0;
                if (grew || scan.Temps > 0) lastActive = now;
                prevTotal = scan.Total;
                prevTime = now;

                if (!downloading)
                {
                    if (grew || scan.Temps > 0)
                    {
                        downloading = true;
                        log.Info("检测到下载活动，开始监控（当前新增 " + FormatSize(Math.Max(0, delta)) + "）。");
                    }
                    else if (s.StartTimeout > 0 && now - started >= s.StartTimeout)
                    {
                        log.Progress("");
                        log.Info(FormatHms(s.StartTimeout) + " 内没有检测到任何下载活动，已退出。");
                        return "timeout";
                    }
                    else
                    {
                        string limit = s.StartTimeout > 0 ? "，等待上限 " + FormatHms(s.StartTimeout - (now - started)) : "";
                        log.Progress("等待下载开始…（已等待 " + FormatElapsed(now - started) + limit + "）");
                        if (!Wait(poll, token)) { log.Progress(""); return "stopped"; }
                        continue;
                    }
                }

                double idleFor = now - lastActive;
                if (delta >= minBytes && scan.Temps == 0 && idleFor >= s.Idle)
                {
                    log.Progress("");
                    log.Info("下载已停止变化 " + ((int)idleFor) + " 秒，判定完成：新增 " + FormatSize(Math.Max(0, delta)) +
                             "，共 " + scan.Files + " 个文件，用时 " + FormatElapsed(now - started) + "。");
                    return "finished";
                }
                if (idleFor >= s.Idle && scan.Temps == 0 && delta < minBytes && !warnedSmall)
                {
                    warnedSmall = true;
                    log.Info("新增内容只有 " + FormatSize(Math.Max(0, delta)) + "，小于下限 " +
                             FormatSize(minBytes) + "，继续等待…");
                }
                if (s.StartTimeout > 0 && now - started >= s.StartTimeout)
                {
                    log.Progress("");
                    log.Info("超过 " + FormatHms(s.StartTimeout) + " 仍在变化或未达下限，已退出。");
                    return "timeout";
                }
                var parts = new List<string>();
                if (scan.Temps > 0) parts.Add("临时文件 " + scan.Temps + " 个（下载中）");
                parts.Add("新增 " + FormatSize(Math.Max(0, delta)));
                parts.Add("共 " + scan.Files + " 个文件");
                if (speed > 1024) parts.Add("速度 " + FormatSize(speed) + "/s");
                parts.Add("静止 " + ((int)idleFor) + "/" + ((int)s.Idle) + " 秒");
                log.Progress(string.Join(" · ", parts.ToArray()));
                if (!Wait(poll, token)) { log.Progress(""); return "stopped"; }
            }
        }


        public static int RunSession(Settings s, IReporter log, CancellationToken token)
        {
            log.Info(App + " 启动（" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "）" +
                     (s.DryRun ? "［演练模式］" : ""));
            double started = Now();

            if (s.Mode == "timer")
            {
                string label = ActionText(s.Action);
                DateTime when = DateTime.Now.AddSeconds(Math.Max(0, s.TimerSeconds));
                log.Info("定时" + label + "：将在 " + when.ToString("yyyy-MM-dd HH:mm:ss") + " 执行（约 " +
                         FormatHms(s.TimerSeconds) + " 后）。");
                if (HasOsTimer(s.Action))
                {
                    string output;
                    if (!RunShutdown(ActionArgs(s.Action, s.TimerSeconds, s.Force), log, s.DryRun, out output))
                    {
                        log.Info("安排" + label + "失败：" + output);
                        return 1;
                    }
                    if (output.Length > 0) log.Info(output);
                    if (s.DryRun)
                    {
                        log.Info("演练模式：跳过 " + FormatHms(s.TimerSeconds) + " 的等待时间。");
                        return 0;
                    }
                    log.Info("已交给系统计时：关掉本程序也照样执行；点“停止”或运行 --cancel 可取消。");
                    if (!Countdown(s.TimerSeconds, label, log, token))
                    {
                        string ignored;
                        RunShutdown("/a", log, false, out ignored);
                        log.Info("已取消" + label + "。");
                        return 130;
                    }
                    log.Info("倒计时结束，等待系统完成" + label + "。");
                    return 0;
                }
                log.Info("该动作没有系统计时，程序会一直等到点（请不要关闭窗口）。");
                if (s.DryRun) log.Info("演练模式：跳过 " + FormatHms(s.TimerSeconds) + " 的等待时间。");
                else if (!Countdown(s.TimerSeconds, label, log, token))
                {
                    log.Info("已取消" + label + "。");
                    return 130;
                }
                int savedDelay = s.Delay;
                s.Delay = 0;
                int timerCode = Fire(s, log, token);
                s.Delay = savedDelay;
                return timerCode;
            }

            if (s.Mode == "task")
            {
                string taskOutcome = WatchTask(s, log, token, started);
                if (taskOutcome == "stopped") { log.Info("已停止。"); return 130; }
                log.Info("任务已结束，用时 " + FormatElapsed(Now() - started) + "。");
            }
            else
            {
                string outcome = WatchDownloads(s, log, token);
                if (outcome == "stopped") { log.Info("已停止。"); return 130; }
                if (outcome == "nodir") { log.Info("下载文件夹不存在。"); return 2; }
                if (outcome != "finished") return 2;
            }

            if (s.Action == PowerAction.None) { log.Info("按设置不执行电源操作，任务结束。"); return 0; }
            return Fire(s, log, token);
        }


        // ---------------------------------------------------------- 任务结束检测

        public class TailedFile
        {
            readonly string path;
            long position;

            public TailedFile(string path)
            {
                this.path = path;
                try { position = new FileInfo(path).Length; }
                catch (Exception) { position = 0; }
            }

            public string ReadNew()
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) return "";
                    if (info.Length < position) { position = 0; }
                    if (info.Length == position) return "";
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Seek(position, SeekOrigin.Begin);
                        int count = (int)Math.Min(info.Length - position, 4 * 1024 * 1024);
                        byte[] data = new byte[count];
                        int read = stream.Read(data, 0, count);
                        position += read;
                        return Encoding.UTF8.GetString(data, 0, read);
                    }
                }
                catch (Exception) { return ""; }
            }
        }

        public static string FindMaaLog()
        {
            var roots = new List<string>();
            foreach (string drive in new string[] { "C:\\", "D:\\", "E:\\" })
                if (Directory.Exists(drive)) roots.Add(drive);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(home);
            var stopwatch = Stopwatch.StartNew();
            int visited = 0;
            foreach (string root in roots)
            {
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    if (stopwatch.ElapsedMilliseconds > 4000 || visited > 4000) break;
                    string dir = stack.Pop();
                    visited++;
                    string[] files = null, subs = null;
                    try { files = Directory.GetFiles(dir, "MAA.exe"); } catch (Exception) { }
                    if (files != null && files.Length > 0)
                    {
                        string logPath = Path.Combine(dir, "debug", "asst.log");
                        if (File.Exists(logPath)) return logPath;
                    }
                    if (dir.Substring(root.Length).Count(c => c == '\\') >= 2) continue;
                    try { subs = Directory.GetDirectories(dir); } catch (Exception) { }
                    if (subs == null) continue;
                    foreach (string sub in subs)
                    {
                        string name = Path.GetFileName(sub);
                        if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("AppData", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                        stack.Push(sub);
                    }
                }
            }
            return null;
        }

        public static string NewestFile(string pattern)
        {
            try
            {
                string dir = Path.GetDirectoryName(pattern);
                string name = Path.GetFileName(pattern);
                if (dir.Length == 0) dir = ".";
                if (name.IndexOf('*') < 0 && name.IndexOf('?') < 0)
                    return File.Exists(pattern) ? pattern : null;
                if (!Directory.Exists(dir)) return null;
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string file in Directory.GetFiles(dir, name))
                {
                    DateTime time = File.GetLastWriteTime(file);
                    if (time > bestTime) { bestTime = time; best = file; }
                }
                return best;
            }
            catch (Exception) { return null; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumWindows(WndEnumProc callback, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        delegate bool WndEnumProc(IntPtr hwnd, IntPtr param);




        public static bool PatternMatches(List<string> patterns, string imageName)
        {
            string name = imageName.ToLowerInvariant();
            foreach (string raw in patterns)
            {
                string want = (raw ?? "").Trim().ToLowerInvariant();
                if (want.Length == 0) continue;
                string baseName = want.EndsWith(".exe") ? want.Substring(0, want.Length - 4) : want;
                string nameBase = name.EndsWith(".exe") ? name.Substring(0, name.Length - 4) : name;
                if (name == want || nameBase == baseName || (baseName.Length >= 2 && nameBase.IndexOf(baseName, StringComparison.Ordinal) >= 0))
                    return true;
            }
            return false;
        }

        public static double ProcessCpuMilliseconds(List<string> patterns)
        {
            double total = 0;
            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    try
                    {
                        if (!PatternMatches(patterns, process.ProcessName)) continue;
                        total += process.TotalProcessorTime.TotalMilliseconds;
                    }
                    catch (Exception) { }
                    finally { process.Dispose(); }
                }
            }
            catch (Exception) { }
            return total;
        }

        public class NotifyWatcher
        {
            readonly string dir;
            readonly List<string> appKeys = new List<string>();
            readonly List<string> textKeys = new List<string>();
            readonly Dictionary<string, int> counts = new Dictionary<string, int>();
            string lastStamp = "";

            public NotifyWatcher(string dir, List<string> appKeyList, List<string> textKeyList)
            {
                this.dir = dir;
                foreach (string key in appKeyList)
                    if (!string.IsNullOrEmpty(key) && !appKeys.Contains(key)) appKeys.Add(key);
                foreach (string key in textKeyList)
                    if (!string.IsNullOrEmpty(key) && !textKeys.Contains(key)) textKeys.Add(key);
                byte[] all = ReadAll();
                foreach (string key in AllKeys()) counts[key] = Count(all, key);
                lastStamp = Stamp();
            }

            IEnumerable<string> AllKeys()
            {
                foreach (string key in appKeys) yield return key;
                foreach (string key in textKeys) yield return key;
            }

            static byte[] ReadFile(string path)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        byte[] buffer = new byte[stream.Length];
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read == buffer.Length) return buffer;
                        byte[] shorter = new byte[read];
                        Buffer.BlockCopy(buffer, 0, shorter, 0, read);
                        return shorter;
                    }
                }
                catch (Exception) { return new byte[0]; }
            }

            byte[] ReadAll()
            {
                byte[] db = ReadFile(Path.Combine(dir, "wpndatabase.db"));
                byte[] wal = ReadFile(Path.Combine(dir, "wpndatabase.db-wal"));
                byte[] both = new byte[db.Length + wal.Length];
                Buffer.BlockCopy(db, 0, both, 0, db.Length);
                Buffer.BlockCopy(wal, 0, both, db.Length, wal.Length);
                return both;
            }

            string Stamp()
            {
                var parts = new List<string>();
                foreach (string name in new string[] { "wpndatabase.db", "wpndatabase.db-wal" })
                {
                    string path = Path.Combine(dir, name);
                    try
                    {
                        var info = new FileInfo(path);
                        parts.Add(info.Exists ? info.Length + "@" + info.LastWriteTimeUtc.Ticks : "-");
                    }
                    catch (Exception) { parts.Add("?"); }
                }
                return string.Join("|", parts.ToArray());
            }

            public static int Count(byte[] data, string keyword)
            {
                if (keyword == null || keyword.Length == 0 || data.Length == 0) return 0;
                string text = Encoding.UTF8.GetString(data);
                int hits = 0;
                int index = 0;
                while (true)
                {
                    index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    hits++;
                    index += keyword.Length;
                }
                return hits;
            }

            public string Poll(IReporter log)
            {
                if (appKeys.Count == 0 && textKeys.Count == 0) return null;
                string stamp = Stamp();
                if (stamp == lastStamp) return null;
                lastStamp = stamp;
                byte[] all = ReadAll();
                var increased = new List<string>();
                var detail = new List<string>();
                foreach (string key in AllKeys())
                {
                    int now = Count(all, key);
                    int before = counts.ContainsKey(key) ? counts[key] : 0;
                    int delta = now - before;
                    if (delta > 0) detail.Add(key + " " + before + " -> " + now + " (+" + delta + ")");
                    if (delta > 0 && delta <= 3) increased.Add(key);
                    counts[key] = Math.Max(before, now);
                }
                if (increased.Count == 0) return null;
                if (log != null) log.Info("通知库变化：" + string.Join("，", detail.ToArray()));
                bool appOk = appKeys.Count == 0;
                foreach (string key in appKeys) if (Count(all, key) > 0) appOk = true;
                bool textOk = textKeys.Count == 0;
                foreach (string key in textKeys) if (increased.Contains(key)) textOk = true;
                if (!(appOk && textOk)) return null;
                var hit = new List<string>();
                if (appKeys.Count > 0) hit.Add("来源 " + string.Join("/", appKeys.ToArray()));
                if (textKeys.Count > 0) hit.Add("新增文字 " + string.Join("/", increased.ToArray()));
                return string.Join("，", hit.ToArray());
            }
        }

        public static string NotifyDir(string configured)
        {
            if (!string.IsNullOrEmpty(configured)) return configured;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Microsoft", "Windows", "Notifications");
        }

        public static string WatchTask(Settings s, IReporter log, CancellationToken token, double started)
        {
            var patterns = s.TaskNames;
            var keywords = s.TaskLogKeywords;
            string file = string.IsNullOrEmpty(s.TaskLogPath) ? null : NewestFile(s.TaskLogPath);
            log.Info("等任务结束：目标进程 " + (patterns.Count > 0 ? string.Join("、", patterns.ToArray()) : "（未指定）"));
            if (file != null)
                log.Info("  日志：" + file + " → 出现关键字即算结束：" + string.Join("、", keywords.ToArray()));
            else if (keywords.Count > 0)
                log.Info("  警告：找不到日志文件 " + s.TaskLogPath + "，这个信号暂时用不了。");

            bool notifyOn = s.TaskNotifyText.Length > 0 || s.TaskNotifyApp.Length > 0;
            string notifyDir = NotifyDir(s.TaskNotifyDir);
            NotifyWatcher notifier = null;
            if (notifyOn)
            {
                log.Info("  系统通知信号：" + notifyDir + " 里" +
                    (s.TaskNotifyApp.Length > 0 ? " 来源含\"" + s.TaskNotifyApp + "\"" : "") +
                    (s.TaskNotifyText.Length > 0 ? " 文字含\"" + s.TaskNotifyText + "\"" : "") + " 的新通知");
                if (s.TaskNotifyApp.Length > 0 && s.TaskNotifyText.Length == 0)
                    log.Info("  提示：只填了来源、没填通知文字——这样任何通知变化都可能触发，建议再填一个文字关键字。");
                notifier = new NotifyWatcher(notifyDir, SplitList(s.TaskNotifyApp), SplitList(s.TaskNotifyText));
            }
            if (s.TaskCpuIdle > 0)
                log.Info("  CPU 空闲信号：连续 " + ((int)s.TaskCpuIdle) + " 秒低于 " +
                         s.TaskCpuPercent.ToString("0.#") + "% 算结束");

            var tailed = file == null ? null : new TailedFile(file);
            bool idleConfigured = s.TaskCpuIdle > 0;
            bool sawActive = !idleConfigured;
            double idleSince = 0;
            double prevCpu = ProcessCpuMilliseconds(patterns);
            double prevTime = Now();
            string note = "等待信号";

            while (true)
            {
                if (token.IsCancellationRequested) { log.Progress(""); return "stopped"; }

                if (tailed != null)
                {
                    string fresh = tailed.ReadNew();
                    if (fresh.Length > 0)
                    {
                        foreach (string keyword in keywords)
                        {
                            if (fresh.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                log.Progress("");
                                log.Info("日志出现关键字「" + keyword + "」，判定任务结束。");
                                return "finished";
                            }
                        }
                    }
                }

                if (notifier != null)
                {
                    string notifyHit = notifier.Poll(log);
                    if (notifyHit != null)
                    {
                        log.Progress("");
                        log.Info("收到系统通知（" + notifyHit + "），判定任务结束。");
                        return "finished";
                    }
                }

                if (idleConfigured)
                {
                    double cpu = ProcessCpuMilliseconds(patterns);
                    double now = Now();
                    double elapsed = Math.Max(0.001, now - prevTime);
                    double percent = (cpu - prevCpu) / (elapsed * 1000.0) * 100.0;
                    if (percent < 0) percent = 0;
                    prevCpu = cpu;
                    prevTime = now;
                    if (percent >= s.TaskCpuActivePercent)
                    {
                        sawActive = true;
                        idleSince = now;
                        note = "任务在跑（CPU " + percent.ToString("0.#") + "%）";
                    }
                    else if (!sawActive)
                    {
                        note = "等任务开始（还没见到 CPU 活跃）";
                    }
                    else
                    {
                        if (idleSince <= 0) idleSince = now;
                        double idleFor = now - idleSince;
                        if (idleFor >= s.TaskCpuIdle)
                        {
                            log.Progress("");
                            log.Info("CPU 已空闲 " + ((int)idleFor) + " 秒，判定任务结束。");
                            return "finished";
                        }
                        note = "已空闲 " + ((int)idleFor) + "/" + ((int)s.TaskCpuIdle) + " 秒";
                    }
                }

                log.Progress("等任务结束 · " + note);
                if (!Wait(s.TaskPoll, token)) { log.Progress(""); return "stopped"; }
            }
        }



        // ---------------------------------------------------------- 任务结束检测







        static Dictionary<int, string> pidToImage;

        public static List<string[]> WindowList()
        {
            var rows = new List<string[]>();
            try
            {
                EnumWindows(delegate(IntPtr hwnd, IntPtr param)
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    var buffer = new StringBuilder(512);
                    if (GetWindowTextW(hwnd, buffer, buffer.Capacity) <= 0) return true;
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    rows.Add(new string[] { ((int)pid).ToString(), buffer.ToString() });
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { }
            return rows;
        }

        public static void RefreshPidMap()
        {
            var map = new Dictionary<int, string>();
            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    try { map[process.Id] = process.ProcessName.ToLowerInvariant(); }
                    catch (Exception) { }
                    finally { process.Dispose(); }
                }
            }
            catch (Exception) { }
            pidToImage = map;
        }



        public static string MatchedTitle(List<string> processPatterns, List<string> titleKeywords)
        {
            if (titleKeywords.Count == 0 || pidToImage == null) return null;
            foreach (string[] row in WindowList())
            {
                string title = row[1];
                string keyword = null;
                foreach (string candidate in titleKeywords)
                    if (title.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) { keyword = candidate; break; }
                if (keyword == null) continue;
                string image;
                if (!pidToImage.TryGetValue(int.Parse(row[0]), out image)) continue;
                if (processPatterns.Count > 0 && !PatternMatches(processPatterns, image)) continue;
                return keyword + "（窗口标题：" + title.Trim() + "）";
            }
            return null;
        }



        public static List<string> FindOverlaps(Control root)
        {
            var problems = new List<string>();
            CheckSiblings(root, problems);
            return problems;
        }

        static void CheckSiblings(Control parent, List<string> problems)
        {
            var visible = new List<Control>();
            foreach (Control child in parent.Controls)
                if (child.Visible && child.Width > 0 && child.Height > 0) visible.Add(child);
            for (int i = 0; i < visible.Count; i++)
                for (int j = i + 1; j < visible.Count; j++)
                {
                    Rectangle a = visible[i].Bounds;
                    Rectangle b = visible[j].Bounds;
                    if (!a.IntersectsWith(b)) continue;
                    Rectangle overlap = Rectangle.Intersect(a, b);
                    if (overlap.Width * overlap.Height < 40) continue;
                    problems.Add(visible[i].Text + "[" + visible[i].Name + "] 与 " +
                                 visible[j].Text + "[" + visible[j].Name + "] 重叠 " +
                                 overlap.Width + "x" + overlap.Height);
                }
            foreach (Control child in parent.Controls) CheckSiblings(child, problems);
        }

        public static string BuildArguments(Settings s)
        {
            var args = new List<string>();
            if (s.Mode == "task")
            {
                args.Add("--task \"" + string.Join(",", s.TaskNames.ToArray()) + "\"");
                if (!string.IsNullOrEmpty(s.TaskLogPath)) args.Add("--task-log \"" + s.TaskLogPath + "\"");
                if (s.TaskLogKeywords.Count > 0) args.Add("--task-log-keyword \"" + string.Join(",", s.TaskLogKeywords.ToArray()) + "\"");
                if (s.TaskNotifyApp.Length > 0) args.Add("--task-notify-app \"" + s.TaskNotifyApp + "\"");
                if (s.TaskNotifyText.Length > 0) args.Add("--task-notify-text \"" + s.TaskNotifyText + "\"");
                if (s.TaskCpuIdle > 0) args.Add("--task-cpu-idle " + s.TaskCpuIdle.ToString("0.###"));
                args.Add("--task-cpu-percent " + s.TaskCpuPercent.ToString("0.###"));
            }
            else if (s.Mode == "timer")
            {
                if (s.TimerUseClock) args.Add("--at " + s.TimerClock);
                else args.Add("--in " + s.TimerMinutes.ToString("0.###"));
            }
            else if (s.Mode == "download" && s.DownloadDirs.Count > 0)
            {
                args.Add("--download-dir \"" + s.DownloadDirs[0] + "\"");
                args.Add("--idle " + s.Idle.ToString("0.###"));
                args.Add("--min-size " + s.MinMb.ToString("0.###"));
                args.Add("--start-timeout " + s.StartTimeout.ToString("0.###"));
                if (!s.Recurse) args.Add("--no-recurse");
            }
            args.Add("--delay " + s.Delay);
            args.Add("--action " + s.Action.ToString().ToLowerInvariant());
            if (s.Force) args.Add("--force");
            if (s.DryRun) args.Add("--dry-run");
            return string.Join(" ", args.ToArray());
        }
    }

    class FormReporter : IReporter
    {
        readonly MainForm form;
        public FormReporter(MainForm form) { this.form = form; }

        public void Info(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            form.Post(delegate()
            {
                form.AppendLog(line);
                if (message.IndexOf("已停止") >= 0 || message.IndexOf("已完成") >= 0 ||
                    message.IndexOf("已取消") >= 0 || message.IndexOf("任务结束") >= 0)
                    form.SetCount("已结束");
            });
        }

        public void Raw(string line)
        {
            form.Post(delegate() { form.AppendLog("  " + line); });
        }

        public void Progress(string text)
        {
            form.Post(delegate() { form.SetDetail(text); });
        }
    }

    class MainForm : Form
    {
        readonly ComboBox modeBox = new ComboBox();
        readonly Panel panelDownload = new Panel();
        readonly Panel panelTimer = new Panel();
        readonly Panel panelTask = new Panel();
        readonly TextBox taskNamesBox = new TextBox(), taskLogBox = new TextBox();
        readonly TextBox taskKeywordBox = new TextBox();
        readonly NumericUpDown cpuIdleBox = new NumericUpDown(), cpuPctBox = new NumericUpDown();
        readonly TextBox notifyAppBox = new TextBox(), notifyTextBox = new TextBox();
        readonly RadioButton timerCountRadio = new RadioButton(), timerClockRadio = new RadioButton();
        readonly NumericUpDown timerMinutesBox = new NumericUpDown();
        readonly TextBox clockBox = new TextBox();
        readonly Label timerHint = new Label();
        readonly TextBox folderBox = new TextBox();
        readonly NumericUpDown idleBox = new NumericUpDown(), minBox = new NumericUpDown();
        readonly NumericUpDown delayBox = new NumericUpDown();
        readonly CheckBox recurseBox = new CheckBox();
        readonly CheckBox forceBox = new CheckBox(), dryBox = new CheckBox();
        readonly Label countLabel = new Label(), detailLabel = new Label();
        readonly Button startBtn = new Button(), stopBtn = new Button(), detachedBtn = new Button();
        readonly Button cancelBtn = new Button(), quitBtn = new Button();
        readonly TextBox logBox = new TextBox();
        readonly RadioButton noneRadio = new RadioButton(), shutdownRadio = new RadioButton();
        readonly RadioButton restartRadio = new RadioButton(), hibernateRadio = new RadioButton();
        readonly RadioButton logoffRadio = new RadioButton();

        Thread worker;
        CancellationTokenSource cts;

        public MainForm() : this(0) { }

        public MainForm(int modeIndex)
        {
            Text = Core.App;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(668, 760);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var title = new Label();
            title.Text = "程序跑完（含 DSH、MAA）/ 下载完毕 → 自动关机";
            title.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            title.SetBounds(14, 10, 620, 24);
            Controls.Add(title);

            var modeLabel = new Label();
            modeLabel.Text = "方式";
            modeLabel.SetBounds(14, 44, 40, 22);
            Controls.Add(modeLabel);
            modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeBox.Items.AddRange(new object[] { "下载完成（盯住下载文件夹）", "定时关机（到点就关）", "任务结束（程序不用关，如 DSH / MAA）" });
            modeBox.SetBounds(60, 41, 320, 24);
            modeIndex = Math.Max(0, Math.Min(2, modeIndex));
            modeBox.SelectedIndex = modeIndex;
            modeBox.SelectedIndexChanged += delegate { UpdateMode(); };
            Controls.Add(modeBox);

            var taskBox = new GroupBox();
            taskBox.Text = " 等什么结束 ";
            taskBox.SetBounds(14, 74, 640, 200);
            Controls.Add(taskBox);

            foreach (var panel in new Panel[] { panelDownload, panelTimer, panelTask })
            {
                panel.SetBounds(10, 22, 620, 168);
                panel.Visible = false;
                taskBox.Controls.Add(panel);
            }
            BuildDownloadPanel();
            BuildTimerPanel();
            BuildTaskPanel();

            var planBox = new GroupBox();
            planBox.Text = " 结束后做什么 ";
            planBox.SetBounds(14, 284, 640, 124);
            Controls.Add(planBox);
            int x = 14;
            foreach (RadioButton radio in new RadioButton[] { shutdownRadio, restartRadio, hibernateRadio, logoffRadio, noneRadio })
            {
                radio.SetBounds(x, 24, 62, 22);
                x += 64;
                planBox.Controls.Add(radio);
            }
            shutdownRadio.Text = "关机";
            restartRadio.Text = "重启";
            hibernateRadio.Text = "休眠";
            logoffRadio.Text = "注销";
            noneRadio.Text = "不操作";
            shutdownRadio.Checked = true;

            var delayLabel = new Label();
            delayLabel.Text = "反悔时间";
            delayLabel.SetBounds(14, 58, 60, 22);
            planBox.Controls.Add(delayLabel);
            delayBox.SetBounds(78, 55, 70, 24);
            delayBox.Minimum = 0;
            delayBox.Maximum = 86400;
            delayBox.Value = 60;
            planBox.Controls.Add(delayBox);
            var delayTail = new Label();
            delayTail.Text = "秒后执行";
            delayTail.SetBounds(154, 58, 70, 22);
            planBox.Controls.Add(delayTail);
            forceBox.Text = "强制关闭未保存的程序";
            forceBox.SetBounds(232, 57, 190, 22);
            planBox.Controls.Add(forceBox);
            dryBox.Text = "演练模式（只显示要执行的命令，不真的关机）";
            dryBox.SetBounds(14, 88, 330, 22);
            planBox.Controls.Add(dryBox);

            countLabel.Text = "等待开始";
            countLabel.Font = new Font(Font.FontFamily, 20F, FontStyle.Bold);
            countLabel.SetBounds(16, 414, 620, 38);
            Controls.Add(countLabel);
            detailLabel.Text = "选一种方式，然后点“开始”。";
            detailLabel.ForeColor = Color.FromArgb(90, 90, 90);
            detailLabel.SetBounds(16, 456, 636, 38);
            Controls.Add(detailLabel);

            StyleButton(startBtn, "开始", 16, true);
            startBtn.Click += delegate { StartSession(); };
            StyleButton(stopBtn, "停止", 112, false);
            stopBtn.Enabled = false;
            stopBtn.Click += delegate { StopSession(); };
            StyleButton(detachedBtn, "后台独立运行", 208, false);
            detachedBtn.Click += delegate { StartDetached(); };
            StyleButton(cancelBtn, "取消已安排的关机", 366, false);
            cancelBtn.Click += delegate { CancelPending(); };
            StyleButton(quitBtn, "退出", 526, false);
            quitBtn.Click += delegate { Close(); };

            var logLabel = new Label();
            logLabel.Text = "记录";
            logLabel.SetBounds(16, 534, 60, 20);
            Controls.Add(logLabel);
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.FromArgb(247, 247, 247);
            logBox.SetBounds(16, 556, 636, 190);
            logBox.Font = new Font("Consolas", 9F);
            Controls.Add(logBox);

            UpdateMode();
            AppendLog("就绪。三种方式：定时关机 / 下载完成 / 任务结束。");
            AppendLog("等 DSH、MAA 这轮任务跑完（程序继续开着）：方式选“任务结束”，点“DSH 任务结束 / MAA 任务结束”。");
            FormClosing += OnClosing;
        }

        void StyleButton(Button button, string text, int left, bool primary)
        {
            button.Text = text;
            button.SetBounds(left, 496, primary ? 88 : (text.Length > 4 ? 150 : 88), 30);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            if (primary)
            {
                button.BackColor = Color.FromArgb(26, 115, 232);
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderSize = 0;
            }
            Controls.Add(button);
        }

        void BuildDownloadPanel()
        {
            var folderLabel = new Label();
            folderLabel.Text = "下载文件夹";
            folderLabel.SetBounds(0, 6, 80, 22);
            panelDownload.Controls.Add(folderLabel);
            folderBox.SetBounds(84, 3, 430, 24);
            folderBox.Text = Core.DefaultDownloadDirs()[0];
            panelDownload.Controls.Add(folderBox);
            var browse = new Button();
            browse.Text = "浏览…";
            browse.SetBounds(520, 2, 90, 26);
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择下载文件夹";
                    if (Directory.Exists(folderBox.Text)) dialog.SelectedPath = folderBox.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) folderBox.Text = dialog.SelectedPath;
                }
            };
            panelDownload.Controls.Add(browse);

            var idleLabel = new Label();
            idleLabel.Text = "停止变化";
            idleLabel.SetBounds(0, 42, 80, 22);
            panelDownload.Controls.Add(idleLabel);
            idleBox.SetBounds(84, 39, 70, 24);
            idleBox.Minimum = 5;
            idleBox.Maximum = 86400;
            idleBox.Value = 30;
            panelDownload.Controls.Add(idleBox);
            var idleTail = new Label();
            idleTail.Text = "秒内文件不再变大、且没有 .crdownload / .part 等临时文件 → 算下载完";
            idleTail.SetBounds(162, 42, 450, 22);
            panelDownload.Controls.Add(idleTail);

            var minLabel = new Label();
            minLabel.Text = "下限";
            minLabel.SetBounds(0, 74, 80, 22);
            panelDownload.Controls.Add(minLabel);
            minBox.SetBounds(84, 71, 70, 24);
            minBox.Minimum = 0;
            minBox.Maximum = 1024000;
            minBox.Value = 1;
            panelDownload.Controls.Add(minBox);
            var minTail = new Label();
            minTail.Text = "MB，新增内容少于它就继续等，避免误关机";
            minTail.SetBounds(162, 74, 420, 22);
            panelDownload.Controls.Add(minTail);

            recurseBox.Text = "包含子文件夹";
            recurseBox.SetBounds(84, 104, 160, 22);
            recurseBox.Checked = true;
            panelDownload.Controls.Add(recurseBox);
        }


        void UpdateMode()
        {
            panelDownload.Visible = modeBox.SelectedIndex == 0;
            panelTimer.Visible = modeBox.SelectedIndex == 1;
            panelTask.Visible = modeBox.SelectedIndex == 2;
        }

        void BuildTaskPanel()
        {
            var namesLabel = new Label();
            namesLabel.Text = "目标进程";
            namesLabel.SetBounds(0, 6, 80, 22);
            panelTask.Controls.Add(namesLabel);
            taskNamesBox.SetBounds(84, 3, 296, 24);
            panelTask.Controls.Add(taskNamesBox);
            var maaBtn = new Button();
            maaBtn.Text = "MAA 任务结束";
            maaBtn.SetBounds(388, 2, 120, 26);
            maaBtn.Click += delegate { ApplyMaaPreset(); };
            panelTask.Controls.Add(maaBtn);
            var dshBtn = new Button();
            dshBtn.Text = "DSH 任务结束";
            dshBtn.SetBounds(514, 2, 120, 26);
            dshBtn.Click += delegate { ApplyDshPreset(); };
            panelTask.Controls.Add(dshBtn);

            var logLabel = new Label();
            logLabel.Text = "日志文件";
            logLabel.SetBounds(0, 42, 80, 22);
            panelTask.Controls.Add(logLabel);
            taskLogBox.SetBounds(84, 39, 430, 24);
            panelTask.Controls.Add(taskLogBox);
            var browse = new Button();
            browse.Text = "选择…";
            browse.SetBounds(520, 38, 90, 26);
            browse.Click += delegate
            {
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "选择要盯的日志文件";
                    dialog.Filter = "日志文件|*.log;*.txt|所有文件|*.*";
                    if (dialog.ShowDialog(this) == DialogResult.OK) taskLogBox.Text = dialog.FileName;
                }
            };
            panelTask.Controls.Add(browse);

            var keywordLabel = new Label();
            keywordLabel.Text = "日志关键字";
            keywordLabel.SetBounds(0, 74, 80, 22);
            panelTask.Controls.Add(keywordLabel);
            taskKeywordBox.SetBounds(84, 71, 240, 24);
            panelTask.Controls.Add(taskKeywordBox);
            var keywordTip = new Label();
            keywordTip.Text = "日志里新出现这个词就算结束（逗号分隔，如 MAA 的 AllTasksCompleted）";
            keywordTip.ForeColor = Color.FromArgb(90, 90, 90);
            keywordTip.SetBounds(330, 74, 300, 22);
            panelTask.Controls.Add(keywordTip);

            var cpuLabel = new Label();
            cpuLabel.Text = "CPU 空闲";
            cpuLabel.SetBounds(0, 106, 80, 22);
            panelTask.Controls.Add(cpuLabel);
            cpuIdleBox.SetBounds(84, 103, 70, 24);
            cpuIdleBox.Maximum = 86400;
            cpuIdleBox.Value = 0;
            panelTask.Controls.Add(cpuIdleBox);
            var cpuTail = new Label();
            cpuTail.Text = "秒内连续低于";
            cpuTail.SetBounds(160, 106, 92, 22);
            panelTask.Controls.Add(cpuTail);
            cpuPctBox.SetBounds(252, 103, 60, 24);
            cpuPctBox.Maximum = 100;
            cpuPctBox.DecimalPlaces = 1;
            cpuPctBox.Value = 3;
            panelTask.Controls.Add(cpuPctBox);
            var cpuTail2 = new Label();
            cpuTail2.Text = "% 就算结束（0 秒 = 不用这个信号）";
            cpuTail2.SetBounds(320, 106, 300, 22);
            panelTask.Controls.Add(cpuTail2);
        }

        void ApplyMaaPreset()
        {
            taskNamesBox.Text = "MAA";
            taskKeywordBox.Text = "AllTasksCompleted";
            cpuIdleBox.Value = 0;
            AppendLog("正在查找 MAA 的 debug\\asst.log …");
            var finder = new Thread(delegate()
            {
                string found = Core.FindMaaLog();
                Post(delegate()
                {
                    taskLogBox.Text = found ?? "";
                    AppendLog(found == null
                        ? "没自动找到 MAA 日志，请点“选择…”手动指定 debug\\asst.log。"
                        : "已找到 MAA 日志：" + found);
                });
            });
            finder.IsBackground = true;
            finder.Start();
        }

        void ApplyDshPreset()
        {
            taskNamesBox.Text = "DSH";
            taskLogBox.Text = "";
            taskKeywordBox.Text = "";
            cpuIdleBox.Value = 300;
            cpuPctBox.Value = 3;
            AppendLog("DSH 用 CPU 空闲信号：连续 300 秒低于 3% 就认为这轮任务跑完了（程序继续开着）。");
        }

        void BuildTimerPanel()
        {
            timerCountRadio.Text = "倒计时";
            timerCountRadio.SetBounds(0, 6, 80, 22);
            timerCountRadio.Checked = true;
            timerCountRadio.CheckedChanged += delegate { UpdateTimerInputs(); };
            panelTimer.Controls.Add(timerCountRadio);

            timerMinutesBox.SetBounds(88, 3, 80, 24);
            timerMinutesBox.Minimum = 0;
            timerMinutesBox.Maximum = 100000;
            timerMinutesBox.Value = 30;
            timerMinutesBox.ValueChanged += delegate { UpdateTimerInputs(); };
            panelTimer.Controls.Add(timerMinutesBox);
            var tail = new Label();
            tail.Text = "分钟后执行（0 = 立即执行）";
            tail.SetBounds(176, 6, 300, 22);
            panelTimer.Controls.Add(tail);

            var quick = new Label();
            quick.Text = "常用";
            quick.SetBounds(0, 40, 80, 22);
            panelTimer.Controls.Add(quick);
            int x = 88;
            foreach (object[] pair in new object[][]
            {
                new object[] { "10 分钟", 10m }, new object[] { "30 分钟", 30m },
                new object[] { "1 小时", 60m }, new object[] { "2 小时", 120m }
            })
            {
                var button = new Button();
                button.Text = (string)pair[0];
                button.SetBounds(x, 37, 86, 26);
                decimal minutes = (decimal)pair[1];
                button.Click += delegate { timerCountRadio.Checked = true; timerMinutesBox.Value = minutes; UpdateTimerInputs(); };
                panelTimer.Controls.Add(button);
                x += 92;
            }

            timerClockRadio.Text = "指定时刻";
            timerClockRadio.SetBounds(0, 74, 80, 22);
            timerClockRadio.CheckedChanged += delegate { UpdateTimerInputs(); };
            panelTimer.Controls.Add(timerClockRadio);
            clockBox.SetBounds(88, 71, 80, 24);
            clockBox.Text = "23:30";
            clockBox.TextChanged += delegate { UpdateTimerInputs(); };
            panelTimer.Controls.Add(clockBox);
            var clockTail = new Label();
            clockTail.Text = "时:分，例如 23:30（这个点已经过了就算明天）";
            clockTail.SetBounds(176, 74, 400, 22);
            panelTimer.Controls.Add(clockTail);

            timerHint.SetBounds(0, 112, 600, 40);
            timerHint.ForeColor = Color.FromArgb(90, 90, 90);
            panelTimer.Controls.Add(timerHint);
            UpdateTimerInputs();
        }

        void UpdateTimerInputs()
        {
            timerMinutesBox.Enabled = timerCountRadio.Checked;
            clockBox.Enabled = timerClockRadio.Checked;
            if (timerCountRadio.Checked)
            {
                double minutes = (double)timerMinutesBox.Value;
                timerHint.Text = "计划执行：" + DateTime.Now.AddMinutes(minutes).ToString("yyyy-MM-dd HH:mm:ss") +
                                 "（约 " + Core.FormatHms(minutes * 60) + " 后）";
            }
            else
            {
                int hh, mm;
                if (!Core.TryParseClock(clockBox.Text, out hh, out mm))
                {
                    timerHint.Text = "时间格式请写成 23:30 这样。";
                    return;
                }
                int seconds = Core.SecondsUntilClock(hh, mm);
                timerHint.Text = "计划执行：" + DateTime.Now.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm") +
                                 "（约 " + Core.FormatHms(seconds) + " 后）";
            }
        }

        public void Post(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (Exception) { }
        }

        public void AppendLog(string line)
        {
            if (logBox.IsDisposed) return;
            logBox.AppendText(line + Environment.NewLine);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        public void SetCount(string text) { countLabel.Text = text; }
        public void SetDetail(string text) { detailLabel.Text = text; }

        Settings Collect()
        {
            var s = new Settings();
            s.Delay = (int)delayBox.Value;
            s.Force = forceBox.Checked;
            s.DryRun = dryBox.Checked;
            if (restartRadio.Checked) s.Action = PowerAction.Restart;
            else if (hibernateRadio.Checked) s.Action = PowerAction.Hibernate;
            else if (logoffRadio.Checked) s.Action = PowerAction.Logoff;
            else if (noneRadio.Checked) s.Action = PowerAction.None;
            else s.Action = PowerAction.Shutdown;

            if (modeBox.SelectedIndex == 0)
            {
                s.Mode = "download";
                string folder = folderBox.Text.Trim();
                if (folder.Length == 0 || !Directory.Exists(folder))
                    throw new Exception("下载文件夹不存在：" + folder);
                s.DownloadDirs.Add(folder);
                s.Idle = (double)idleBox.Value;
                s.MinMb = (double)minBox.Value;
                s.StartTimeout = 0;
                s.Recurse = recurseBox.Checked;
            }
            else if (modeBox.SelectedIndex == 2)
            {
                s.Mode = "task";
                s.TaskNames = Core.SplitList(taskNamesBox.Text);
                if (s.TaskNames.Count == 0) throw new Exception("请填写目标进程名（如 DSH 或 MAA）。");
                s.TaskLogPath = taskLogBox.Text.Trim();
                s.TaskLogKeywords = Core.SplitList(taskKeywordBox.Text);
                s.TaskCpuIdle = (double)cpuIdleBox.Value;
                s.TaskCpuPercent = (double) cpuPctBox.Value;
                    throw new Exception("至少要启用一个“结束信号”：日志关键字 / 标题关键字 / CPU 空闲。");
            }
            else if (modeBox.SelectedIndex == 1)
            {
                s.Mode = "timer";
                s.TimerUseClock = timerClockRadio.Checked;
                s.TimerMinutes = (double)timerMinutesBox.Value;
                if (s.TimerUseClock)
                {
                    int hh, mm;
                    if (!Core.TryParseClock(clockBox.Text, out hh, out mm))
                        throw new Exception("请把时刻写成 23:30 这样。");
                    s.TimerClock = clockBox.Text.Trim();
                    s.TimerSeconds = Core.SecondsUntilClock(hh, mm);
                }
                else
                {
                    s.TimerSeconds = (int)Math.Round(s.TimerMinutes * 60);
                }
            }
            return s;
        }

        void StartSession()
        {
            if (worker != null && worker.IsAlive) return;
            Settings settings;
            try { settings = Collect(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Core.App); return; }
            cts = new CancellationTokenSource();
            startBtn.Enabled = false;
            stopBtn.Enabled = true;
            countLabel.Text = "进行中…";
            AppendLog("开始任务。");
            var reporter = new FormReporter(this);
            var token = cts.Token;
            worker = new Thread(delegate()
            {
                int code = 1;
                try { code = Core.RunSession(settings, reporter, token); }
                catch (Exception ex) { reporter.Info("出错：" + ex.Message); code = 1; }
                Post(delegate { FinishSession(code); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void FinishSession(int code)
        {
            stopBtn.Enabled = false;
            startBtn.Enabled = true;
            if (code == 0) countLabel.Text = "已完成";
            else if (code == 130) countLabel.Text = "已停止";
            else countLabel.Text = "已结束（代码 " + code + "）";
        }

        void StopSession()
        {
            if (cts != null) cts.Cancel();
            AppendLog("已请求停止…");
        }

        void StartDetached()
        {
            if (worker != null && worker.IsAlive) { MessageBox.Show(this, "请先停止当前任务。", Core.App); return; }
            Settings settings;
            try { settings = Collect(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Core.App); return; }
            string logPath = Core.DefaultLogPath();
            string arguments = Core.BuildArguments(settings) + " --log \"" + logPath + "\"";
            bool broke;
            int pid = Native.StartDetached(Application.ExecutablePath, arguments, out broke);
            if (pid < 0) { MessageBox.Show(this, "后台启动失败。", Core.App); return; }
            countLabel.Text = "后台运行中";
            AppendLog("已在后台独立运行（PID " + pid + "）。关掉本窗口、甚至关掉 DSH 都不影响它。");
            AppendLog("日志：" + logPath);
            if (!broke) AppendLog("提示：系统不允许完全脱离当前进程组；从 DSH 内部启动时建议直接双击 exe。");
        }

        void CancelPending()
        {
            string output;
            bool ok = Core.RunShutdown("/a", new NullReporter(), dryBox.Checked, out output);
            AppendLog(ok ? "已取消已安排的关机。" : "取消失败（可能本来就没有待执行的任务）。");
        }


        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (worker != null && worker.IsAlive)
            {
                if (MessageBox.Show(this, "任务还在进行，确定要退出吗？", Core.App,
                        MessageBoxButtons.OKCancel) != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
                if (cts != null) cts.Cancel();
            }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0 ||
                (args.Length == 1 && (args[0] == "--gui" || args[0] == "/gui")))
                return RunGui();

            string logPath = null;
            var settings = new Settings();
            settings.Mode = null;
            bool selftest = false, list = false, cancel = false, detached = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--gui": return RunGui();
                    case "--selftest": selftest = true; break;
                    case "--list": list = true; break;
                    case "--find-maa-log":
                    {
                        var findReporter = new FileReporter(logPath ?? Core.DefaultLogPath());
                        string foundLog = Core.FindMaaLog();
                        findReporter.Info(foundLog == null ? "没找到 MAA 的 debug\\asst.log" : "MAA 日志：" + foundLog);
                        findReporter.Close();
                        return foundLog == null ? 1 : 0;
                    }
                    case "--cancel": cancel = true; break;
                    case "--detached": detached = true; break;
                    case "--dry-run": settings.DryRun = true; break;
                    case "--force": settings.Force = true; break;
                    case "--no-recurse": settings.Recurse = false; break;
                    case "--download": settings.Mode = "download"; break;
                    case "--task":
                        settings.Mode = "task";
                        if (i + 1 < args.Length) settings.TaskNames.AddRange(Core.SplitList(args[++i]));
                        break;
                    case "--task-log":
                        settings.Mode = "task";
                        if (i + 1 < args.Length) settings.TaskLogPath = args[++i];
                        break;
                    case "--task-log-keyword":
                        settings.Mode = "task";
                        if (i + 1 < args.Length) settings.TaskLogKeywords.AddRange(Core.SplitList(args[++i]));
                        break;
                    case "--task-notify-app":
                        settings.Mode = "task";
                        if (i + 1 < args.Length) settings.TaskNotifyApp = args[++i];
                        break;
                    case "--task-notify-text":
                        settings.Mode = "task";
                        if (i + 1 < args.Length) settings.TaskNotifyText = args[++i];
                        break;
                    case "--task-notify-dir":
                        settings.Mode = "task";
                        if (i + 1 < args.Length) settings.TaskNotifyDir = args[++i];
                        break;
                    case "--task-cpu-idle":
                        settings.Mode = "task";
                        if (i + 1 < args.Length) settings.TaskCpuIdle = double.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--task-cpu-percent":
                        if (i + 1 < args.Length) settings.TaskCpuPercent = double.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--task-poll":
                        if (i + 1 < args.Length) settings.TaskPoll = double.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--in":
                        settings.Mode = "timer";
                        settings.TimerUseClock = false;
                        if (i + 1 < args.Length) settings.TimerMinutes = double.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--at":
                        settings.Mode = "timer";
                        settings.TimerUseClock = true;
                        if (i + 1 < args.Length) settings.TimerClock = args[++i];
                        break;
                    case "--download-dir":
                        settings.Mode = "download";
                        if (i + 1 < args.Length) settings.DownloadDirs.Add(args[++i]);
                        break;
                    case "--idle": if (i + 1 < args.Length) settings.Idle = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--min-size": if (i + 1 < args.Length) settings.MinMb = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--start-timeout": if (i + 1 < args.Length) settings.StartTimeout = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--download-poll": if (i + 1 < args.Length) settings.DownloadPoll = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--delay": if (i + 1 < args.Length) settings.Delay = (int)double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--action": if (i + 1 < args.Length) settings.Action = Core.ParseAction(args[++i]); break;
                    case "--log": if (i + 1 < args.Length) logPath = args[++i]; break;
                    default:
                        if (arg.StartsWith("--log=")) logPath = arg.Substring(6);
                        break;
                }
            }

            var reporter = new FileReporter(logPath ?? Core.DefaultLogPath());
            if (selftest)
            {
                int failed = SelfTest(reporter);
                reporter.Close();
                return failed;
            }
            if (list)
            {
                var map = Core.ProcessMap();
                reporter.Info("正在运行的程序（" + map.Count + " 个）：");
                foreach (var name in map.Values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                    reporter.Info("  " + name);
                reporter.Close();
                return 0;
            }
            if (cancel)
            {
                string output;
                bool ok = Core.RunShutdown("/a", reporter, settings.DryRun, out output);
                reporter.Info((ok ? "已取消。" : "取消失败（可能本来就没有待执行的任务）。") + output);
                reporter.Close();
                return ok ? 0 : 1;
            }
            if (settings.Mode == null)
            {
                reporter.Info("没有指定要做什么。用 --watch NAME / --download / --run \"命令\"，或直接双击打开界面。");
                reporter.Close();
                return 2;
            }
            if (settings.Mode == "download" && settings.DownloadDirs.Count == 0)
                settings.DownloadDirs = Core.DefaultDownloadDirs();
            if (settings.Mode == "timer")
            {
                if (settings.TimerUseClock)
                {
                    int hh, mm;
                    if (!Core.TryParseClock(settings.TimerClock, out hh, out mm))
                    {
                        reporter.Info("时间格式不对：" + settings.TimerClock + "，请用 23:30 这样的写法。");
                        reporter.Close();
                        return 2;
                    }
                    settings.TimerSeconds = Core.SecondsUntilClock(hh, mm);
                }
                else
                {
                    settings.TimerSeconds = (int)Math.Round(Math.Max(0, settings.TimerMinutes) * 60);
                }
            }

            if (detached)
            {
                var forward = new List<string>();
                foreach (string arg in args) if (arg != "--detached") forward.Add(Quote(arg));
                string forwardText = string.Join(" ", forward.ToArray());
                string target = logPath ?? Core.DefaultLogPath();
                if (forwardText.IndexOf("--log") < 0) forwardText += " --log " + Quote(target);
                bool broke;
                int pid = Native.StartDetached(Application.ExecutablePath, forwardText, out broke);
                reporter.Info(pid < 0 ? "后台启动失败。" : "已在后台独立运行（PID " + pid + "），日志：" + target);
                reporter.Close();
                return pid < 0 ? 1 : 0;
            }

            var token = CancellationToken.None;
            int code = Core.RunSession(settings, reporter, token);
            reporter.Close();
            return code;
        }

        static string Quote(string text)
        {
            return text.IndexOf(' ') >= 0 || text.IndexOf('/') >= 0 ? "\"" + text + "\"" : text;
        }

        static int RunGui()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        static int SelfTest(IReporter log)
        {
            int failed = 0;
            Action<string, object, object> check = delegate(string name, object got, object want)
            {
                if (!object.Equals(got, want))
                {
                    failed++;
                    log.Info("FAIL " + name + "：得到 " + got + "，期望 " + want);
                }
            };

            check("FormatHms", Core.FormatHms(3725), "01:02:05");
            check("FormatHms 进位", Core.FormatHms(59.4), "00:01:00");
            check("FormatElapsed", Core.FormatElapsed(1.24), "1.2 秒");
            check("FormatSize", Core.FormatSize(1.5 * 1024 * 1024), "1.5 MB");
            check("关机命令", Core.ActionArgs(PowerAction.Shutdown, 60, false), "/s /t 60");
            check("重启+强制", Core.ActionArgs(PowerAction.Restart, 90, true), "/r /t 90 /f");
            check("注销无计时", Core.ActionArgs(PowerAction.Logoff, 30, false), "/l");
            check("休眠", Core.ActionArgs(PowerAction.Hibernate, 0, false), "/h");
            check("系统计时", Core.HasOsTimer(PowerAction.Shutdown), true);
            check("休眠无系统计时", Core.HasOsTimer(PowerAction.Hibernate), false);
            check("列表解析", string.Join(",", Core.SplitList("A.exe, b.exe ;c.exe").ToArray()), "A.exe,b.exe,c.exe");

            int hh = 0, mm = 0;
            check("时间解析 23:30", Core.TryParseClock("23:30", out hh, out mm) && hh == 23 && mm == 30, true);
            check("时间解析 7：05", Core.TryParseClock("7：05", out hh, out mm) && hh == 7 && mm == 5, true);
            check("时间解析 2330", Core.TryParseClock("2330", out hh, out mm) && hh == 23 && mm == 30, true);
            check("时间解析 非法 25:00", Core.TryParseClock("25:00", out hh, out mm), false);
            check("时间解析 非法 abc", Core.TryParseClock("abc", out hh, out mm), false);
            int untilNextMinute = Core.SecondsUntilClock(DateTime.Now.Hour, DateTime.Now.Minute);
            check("时钟剩余时间合理", untilNextMinute >= 5 && untilNextMinute <= 86400, true);


            var probe = new Panel();
            var probeA = new Label();
            var probeB = new Label();
            probeA.SetBounds(0, 0, 60, 24);
            probeB.SetBounds(20, 8, 60, 24);
            probe.Controls.Add(probeA);
            probe.Controls.Add(probeB);
            check("重叠检测器有效（故意重叠应被抓到）", Core.FindOverlaps(probe).Count > 0, true);

            try
            {
                string[] modeNames = new string[] { "下载", "定时", "任务" };
                for (int mode = 0; mode < 3; mode++)
                {
                    var form = new MainForm(mode);
                    foreach (Control control in form.Controls) control.PerformLayout();
                    var overlaps = Core.FindOverlaps(form);
                    foreach (string item in overlaps) log.Info("界面重叠（" + modeNames[mode] + "）：" + item);
                    check("界面无重叠 · " + modeNames[mode] + "面板", overlaps.Count, 0);
                    form.Dispose();
                }
            }
            catch (Exception ex)
            {
                failed++;
                log.Info("FAIL 界面自检异常：" + ex.Message);
            }

            string taskDir = Path.Combine(Path.GetTempPath(), "asd_task_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(taskDir);
            string maaLog = Path.Combine(taskDir, "asst.log");
            File.WriteAllText(maaLog,
                "[2026-09-13 21:00:00.000][INF][Px1][Tx1] Assistant::append_callback | TaskChainCompleted {\"taskchain\":\"StartUp\"}\n");
            File.AppendAllText(maaLog, "[old] AllTasksCompleted {\"finished_tasks\":[1]}\n");
            var taskSettings = new Settings();
            taskSettings.Mode = "task";
            taskSettings.TaskNames.Add("definitely-not-running-xyz");
            taskSettings.TaskLogPath = maaLog;
            taskSettings.TaskLogKeywords.Add("AllTasksCompleted");
            taskSettings.TaskPoll = 0.3;
            taskSettings.Action = PowerAction.None;
            var staleWatch = new CancellationTokenSource(1500);
            check("启动前就有的关键字不误触发", Core.RunSession(taskSettings, log, staleWatch.Token), 130);
            var taskWriter = new Thread(delegate()
            {
                Thread.Sleep(500);
                File.AppendAllText(maaLog,
                    "[2026-09-13 21:33:53.554][INF][Px28740][Tx10214] Assistant::append_callback | AllTasksCompleted {\"finished_tasks\":[1,2,3,4,5,6]}\n");
            });
            taskWriter.IsBackground = true;
            taskWriter.Start();
            var taskOk = new CancellationTokenSource(20000);
            check("日志关键字判定任务结束", Core.RunSession(taskSettings, log, taskOk.Token), 0);
            taskWriter.Join(3000);
            try { Directory.Delete(taskDir, true); } catch (Exception) { }

            string notifyDir = Path.Combine(Path.GetTempPath(), "asd_notify_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(notifyDir);
            string fakeWal = Path.Combine(notifyDir, "wpndatabase.db-wal");
            File.WriteAllText(fakeWal, "<toast>old notification</toast>\n");
            var notifySettings = new Settings();
            notifySettings.Mode = "task";
            notifySettings.TaskNames.Add("none");
            notifySettings.TaskNotifyDir = notifyDir;
            notifySettings.TaskNotifyApp = "ai.deepseek.dsh.desktop";
            notifySettings.TaskNotifyText = "User Turn Completed,回合已完成";
            notifySettings.TaskPoll = 0.3;
            notifySettings.Action = PowerAction.None;
            var staleNotify = new CancellationTokenSource(1200);
            check("启动前的旧通知不误触发", Core.RunSession(notifySettings, log, staleNotify.Token), 130);
            var notifyWriter = new Thread(delegate()
            {
                Thread.Sleep(600);
                File.AppendAllText(fakeWal,
                    "<toast><text>ai.deepseek.dsh.desktop</text><text>用户回合已完成</text></toast>\n");
            });
            notifyWriter.IsBackground = true;
            notifyWriter.Start();
            var notifyOk = new CancellationTokenSource(20000);
            check("系统通知判定任务结束（中文关键字）", Core.RunSession(notifySettings, log, notifyOk.Token), 0);
            notifyWriter.Join(3000);
            try { Directory.Delete(notifyDir, true); } catch (Exception) { }

            var cpuSettings = new Settings();
            cpuSettings.Mode = "task";
            cpuSettings.TaskNames.Add(Process.GetCurrentProcess().ProcessName);
            cpuSettings.TaskCpuIdle = 2;
            cpuSettings.TaskCpuPercent = 5;
            cpuSettings.TaskCpuActivePercent = 10;
            cpuSettings.TaskPoll = 0.3;
            cpuSettings.Action = PowerAction.None;
            var burner = new Thread(delegate()
            {
                DateTime until = DateTime.UtcNow.AddSeconds(1.2);
                double sink = 0;
                while (DateTime.UtcNow < until) sink += Math.Sqrt(sink + 1.1);
                if (sink < 0) log.Info("（不该发生）");
            });
            burner.IsBackground = true;
            burner.Start();
            var cpuWatch = new CancellationTokenSource(15000);
            check("CPU 空闲判定任务结束", Core.RunSession(cpuSettings, log, cpuWatch.Token), 0);
            burner.Join(3000);
            check("进程名匹配", Core.PatternMatches(Core.SplitList("DSH"), "DSH Desktop.exe"), true);
            check("进程名匹配 反例", Core.PatternMatches(Core.SplitList("DSH"), "msedge.exe"), false);

            string temp = Path.Combine(Path.GetTempPath(), "asd_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string part = Path.Combine(temp, "big.part");
            Thread writer = new Thread(delegate()
            {
                using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        byte[] chunk = new byte[60000];
                        stream.Write(chunk, 0, chunk.Length);
                        stream.Flush();
                        Thread.Sleep(400);
                    }
                }
                File.Move(part, Path.Combine(temp, "big.bin"));
            });
            writer.IsBackground = true;
            writer.Start();
            var settings = new Settings();
            settings.Mode = "download";
            settings.DownloadDirs.Add(temp);
            settings.Idle = 1;
            settings.MinMb = 0;
            settings.StartTimeout = 20;
            settings.DownloadPoll = 0.3;
            settings.Action = PowerAction.None;
            int code = Core.RunSession(settings, log, CancellationToken.None);
            check("下载完成判定", code, 0);
            var scan = Core.ScanDownloads(new List<string> { temp }, true);
            check("下载大小统计", scan.Total, 240000L);
            writer.Join(3000);
            try { Directory.Delete(temp, true); } catch (Exception) { }

            log.Info(failed == 0 ? "自检通过，未执行任何真实关机命令。" : "自检失败 " + failed + " 项。");
            return failed == 0 ? 0 : 1;
        }
    }
}

