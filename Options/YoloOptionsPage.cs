using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Microsoft.VisualStudio.Shell;

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// YOLO settings page, corresponding to the IntelliJ version's AgentExtenderConfigurable.
    /// Hosts a WPF control (YoloOptionsControl) inside an ElementHost and persists everything to
    /// YoloSettings (an XML file under LocalApplicationData). The reference design has no
    /// "default agent" concept — settings are: global YOLO toggle, auto-refresh, per-agent
    /// permission rules, and user custom tools.
    /// </summary>
    public class YoloOptionsPage : DialogPage
    {
        private ElementHost? _host;
        private YoloOptionsControl? _control;

        public YoloOptionsPage()
        {
        }

        /// <summary>Builds (once) and returns the WPF-based options UI as an IWin32Window.</summary>
        protected override IWin32Window Window
        {
            get
            {
                if (_host == null)
                {
                    _control = new YoloOptionsControl { Page = this };
                    _host = new ElementHost
                    {
                        Child = _control,
                        Dock = DockStyle.Fill
                    };
                }
                return _host;
            }
        }

        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();
            EnsureControl();
            _control!.LoadFromSettings();
        }

        public override void SaveSettingsToStorage()
        {
            EnsureControl();
            _control!.SaveToSettings();
            base.SaveSettingsToStorage();
        }

        /// <summary>
        /// Enables the Options dialog's Apply button. DialogPage raises SettingsChanged via the
        /// protected OnSettingsChanged method; invoke it reflectively so we don't depend on its
        /// exact signature across VS versions.
        /// </summary>
        public void MarkDirty()
        {
            var method = typeof(DialogPage).GetMethod(
                "OnSettingsChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return;
            try
            {
                var args = method.GetParameters().Length == 0
                    ? null
                    : new object[] { this, EventArgs.Empty };
                method.Invoke(this, args);
            }
            catch
            {
                // Best-effort: Apply may still work on OK even if the event didn't fire.
            }
        }

        private void EnsureControl()
        {
            if (_control == null)
            {
                _ = Window; // forces creation in the getter
            }
        }
    }
}
