using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

#pragma warning disable VSTHRD001 // Use SwitchToMainThreadAsync instead of Dispatcher (background reader -> UI marshal)
#pragma warning disable VSTHRD110 // Observe awaitable result (fire-and-forget marshal is intentional)

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Connects a <see cref="ConPty"/> (a real Windows pseudo console) to any
    /// <see cref="ITerminalView"/> (WPF or xterm.js). It forwards pty output to the
    /// view and view keystrokes back to the pty, launches an interactive shell
    /// (PowerShell 7 / Windows PowerShell / cmd) and handles resize and disposal.
    /// </summary>
    internal sealed class ConPtyTerminal : IDisposable
    {
        private readonly object _startLock = new object();
        private ITerminalView _view;
        // Volatile so the unsynchronized fast-path reads in OnResized/RunCommand see a
        // published value; all mutations happen under _startLock.
        private volatile ConPty? _pty;
        private string _shellCommand = "cmd.exe";
        private volatile bool _launched;
        private bool _disposed;
        private int _outLogged;

        // Buffer for commands / env writes issued before the pty has actually started
        // (e.g. an agent launched into a freshly created tab). Flushed on first start.
        private readonly List<(string, string)> _pendingEnv = new List<(string, string)>();
        private string? _pendingCommand;

        /// <summary>
        /// Working directory the shell (and therefore every agent launched from it) starts
        /// in. Defaults to the solution/project directory, set by the tool window.
        /// </summary>
        public string? WorkingDirectory { get; set; }

        public ConPtyTerminal(ITerminalView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _view.InputReceived += OnInput;
            _view.Resized += OnResized;
            _shellCommand = DetectShell();
            Log.Write($"ConPtyTerminal ctor: shell='{_shellCommand}' viewType={view.GetType().Name}");
        }

        /// <summary>
        /// Starts the pseudo console and the shell. Safe to call from a background thread;
        /// the view implementation handles UI-thread marshaling. If the terminal view does
        /// not yet report a valid size, the pty start is deferred until a resize arrives
        /// (<see cref="OnResized"/>), so we never permanently bail on a 0-sized
        /// initialization (which used to make the agent TUI never appear).
        /// </summary>
        public void Launch()
        {
            if (_launched) return;
            _launched = true;
            TryStartPty();
        }

        private void TryStartPty()
        {
            // Launch() runs on a background thread while the resize that finally supplies a
            // valid grid size arrives on the UI thread — serialize so only one pty starts.
            lock (_startLock)
            {
                if (_pty != null || _disposed) return;

                // The view may not have been measured yet (e.g. the default shell tab is created
                // before the tool window is laid out), so it reports 0x0. Rather than defer forever
                // — which would leave _pty null and the terminal permanently unable to take input —
                // start with a sensible default grid. OnResized() resizes the live pty to the real
                // grid as soon as the view is measured.
                int cols = _view.Columns;
                int rows = _view.Rows;
                if (cols < 1 || rows < 1)
                {
                    Log.Write($"PTY: view size {cols}x{rows} invalid, starting with fallback 80x24");
                    cols = 80;
                    rows = 24;
                }

                Log.Write($"PTY starting: cols={cols} rows={rows} shell='{_shellCommand}' wd='{WorkingDirectory}'");
                var pty = new ConPty(cols, rows);
                pty.OutputReceived += OnOutput;
                pty.Start(_shellCommand, WorkingDirectory);
                _pty = pty;
                Log.Write("PTY started");
                FlushPending();
            }
        }

        /// <summary>
        /// Writes any env vars / command that were issued before the pty actually started
        /// (e.g. launching an agent into a freshly created tab). Env writes are flushed
        /// before the command so the launched process inherits them.
        /// </summary>
        private void FlushPending()
        {
            // Always called under _startLock (from TryStartPty). Snapshot the buffers so we
            // can write to the pty outside the lock and leave the lists in a clean state.
            if (_pty == null) return;
            var pty = _pty;
            var env = _pendingEnv.ToArray();
            var cmd = _pendingCommand;
            _pendingEnv.Clear();
            _pendingCommand = null;

            foreach (var (name, value) in env)
                pty.Write(Encoding.UTF8.GetBytes(BuildEnvLine(name, value) + "\r\n"));
            if (cmd != null)
            {
                var safe = cmd.Replace("\r", string.Empty).Replace("\n", string.Empty);
                pty.Write(Encoding.UTF8.GetBytes(safe + "\r\n"));
            }
        }

        private void OnOutput(byte[] data)
        {
            if (_disposed) return;
            if (_outLogged < 200)
            {
                _outLogged++;
                // Preview first 48 bytes as printable ASCII so we can see whether an agent
                // actually emitted a TUI (vs. an error/empty). Control chars shown as '.'.
                var preview = new StringBuilder();
                for (int i = 0; i < Math.Min(data.Length, 48); i++)
                {
                    char c = (char)data[i];
                    preview.Append(char.IsControl(c) ? '.' : c);
                }
                Log.WriteVerbose($"PTY output #{_outLogged}: {data.Length} bytes :: {preview}");
            }
            // The view implementation is responsible for marshaling to its UI thread.
            _view.WriteOutput(data);
        }

        private void OnInput(byte[] data)
        {
            _pty?.Write(data);
        }

        private void OnResized()
        {
            if (_pty == null)
            {
                // The pty start was deferred because the view had no valid size at Launch()
                // time. Now that a resize with real dimensions arrived, start it.
                TryStartPty();
                return;
            }
            if (_view.Columns > 0 && _view.Rows > 0)
                _pty.Resize(_view.Columns, _view.Rows);
        }

        /// <summary>
        /// Writes a line into the shell (as a command), so it is rendered by the real
        /// terminal rather than drawn directly over the shell's screen buffer.
        /// </summary>
        public void WriteLine(string text)
        {
            if (_pty == null) return;
            string cmd = text.Replace("\r", string.Empty).Replace("\n", string.Empty);
            _pty.Write(Encoding.UTF8.GetBytes("echo \"" + cmd.Replace("\"", "'") + "\"\r\n"));
        }

        /// <summary>
        /// Sends a raw command into the real shell (no echo wrapper) so it actually
        /// executes. Used to launch the selected agent; its real output then streams
        /// back through the pty into the terminal.
        /// </summary>
        public void RunCommand(string command)
        {
            Log.Write($"RunCommand: ptyStarted={_pty != null} cmd='{command}'");
            TryStartPty();
            bool writeNow;
            lock (_startLock)
            {
                writeNow = _pty != null;
                if (!writeNow) _pendingCommand = command; // buffer until the pty actually starts
            }
            if (writeNow)
            {
                var safe = command.Replace("\r", string.Empty).Replace("\n", string.Empty);
                _pty!.Write(Encoding.UTF8.GetBytes(safe + "\r\n"));
            }
        }

        /// <summary>
        /// Sets an environment variable in the running shell before an agent is launched.
        /// Some agents (e.g. goose, via GOOSE_MODE) bypass permissions through an env var
        /// that must be present when the process starts — it cannot be appended to the
        /// command line. Mirrors the IntelliJ plugin's DefaultSkipEnvs handling.
        /// </summary>
        public void SetEnvVar(string name, string value)
        {
            bool writeNow;
            lock (_startLock)
            {
                writeNow = _pty != null;
                if (!writeNow) _pendingEnv.Add((name, value)); // buffer until the pty actually starts
            }
            if (writeNow)
                _pty!.Write(Encoding.UTF8.GetBytes(BuildEnvLine(name, value) + "\r\n"));
        }

        private string BuildEnvLine(string name, string value)
        {
            if (_shellCommand.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                _shellCommand.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"$env:{name} = \"{value}\"";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return $"set {name}={value}";
            return $"export {name}=\"{value}\"";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _view.InputReceived -= OnInput;
            _view.Resized -= OnResized;
            _pty?.Dispose();
            _pty = null;
        }

        /// <summary>
        /// Re-targets the terminal at a different view (e.g. when the terminal window was
        /// closed and reopened). Re-subscribes events, resizes the running pty to the new
        /// view, or starts the pty if it was waiting for a valid size.
        /// </summary>
        public void AttachView(ITerminalView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (_view != null)
            {
                _view.InputReceived -= OnInput;
                _view.Resized -= OnResized;
            }
            _view = view;
            _view.InputReceived += OnInput;
            _view.Resized += OnResized;
            if (_pty != null && _view.Columns > 0 && _view.Rows > 0)
                _pty.Resize(_view.Columns, _view.Rows);
            else
                TryStartPty();
        }

        /// <summary>
        /// Picks the best available shell: pwsh (PowerShell 7+), then Windows PowerShell,
        /// then cmd. The chosen command prints a short welcome banner on startup.
        /// </summary>
        private static string DetectShell()
        {
            // Force UTF-8 so agents (claude, etc.) emit UTF-8 bytes the terminal decodes
            // correctly. No banner text is printed into the terminal.
            const string encodingSetup =
                "$OutputEncoding=[System.Text.Encoding]::UTF8; " +
                "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; ";

            string? pwsh = FindInPath("pwsh.exe");
            if (pwsh != null)
                return $"\"{pwsh}\" -NoExit -NoLogo -Command \"{encodingSetup}\"";

            string? ps = FindInPath("powershell.exe");
            if (ps != null)
                return $"\"{ps}\" -NoExit -NoLogo -Command \"{encodingSetup}\"";

            string cmd = FindInPath("cmd.exe") ?? "cmd.exe";
            return $"\"{cmd}\" /K \"chcp 65001 >nul\"";
        }

        private static string? FindInPath(string fileName)
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    try
                    {
                        string candidate = Path.Combine(dir, fileName);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch { /* ignore invalid path entries */ }
                }
            }

            // Fallbacks for common fixed locations.
            var fixedDirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", fileName)
            };
            foreach (var candidate in fixedDirs)
            {
                try { if (File.Exists(candidate)) return candidate; }
                catch { /* ignore */ }
            }
            return null;
        }
    }
}
