using System;
using System.Collections;
using ScriptPortal.Vegas;

namespace Subtitler.VegasPlugin
{
    public sealed class Module : ICustomCommandModule
    {
        private ScriptPortal.Vegas.Vegas vegas;
        private readonly CustomCommand command = new CustomCommand(CommandCategory.View, "Eigyn.Subtitler.Panel");
        public void InitializeModule(ScriptPortal.Vegas.Vegas host) { vegas = host; }
        public ICollection GetCustomCommands()
        {
            command.DisplayName = "Subtitler";
            command.Invoked += (sender, args) => Open(vegas, command);
            command.MenuPopup += (sender, args) => command.Checked = IsOpen(vegas);
            return new[] { command };
        }
        public static void Open(ScriptPortal.Vegas.Vegas host, CustomCommand autoLoad)
        {
            if (IsOpen(host)) { host.ActivateDockView("Eigyn.Subtitler"); return; }
            var panel = new SubtitlerPanel(host) { AutoLoadCommand = autoLoad, PersistDockWindowState = true };
            host.LoadDockView(panel);
        }
        private static bool IsOpen(ScriptPortal.Vegas.Vegas host)
        {
            // Build 250 throws instead of returning false for an unknown instance name.
            try { return host.FindDockView("Eigyn.Subtitler"); }
            catch (ApplicationException) { return false; }
        }
    }
}

public sealed class EntryPoint
{
    public void FromVegas(ScriptPortal.Vegas.Vegas vegas)
    {
        Subtitler.VegasPlugin.Module.Open(vegas, null);
    }
}
