using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 按应用统计流量：订阅 Microsoft-Windows-Kernel-Network 的 ETW 实时事件。
    /// 每个 TCP/UDP 收发事件都带有所属进程 PID 和字节数，直接累加到 <see cref="AppTrafficLogger"/>。
    ///
    /// 只用原生 advapi32 ETW API（无第三方库），单个后台线程，事件在内核侧缓冲、每秒刷新一次，
    /// 对 CPU/内存影响很小。需要管理员权限（LiteMonitor 本身以管理员运行）。
    /// </summary>
    public static class AppTrafficTracer
    {
        private const string SessionName = "LiteMonitor-AppTraffic";

        // Microsoft-Windows-Kernel-Network {7DD42A49-5329-4832-8DFD-43D979153A88}
        private static Guid _providerId = new("7DD42A49-5329-4832-8DFD-43D979153A88");
        private const ulong KEYWORD_IPV4 = 0x10, KEYWORD_IPV6 = 0x20;

        // 事件 ID → (上传?, IPv6?)
        //   10/11 TCPv4 send/recv, 26/27 TCPv6 send/recv, 42/43 UDPv4 send/recv, 58/59 UDPv6 send/recv
        // 负载布局均以 PID(uint32), size(uint32), daddr, saddr 开头

        private static Thread? _thread;
        private static ulong _sessionHandle;
        private static volatile bool _running;
        private static EventRecordCallback? _callback; // 防止委托被 GC

        // PID → 程序名缓存（仅在 ETW 线程访问，每 60 秒清空一次以应对 PID 复用）
        private static readonly Dictionary<uint, string> _pidCache = new();
        private static long _cacheResetTick;

        public static bool IsRunning => _running;

        public static void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "AppTrafficTracer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public static void Stop()
        {
            var thread = _thread;
            _running = false;
            try
            {
                using var props = new TraceProperties();
                ControlTraceW(0, SessionName, props.Ptr, EVENT_TRACE_CONTROL_STOP); // ProcessTrace 随之返回
            }
            catch { }
            // 等消费线程处理完缓冲区剩余事件（通常立即返回，最多 1 秒），确保随后的 Save() 不漏数据
            thread?.Join(1000);
        }

        private static void Run()
        {
            try
            {
                if (!StartSession()) return;
                _running = true;
                Consume(); // 阻塞直到会话停止
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppTrafficTracer] {ex.Message}");
            }
            finally
            {
                _running = false;
                _thread = null;
            }
        }

        private static bool StartSession()
        {
            using var props = new TraceProperties();
            int rc = StartTraceW(out _sessionHandle, SessionName, props.Ptr);
            if (rc == ERROR_ALREADY_EXISTS)
            {
                // 上次异常退出残留的会话：停止后重建
                using (var p2 = new TraceProperties()) ControlTraceW(0, SessionName, p2.Ptr, EVENT_TRACE_CONTROL_STOP);
                using var p3 = new TraceProperties();
                rc = StartTraceW(out _sessionHandle, SessionName, p3.Ptr);
            }
            if (rc != 0)
            {
                Debug.WriteLine($"[AppTrafficTracer] StartTrace failed: {rc} (需要管理员权限)");
                return false;
            }

            rc = EnableTraceEx2(_sessionHandle, ref _providerId, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE,
                                KEYWORD_IPV4 | KEYWORD_IPV6, 0, 0, IntPtr.Zero);
            if (rc != 0)
            {
                Debug.WriteLine($"[AppTrafficTracer] EnableTraceEx2 failed: {rc}");
                Stop();
                return false;
            }
            return true;
        }

        private static void Consume()
        {
            _callback = OnEvent;
            IntPtr logFile = Marshal.AllocHGlobal(LOGFILE_SIZE);
            IntPtr name = Marshal.StringToHGlobalUni(SessionName);
            try
            {
                for (int i = 0; i < LOGFILE_SIZE; i++) Marshal.WriteByte(logFile, i, 0);
                Marshal.WriteIntPtr(logFile, LOGFILE_LoggerName, name);
                Marshal.WriteInt32(logFile, LOGFILE_ProcessTraceMode, PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD);
                Marshal.WriteIntPtr(logFile, LOGFILE_EventRecordCallback, Marshal.GetFunctionPointerForDelegate(_callback));

                ulong handle = OpenTraceW(logFile);
                if (handle == INVALID_PROCESSTRACE_HANDLE) return;
                try { ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero); }
                finally { CloseTrace(handle); }
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                Marshal.FreeHGlobal(logFile);
            }
        }

        // ============ 事件回调（ETW 线程） ============
        private static void OnEvent(IntPtr rec)
        {
            try
            {
                ushort id = (ushort)Marshal.ReadInt16(rec, REC_EventId);
                bool up, v6;
                switch (id)
                {
                    case 10: case 42: up = true; v6 = false; break;
                    case 11: case 43: up = false; v6 = false; break;
                    case 26: case 58: up = true; v6 = true; break;
                    case 27: case 59: up = false; v6 = true; break;
                    default: return;
                }

                ushort len = (ushort)Marshal.ReadInt16(rec, REC_UserDataLength);
                IntPtr data = Marshal.ReadIntPtr(rec, REC_UserData);
                if (data == IntPtr.Zero || len < (v6 ? 24 : 12)) return;

                uint pid = (uint)Marshal.ReadInt32(data, 0);
                uint size = (uint)Marshal.ReadInt32(data, 4);
                if (size == 0 || IsLoopback(data + 8, v6)) return; // 本机回环流量不计

                AppTrafficLogger.Add(ResolveName(pid), up ? size : 0, up ? 0 : size);
            }
            catch { }
        }

        private static bool IsLoopback(IntPtr addr, bool v6)
        {
            if (!v6) return Marshal.ReadByte(addr) == 127;
            // ::1 或 IPv4 映射回环 ::ffff:127.x.x.x
            long hi = Marshal.ReadInt64(addr), lo = Marshal.ReadInt64(addr, 8);
            if (hi == 0 && lo == 0x0100000000000000) return true;
            return hi == 0 && (lo & 0xFFFFFFFF) == unchecked((long)0xFFFF0000) && Marshal.ReadByte(addr, 12) == 127;
        }

        /// <summary>PID → 程序名 (如 "chrome.exe")；首次解析时顺便记录完整路径供图标使用。</summary>
        private static string ResolveName(uint pid)
        {
            long now = Environment.TickCount64;
            if (now - _cacheResetTick > 60_000) { _pidCache.Clear(); _cacheResetTick = now; }
            if (_pidCache.TryGetValue(pid, out var name)) return name;

            string? path = pid <= 4 ? null : QueryImagePath(pid);
            name = path != null ? Path.GetFileName(path) : "System";
            if (path != null) AppTrafficLogger.SetPath(name, path);
            _pidCache[pid] = name;
            return name;
        }

        private static string? QueryImagePath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        // ============ EVENT_TRACE_PROPERTIES (x64) ============
        private sealed class TraceProperties : IDisposable
        {
            private const int PROPS_SIZE = 120, NAME_BYTES = 1024;
            public readonly IntPtr Ptr;

            public TraceProperties()
            {
                int total = PROPS_SIZE + NAME_BYTES;
                Ptr = Marshal.AllocHGlobal(total);
                for (int i = 0; i < total; i++) Marshal.WriteByte(Ptr, i, 0);
                Marshal.WriteInt32(Ptr, 0, total);                       // Wnode.BufferSize
                Marshal.WriteInt32(Ptr, 40, 1);                          // Wnode.ClientContext = QPC
                Marshal.WriteInt32(Ptr, 44, WNODE_FLAG_TRACED_GUID);     // Wnode.Flags
                Marshal.WriteInt32(Ptr, 48, 16);                         // BufferSize (KB)
                Marshal.WriteInt32(Ptr, 52, 2);                          // MinimumBuffers
                Marshal.WriteInt32(Ptr, 56, 8);                          // MaximumBuffers
                Marshal.WriteInt32(Ptr, 64, EVENT_TRACE_REAL_TIME_MODE); // LogFileMode
                Marshal.WriteInt32(Ptr, 68, 1);                          // FlushTimer = 1s
                Marshal.WriteInt32(Ptr, 116, PROPS_SIZE);                // LoggerNameOffset
            }

            public void Dispose() => Marshal.FreeHGlobal(Ptr);
        }

        // ============ native ============
        private const int ERROR_ALREADY_EXISTS = 183;
        private const int WNODE_FLAG_TRACED_GUID = 0x00020000;
        private const int EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        private const uint EVENT_TRACE_CONTROL_STOP = 1;
        private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        private const byte TRACE_LEVEL_VERBOSE = 5;
        private const int PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
        private const int PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // EVENT_TRACE_LOGFILEW (x64) offsets
        private const int LOGFILE_SIZE = 448, LOGFILE_LoggerName = 8, LOGFILE_ProcessTraceMode = 28, LOGFILE_EventRecordCallback = 424;
        // EVENT_RECORD (x64) offsets
        private const int REC_EventId = 40, REC_UserDataLength = 86, REC_UserData = 96;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EventRecordCallback(IntPtr eventRecord);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int StartTraceW(out ulong handle, string name, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int ControlTraceW(ulong handle, string name, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll")]
        private static extern int EnableTraceEx2(ulong handle, ref Guid providerId, uint controlCode, byte level,
                                                 ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern ulong OpenTraceW(IntPtr logfile);

        [DllImport("advapi32.dll")]
        private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll")]
        private static extern int CloseTrace(ulong handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);
    }
}
