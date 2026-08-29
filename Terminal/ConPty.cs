using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Thin wrapper around the Windows Pseudo Console (ConPTY) API. Hosts a real
    /// shell (e.g. pwsh.exe) and exposes its input/output as byte streams so a WPF
    /// control can render it. No external dependencies.
    /// </summary>
    internal sealed class ConPty : IDisposable
    {
        public event Action<byte[]>? OutputReceived;

        private IntPtr _inRead = IntPtr.Zero;
        private IntPtr _inWrite = IntPtr.Zero;
        private IntPtr _outRead = IntPtr.Zero;
        private IntPtr _outWrite = IntPtr.Zero;
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _hProcess = IntPtr.Zero;
        private IntPtr _hThread = IntPtr.Zero;
        private IntPtr _attrList = IntPtr.Zero;
        private Thread? _reader;
        private volatile bool _running;
        private bool _closed;
        private readonly object _closeLock = new object();

        private const uint STARTF_USESTDHANDLES = 0x00000100;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const int HANDLE_FLAG_INHERIT = 0x00000001;
        private const int ERROR_BROKEN_PIPE = 109;
        private const int ERROR_INVALID_HANDLE = 6;
        private const uint WAIT_OBJECT_0 = 0;

        public ConPty(int cols, int rows)
        {
            if (!CreatePipe(out _inRead, out _inWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException("Failed to create input pipe.");
            if (!CreatePipe(out _outRead, out _outWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException("Failed to create output pipe.");

            // ConPTY connects via the attribute list, so the pipe handles need not be inherited.
            SetHandleInformation(_inRead, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(_inWrite, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(_outRead, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(_outWrite, HANDLE_FLAG_INHERIT, 0);

            var size = new COORD { X = (short)cols, Y = (short)rows };
            int hr = CreatePseudoConsole(size, _inRead, _outWrite, 0, out _hPC);
            if (hr != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
        }

        public void Start(string command, string? workingDirectory = null)
        {
            int attrListSize = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
            _attrList = Marshal.AllocHGlobal(attrListSize);
            if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref attrListSize))
                throw new InvalidOperationException("InitializeProcThreadAttributeList failed.");
            if (!UpdateProcThreadAttribute(_attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("UpdateProcThreadAttribute failed.");

            var si = new STARTUPINFOEX
            {
                cb = Marshal.SizeOf<STARTUPINFOEX>(),
                dwFlags = (int)STARTF_USESTDHANDLES,
                hStdInput = _inWrite,
                hStdOutput = _outRead,
                hStdError = _outRead,
                lpAttributeList = _attrList
            };

            var pi = new PROCESS_INFORMATION();
            if (!CreateProcessW(
                    IntPtr.Zero,
                    command,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero,
                    workingDirectory,
                    ref si,
                    out pi))
            {
                throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
            }

            _hProcess = pi.hProcess;
            _hThread = pi.hThread;

            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPtyReader" };
            _reader.Start();
        }

        private void ReadLoop()
        {
            var buf = new byte[4096];
            while (_running)
            {
                if (!ReadFile(_outRead, buf, buf.Length, out int n, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    // ERROR_BROKEN_PIPE / ERROR_INVALID_HANDLE are the normal ways the read
                    // ends when the pty is torn down; anything else is worth recording.
                    if (_running && err != ERROR_BROKEN_PIPE && err != ERROR_INVALID_HANDLE)
                        Log.Write($"ConPty.ReadLoop: ReadFile failed, win32={err}");
                    break;
                }
                if (n <= 0) break;   // EOF: the shell exited and the pty closed its write end

                var chunk = new byte[n];
                Array.Copy(buf, chunk, n);
                OutputReceived?.Invoke(chunk);
            }
            Log.Write("ConPty.ReadLoop: exited");
        }

        public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

        public void Write(byte[] data) => Write(data, 0, data.Length);

        public void Write(byte[] data, int offset, int count)
        {
            if (_inWrite == IntPtr.Zero)
                return;
            WriteFile(_inWrite, data, count, out _, IntPtr.Zero);
        }

        public void Resize(int cols, int rows)
        {
            if (_hPC == IntPtr.Zero)
                return;
            ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
        }

        /// <summary>
        /// Tears the pseudo console down without leaking the shell process or the reader
        /// thread. Order matters: closing the input pipe first lets the shell exit on its
        /// own (stdin EOF), and <c>ClosePseudoConsole</c> is what actually unblocks the
        /// reader thread sitting in <c>ReadFile</c>. Idempotent.
        /// </summary>
        public void Close()
        {
            lock (_closeLock)
            {
                if (_closed) return;
                _closed = true;
                _running = false;

                // 1. Signal EOF on stdin so the shell can shut down cleanly.
                CloseHandleSafe(ref _inWrite);

                // 2. Give it a moment, then force it if it ignored the EOF.
                if (_hProcess != IntPtr.Zero)
                {
                    if (WaitForSingleObject(_hProcess, 300) != WAIT_OBJECT_0)
                    {
                        try { TerminateProcess(_hProcess, 0); } catch { /* best effort */ }
                        WaitForSingleObject(_hProcess, 300);
                    }
                }

                // 3. Closing the pty releases the output pipe, which makes the pending
                //    ReadFile on the reader thread return so the loop can exit.
                if (_hPC != IntPtr.Zero)
                {
                    ClosePseudoConsole(_hPC);
                    _hPC = IntPtr.Zero;
                }

                // 4. Reap the reader before closing the handle it reads from.
                var reader = _reader;
                _reader = null;
                if (reader != null && reader.IsAlive && !reader.Join(1000))
                    Log.Write("ConPty.Close: reader thread did not exit in 1s");

                OutputReceived = null;

                CloseHandleSafe(ref _hProcess);
                CloseHandleSafe(ref _hThread);
                CloseHandleSafe(ref _inRead);
                CloseHandleSafe(ref _outRead);
                CloseHandleSafe(ref _outWrite);

                if (_attrList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(_attrList);
                    Marshal.FreeHGlobal(_attrList);
                    _attrList = IntPtr.Zero;
                }
                Log.Write("ConPty.Close: done");
            }
        }

        private static void CloseHandleSafe(ref IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            CloseHandle(h);
            h = IntPtr.Zero;
        }

        public void Dispose() => Close();

        #region P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref int lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, int dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            IntPtr lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        #endregion
    }
}
