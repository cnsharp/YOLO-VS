# YOLO for Visual Studio

A VSIX extension that integrates AI Agents into Visual Studio 2022, providing capabilities similar to the IntelliJ version of YOLO.

> ⚠️ **This README is a stale design draft.** Several details below no longer match the code (e.g. the project file is `Yolo.csproj`, the SDK pin is `17.0.31902.203`, the terminal is a real hand-drawn ConPTY implementation, and `Navigation/` / `CommandValidator.cs` / `IconResolver.cs` have been removed). For the authoritative, current description, see [`AGENTS.md`](./AGENTS.md).

## Features

- **Right-side tool window**: interact with AI Agents
- **Agent dropdown**: supports Claude, Copilot, Gemini, Cursor, etc. (managed via `agents.json`, separated from code)
- **Y (skip permission) / R (resume session) global toggles**: icon geometry and colors reuse the IntelliJ vector assets directly; state is persisted
- **Multi-tab terminal**: each Agent launch opens a new tab, so multiple Agent sessions can run concurrently; tabs have a close button
- **Detection cache**: the installed-Agent set is persisted and only re-scanned in the background when the set changes
- **Clickable links**: file paths in terminal output are clickable to navigate
- **File navigation**: clicking a link opens the file and jumps to the specified line

## Tech stack

- **Extension skeleton**: `Microsoft.VisualStudio.SDK` 17.0.31902.203 (`AsyncPackage`)
- **UI framework**: WPF (tool window content)
- **Target framework**: `net472` (maximizes compatibility with the VS Shell / COM interop)
- **Debug target**: Visual Studio 2026 (v18), debugged via the VSIX experimental instance with F5
  - `source.extension.vsixmanifest`'s `InstallationTarget` is `[17.0,19.0)`, covering both VS2022 and VS2026.
  - When you open the project in VS2026 and press F5, it launches the VS2026 experimental instance and loads this extension (the v17-referenced assemblies resolve against the v18 runtime through VS's binding redirects).

## Directory structure

```
yolo-vs/
  Yolo.csproj                 # SDK-style project file (net472)
  Yolo.sln                    # Solution
  Yolo.vsct                   # Command table: View menu + Ctrl+W,Y
  source.extension.vsixmanifest  # VSIX manifest (InstallationTarget [17.0,19.0))
  YoloPackage.cs              # AsyncPackage entry point
  YoloToolWindowCommand.cs    # View-menu command (toggles the tool window)
  Constants.cs                # Constant definitions
  agents.json                 # Agent metadata (embedded resource)
  Resources.resx / Resources.Designer.cs  # Localization resources
  Properties/AssemblyInfo.cs

  ToolWindow/
    YoloToolWindowPane.cs     # ToolWindowPane
    YoloPanel.xaml(.cs)       # Panel UI (dropdown + Y/R toggles + multi-tab terminal)

  Terminal/                   # Hand-drawn ConPTY terminal (no WebView2 / HwndHost)
    ConPty.cs                 # CreatePseudoConsole / ResizePseudoConsole / Close P/Invoke
    ConPtyTerminal.cs         # Process + PTY lifecycle, pending command/env buffering
    TerminalEmulator.cs       # VT/ANSI escape-sequence parsing
    TerminalSurface.cs        # Hand-drawn text grid (cell metrics)
    WpfTerminalView.xaml(.cs) # WPF host control
    TerminalPalette.cs / CharWidth.cs / ITerminalView.cs / Logger.cs

  Links/                      # Terminal output hyperlink detection
    YoloLinkPatterns.cs       # Regex patterns
    YoloHyperlink.cs          # Link model (LinkMatch / LinkTarget / LinkKind)
    FileLinkFilter.cs         # File-path links
    StackTraceLinkFilter.cs   # Stack-frame / bare file-name links
    TypeLinkFilter.cs         # Type-name links
    MemberLinkFilter.cs       # Class.member links
    UrlLinkFilter.cs          # URL links
    OutputLinkInterceptor.cs  # Output interceptor entry point

  Agents/
    AgentRegistry.cs           # Loads and queries agents.json
    AgentDetector.cs           # PATH + --version probing
    InstalledAgents.cs        # Installed-set cache + background re-scan
    AgentIconImage.cs          # SVG/PNG icon rendering
    DefaultSkipEnvs.cs        # env-style bypass (e.g. GOOSE_MODE=auto)
    ExecutableNames.cs         # Executable-name normalization

  Options/
    YoloOptionsPage.cs         # DialogPage (Tools > Options > YOLO > Agents)
    YoloOptionsControl.xaml(.cs)
    YoloSettingsDialog.xaml(.cs)
    YoloSettings.cs            # Persistence (XmlSerializer)
    AgentModels.cs             # Settings-page data model

  Resources/                  # Brand tile + icons
    pluginIcon.png / pluginIcon.svg / pluginIcon_dark.svg
    icons/agents/*             # Per-agent icons (from the IDEA version)
    icons/skipY*, resume*      # Y/R toggle icon geometry
```

## Build

```bash
# Build with the .NET CLI (name the csproj explicitly — the repo has several sln/csproj files)
dotnet build Yolo.csproj -c Debug

# Or use Visual Studio
# Open Yolo.sln and press F6 to build
```

## Debug

1. Open `Yolo.sln` (or `Yolo.csproj`) in **Visual Studio 2026**
2. Press **F5** to launch the VS2026 extension experimental instance
3. In the experimental instance, open the tool window via the top-level **YOLO** entry on the **View** menu, or press **Ctrl+W, Y**
   - Open the settings page via `Tools > Options > YOLO` (corresponds to `YoloOptionsPage`)
4. The terminal is a real hand-drawn ConPTY (see `Terminal/`); launching an Agent runs it as a live process inside a new tab.

## Risks and TODO

### Terminal

The hand-drawn ConPTY terminal is implemented (`Terminal/ConPty*` + `TerminalEmulator` + `TerminalSurface` + `WpfTerminalView`); the SPIKE concluded against hosting Windows Terminal's control. Remaining polish:

- HiDPI / font-scaling edge cases
- Selection / copy from the terminal grid

### Link filtering

Terminal output hyperlink detection lives in `Links/` (regex patterns + per-type filters + interceptor). Wiring the painted spans to click navigation is ongoing.

### Phase 2 features

- Full settings page (table, duplicate validation, Validate, icon download)
- Agent detection cache + background re-scan (refresh behaviour)
- Chinese/English localization
- Package the `.vsix` + Marketplace publishing

## Reference docs

- [ARCHITECTURE.md](./ARCHITECTURE.md) - architecture design
- [SPIKE.md](./SPIKE.md) - risk-validation steps
