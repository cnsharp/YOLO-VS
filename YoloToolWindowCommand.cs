using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Registers and handles the "View ▸ Other Windows ▸ YOLO" command, which toggles the
    /// YOLO tool window (shows it when hidden, hides it when visible). The package auto-shows
    /// the window on startup; this command lets the user hide/reopen it at will.
    /// </summary>
    internal sealed class YoloToolWindowCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid(Constants.YoloCmdSetGuid);

        private readonly AsyncPackage _package;

        private YoloToolWindowCommand(AsyncPackage package, IMenuCommandService commandService)
        {
            _package = package;
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, CommandId)));
        }

        public static YoloToolWindowCommand? Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            // AddCommand must happen on the UI thread. This mirrors the official VS "Custom
            // Command" item template exactly: it switches to the main thread before resolving
            // the service. Resolving IMenuCommandService from a background thread (which is
            // where InitializeAsync runs under AllowsBackgroundLoading) can return null,
            // leaving the command present in the command table but with no handler attached.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as IMenuCommandService;
            if (commandService == null)
            {
                Log.Write("YoloToolWindowCommand: IMenuCommandService unavailable; menu command has no handler.");
                return;
            }
            Instance = new YoloToolWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ToggleToolWindowAsync();
                }
                catch (Exception ex)
                {
                    // Never let a toggle failure fault the package/IDE or become an
                    // unobserved task exception.
                    System.Diagnostics.Trace.TraceError($"YOLO toggle command failed: {ex}");
                }
            });
        }

        private async Task ToggleToolWindowAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Find the existing pane without creating it; if it is already on screen, hide it.
            // IVsWindowFrame.IsVisible() returns S_OK (0) when the frame is visible and S_FALSE (1)
            // when hidden, so compare against S_OK — treating 0 as "not visible" inverts the toggle.
            var existing = await _package.FindToolWindowAsync(typeof(YoloToolWindowPane), 0, false, _package.DisposalToken);
            if (existing?.Frame is IVsWindowFrame frame && frame.IsVisible() == Microsoft.VisualStudio.VSConstants.S_OK)
            {
                frame.Hide();
                return;
            }

            // Otherwise create (if needed) and show/activate it.
            var window = await _package.FindToolWindowAsync(typeof(YoloToolWindowPane), 0, true, _package.DisposalToken);
            if (window?.Frame is IVsWindowFrame showFrame)
                showFrame.Show();
        }
    }
}
