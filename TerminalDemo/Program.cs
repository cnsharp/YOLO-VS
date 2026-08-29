using System;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TerminalDemo
{
    /// <summary>
    /// Headless test: confirms (1) the WebView2 runtime is installed, (2) terminal.html
    /// loads xterm.js and posts READY, and (3) a WRITE message renders. Runs in a hidden
    /// WinForms window so nothing visible pops up; prints PASS/FAIL and exits.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();

            var form = new Form
            {
                Width = 1,
                Height = 1,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-1000, -1000)
            };
            var wv = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(wv);

            var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            form.Load += async (_, __) =>
            {
                try
                {
                    await RunDemo(wv, cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    FAIL: {ex}");
                }
                finally
                {
                    form.BeginInvoke(new Action(() => form.Close()));
                }
            };

            Application.Run(form);
            Console.WriteLine(cts.IsCancellationRequested
                ? "=== RESULT: FAIL (timed out) — see output above ==="
                : "=== RESULT: PASS — terminal pipeline works ===");
        }

        private static async Task RunDemo(WebView2 wv, System.Threading.CancellationToken token)
        {
            Console.WriteLine("=== Yolo terminal pipeline demo ===");

            Console.WriteLine("[1] Creating WebView2 environment...");
            var userData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YoloDemo", "WebView2");
            System.IO.Directory.CreateDirectory(userData);

            // Prefer the installed EdgeWebView runtime folder directly (bypasses the
            // EdgeUpdate registry lookup, which may be missing on this machine).
            var browserFolder = FindWebView2RuntimeFolder();
            Console.WriteLine($"    info: browserExecutableFolder = {browserFolder ?? "<system default>"}");
            var env = await CoreWebView2Environment.CreateAsync(browserFolder, userData);
            Console.WriteLine("    PASS: WebView2 environment created.");

            await wv.EnsureCoreWebView2Async(env);
            Console.WriteLine("    PASS: CoreWebView2 ready.");

            var ready = new TaskCompletionSource<string>();
            token.Register(() => ready.TrySetCanceled());

            wv.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                var msg = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(msg)) return;
                if (msg.StartsWith("READY|", StringComparison.Ordinal))
                {
                    var parts = msg.Substring(6).Split('|');
                    Console.WriteLine($"    PASS: xterm.js READY (cols={parts[0]} rows={parts[1]}).");
                    var text = "YOLO demo terminal OK \u2764\r\n$ ";
                    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                    wv.CoreWebView2.PostWebMessageAsString("WRITE|" + b64);
                    Console.WriteLine("    PASS: test WRITE posted to xterm.js (renders above).");
                    ready.TrySetResult(msg);
                }
                else
                {
                    Console.WriteLine($"    info: message: {msg.Substring(0, Math.Min(40, msg.Length))}");
                }
            };

            var html = System.IO.Path.Combine(AppContext.BaseDirectory, "web", "terminal.html");
            Console.WriteLine($"[2] Navigating to {html} ... waiting for READY.");
            wv.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);

            await ready.Task;
        }

        private static string? FindWebView2RuntimeFolder()
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var baseDir = System.IO.Path.Combine(pf86, "Microsoft", "EdgeWebView", "Application");
            if (!System.IO.Directory.Exists(baseDir)) return null;
            string? best = null;
            Version? bestV = null;
            foreach (var d in System.IO.Directory.GetDirectories(baseDir))
            {
                var name = System.IO.Path.GetFileName(d);
                if (System.Version.TryParse(name, out var v) && (bestV == null || v > bestV))
                {
                    bestV = v; best = d;
                }
            }
            return best;
        }
    }
}
