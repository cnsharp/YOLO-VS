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

- **Extension skeleton**: `Microsoft.VisualStudio.SDK` 17.14.x (`AsyncPackage`)
- **UI framework**: WPF (tool window content)
- **Target framework**: `net472` (maximizes compatibility with the VS Shell / COM interop)
- **Debug target**: Visual Studio 2026 (v18), debugged via the VSIX experimental instance with F5
  - `source.extension.vsixmanifest`'s `InstallationTarget` is `[17.0,19.0)`, covering both VS2022 and VS2026.
  - When you open the project in VS2026 and press F5, it launches the VS2026 experimental instance and loads this extension (the v17-referenced assemblies resolve against the v18 runtime through VS's binding redirects).

## Directory structure

```
yolo-vs/
  YoloVS.csproj              # Project file
  source.extension.vsixmanifest  # VSIX manifest
  YoloPackage.cs             # AsyncPackage entry point
  Constants.cs               # Constant definitions
  Resources.resx             # Localization resources

  ToolWindow/
    YoloToolWindowPane.cs    # Tool window panel
    YoloPanel.xaml(.cs)      # Panel UI and code-behind

  Terminal/
    YoloTerminalHost.cs      # Terminal host (ConPTY wrapper)

  Links/
    YoloLinkPatterns.cs      # Regex patterns
    YoloHyperlink.cs         # Link model
    FileLinkFilter.cs        # File-path links
    StackTraceLinkFilter.cs  # Stack-frame links
    OutputLinkInterceptor.cs # Output interceptor

  Navigation/
    YoloNavigation.cs         # File/symbol navigation
    ProjectTypesSnapshot.cs   # Project-type snapshot

  Agents/
    agents.json              # Agent metadata (separated from code, embedded resource)
    AgentRegistry.cs         # Loads and queries agents.json
    InstalledAgents.cs       # Installed Agent list
    AgentDetector.cs         # Agent detection
    CommandValidator.cs      # Command validation
    DefaultSkipEnvs.cs       # Default skip environment variables (from agents.json)
    ExecutableNames.cs       # Executable names
    IconResolver.cs          # Icon resolver (icons from agents.json)

  Options/
    YoloOptionsPage.cs       # Settings page
    YoloSettings.cs          # Persisted settings
```

## Build

```bash
# Build with the .NET CLI
dotnet build

# Or use Visual Studio
# Open YoloVS.sln and press F6 to build
```

## Debug

1. Open `YoloVS.sln` (or `YoloVS.csproj`) in **Visual Studio 2026**
2. Press **F5** to launch the VS2026 extension experimental instance
3. In the experimental instance, open the tool window via the menu `View > Other Windows > YOLO`
   - Open the settings page via `Tools > Options > YOLO` (corresponds to `YoloOptionsPage`)
4. The terminal placeholder area is currently a `TextBox` mock; selecting an Agent triggers a simulated response (the real ConPTY terminal is in the TODO below)

## Risks and TODO

### Highest risk: terminal implementation

The current implementation uses a `TextBox` as a placeholder; the following work remains:

1. **SPIKE Step 2**: validate the ConPTY terminal approach
   - Option 1 (preferred): host Windows Terminal's `TerminalControl`
   - Option 2 (fallback): hand-drawn ConPTY + RichTextBox

2. **Link filtering**: implement `OutputLinkInterceptor` to intercept the terminal output stream
   - Match file paths with regex
   - Generate clickable links
   - Implement file navigation

### Phase 2 features

- Type-name / member links (using `ISymbolSearchService`)
- URL links (open in the system browser)
- Full settings page (table, duplicate validation, Validate, icon download)
- Agent detection cache + background re-scan
- HiDPI / font-scaling polish
- Chinese/English localization
- Package the `.vsix` + Marketplace publishing

## Reference docs

- [ARCHITECTURE.md](./ARCHITECTURE.md) - architecture design
- [SPIKE.md](./SPIKE.md) - risk-validation steps
