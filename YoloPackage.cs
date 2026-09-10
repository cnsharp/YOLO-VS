using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CnSharp.VSIX.Yolo
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(Constants.PackageGuid)]
    [ProvideToolWindow(typeof(YoloToolWindowPane), Style = VsDockStyle.Tabbed,
                       Window = ToolWindowGuids80.SolutionExplorer)]
    [ProvideOptionPage(typeof(YoloOptionsPage), "Agent YOLO", "Agents", 0, 0, true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Auto-load the package once the VS shell is initialized so the tool window can be shown
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class YoloPackage : AsyncPackage
    {
        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Register the "View ▸ Other Windows ▸ YOLO" command (toggles the tool window).
            await YoloToolWindowCommand.InitializeAsync(this);

            // Defer showing the tool window until after initialization completes.
            // Creating and showing a tool window frame synchronously inside
            // InitializeAsync deadlocks the VS shell (it is still initializing),
            // so we schedule it via RunAsync to run once the package is fully loaded.
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                    var window = await FindToolWindowAsync(typeof(YoloToolWindowPane), 0, true, cancellationToken);
                    if (window?.Frame != null)
                    {
                        ((IVsWindowFrame)window.Frame).Show();
                    }
                }
                catch (Exception ex)
                {
                    // Never let a tool-window failure fault the package/IDE.
                    System.Diagnostics.Trace.TraceError($"Failed to show YOLO tool window: {ex}");
                }
            });
        }
    }
}
