# AGENTS.md

> For AI agents (CodeBuddy / Claude / Cursor / Copilot, etc.) working in this repository.
> Goal: build a correct mental model without reading every source file, and **avoid the known pitfalls**.
> This file is the **single authoritative entry point**. `ARCHITECTURE.md` / `SPIKE.md` / `README.md` are design drafts, **some of which are outdated**; where they conflict with this file, this file wins (see §8 for what's stale).

---

## 1. What the project is

| Item | Value |
|---|---|
| Shape | A VSIX extension porting the IntelliJ **YOLO (AI Agents Extender)** — branded as **Agent YOLO** — to full Visual Studio |
| Nature | A **cross-platform rewrite** of Kotlin/JVM/Swing → C#/.NET/WPF, not a code lift |
| Assembly / namespace | `Yolo` / `CnSharp.VSIX.Yolo` |
| Target framework | `net472` |
| Debug host | **VS2026 (v18)** Community, F5 launches the experimental instance |
| Install range | `[17.0,19.0)` (covers both VS2022 / VS2026), amd64 + arm64 |

Core feature: a YOLO tool window on the right → pick an Agent → launch it in a **real ConPTY terminal**; the Y (skip permission) / R (resume session) global toggles inject a flag / environment variable at launch time.

---

## 2. Build / debug (commands are copy-paste ready)

```bash
# Build (you MUST name the csproj: the repo root has several sln/slnx/csproj, bare `dotnet build` is ambiguous)
dotnet build Yolo.csproj -c Debug
# Output: bin/Debug/net472/Yolo.vsix
# Baseline requirement: 0 errors, and 0 VSTHRD0xx warnings

# To see the full warning set while diagnosing (incremental builds swallow warnings)
dotnet build Yolo.csproj -c Debug --no-incremental
```

Debug: open `Yolo.sln` in VS2026 → **F5** → launches the experimental instance.

```powershell
# Reset the experimental instance (required after editing .vsct / manifest)
& "C:\Program Files\Microsoft Visual Studio\18\Community\VSSDK\VisualStudioIntegration\Tools\Bin\CreateExpInstance.exe" `
    /Reset /VSInstance=18.0_9602cece /RootSuffix=Exp

# Launch with logging (to diagnose load failures)
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" /rootsuffix Exp /Log
# Log lives in Roaming (NOT Local!):
#   %APPDATA%\Microsoft\VisualStudio\18.0_9602ceceExp\ActivityLog.xml
```

Key paths:

| Purpose | Path |
|---|---|
| VS2026 install | `C:\Program Files\Microsoft Visual Studio\18\Community` |
| Experimental instance | `%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_9602ceceExp` |
| Deployed extension | `...\18.0_9602ceceExp\Extensions\<random dir>\Yolo.dll` + `Yolo.pkgdef` |
| VSCT compiler | `...\VSSDK\VisualStudioIntegration\Tools\Bin\VSCT.exe` |
| Official template (for comparison) | `...\Common7\IDE\ProjectTemplates\CSharp\Extensibility\1033\VSIXProject` |
| Official command template (with vsct) | `...\Common7\IDE\Extensions\Microsoft.Vsix.TemplatesPackage\ItemTemplates\...\CSharpCustomCommandItemTemplate` |

---

## 3. Directory structure (**current reality**, not the draft)

```
Yolo.csproj                        # SDK-style project; note ResourceName on VSCTCompile (see §6.2)
Yolo.sln
source.extension.vsixmanifest      # Assets MUST be a top-level element (see §6.5)
YoloPackage.cs                     # AsyncPackage entry point
YoloToolWindowCommand.cs           # View-menu command (toggles tool window visibility)
Yolo.vsct                          # Command table: View menu + Ctrl+W,Y
Constants.cs                       # Constants
agents.json                        # Agent metadata (embedded resource, separate from code)
Resources.resx / .Designer.cs      # Strings
Resources/pluginIcon.png           # Brand tile 128x128 (from the IDEA version), used by manifest <Icon>/<PreviewImage>
Resources/pluginIcon.svg           # Same, light-mode vector original
Resources/pluginIcon_dark.svg      # Same, dark-mode vector original

ToolWindow/
  YoloToolWindowPane.cs            # ToolWindowPane
  YoloPanel.xaml(.cs)              # Panel UI: dropdown + Y/R toggles + multi-tab terminal

Terminal/                          # Hand-drawn ConPTY terminal (Spike conclusion: do not host the Windows Terminal control)
  ConPty.cs                        # CreatePseudoConsole/ResizePseudoConsole/ClosePseudoConsole P/Invoke
  ConPtyTerminal.cs                # Process + PTY lifecycle, pending command/env buffering
  TerminalEmulator.cs              # VT/ANSI escape sequence parsing
  TerminalSurface.cs               # Hand-drawn text grid (cell metrics)
  WpfTerminalView.xaml(.cs)        # WPF host control
  TerminalPalette.cs / CharWidth.cs / ITerminalView.cs / Logger.cs

Agents/
  AgentRegistry.cs                 # Loads and queries the embedded agents.json (replaced the old KnownAgents)
  AgentDetector.cs                 # PATH + `--version` probing
  InstalledAgents.cs               # Installed-set cache + background re-scan
  AgentIconImage.cs                # SVG/PNG icon rendering
  DefaultSkipEnvs.cs               # env-style bypass (e.g. GOOSE_MODE=auto)
  ExecutableNames.cs               # Executable-name normalization

Options/
  YoloOptionsPage.cs               # DialogPage (Tools > Options > Agent YOLO > Agents)
  YoloOptionsControl.xaml(.cs)     # Settings-page UI
  YoloSettingsDialog.xaml(.cs)     # Custom settings dialog
  YoloSettings.cs                  # Persistence (XmlSerializer)
  AgentModels.cs                   # Settings-page data model

Links/                             # ⚠️ Spare, do NOT delete (user explicitly asked to keep it)
  YoloLinkPatterns.cs / YoloHyperlink.cs / OutputLinkInterceptor.cs
  FileLinkFilter.cs / StackTraceLinkFilter.cs
  TypeLinkFilter.cs / MemberLinkFilter.cs / UrlLinkFilter.cs   # Phase 2

TerminalDemo/                      # Standalone headless test program, excluded from the VSIX build
```

---

## 4. Architecture highlights

- **Package entry**: `YoloPackage : AsyncPackage`, `ProvideAutoLoad(ShellInitialized, BackgroundLoad)`; auto-shows the tool window after init; menu-command registration is delegated to `YoloToolWindowCommand.InitializeAsync`.
- **Tool window**: `YoloToolWindowPane` (`VsDockStyle.Tabbed`, docked next to Solution Explorer) hosts the WPF `YoloPanel`.
- **Terminal**: **one session = one Tab = one `ConPtyTerminal`**. Layering: `ConPty` (P/Invoke) → `TerminalEmulator` (VT parsing) → `TerminalSurface` (hand-drawn) → `WpfTerminalView`. Closing a tab MUST `Dispose` the pty and the read thread, otherwise every open/close leaks a `pwsh.exe`.
- **Size sync**: panel `SizeChanged` → `ResizePseudoConsole`, otherwise the TUI misaligns / ghosts.
- **Agent metadata is data-driven**: `agents.json` is an embedded resource (`LogicalName=CnSharp.VSIX.Yolo.agents.json`), loaded by `AgentRegistry`. **To add a new Agent, edit the JSON — do not edit code.**
- **Y / R global toggles**: sticky, persisted to `YoloSettings`. They affect **launch time only** — append `skipFlag` / `resumeFlag` to the command per current state, and when Y is on also inject an env-style bypass. They do not patch already-running sessions. Default off; never auto-start.
- **Menu / shortcut**: a top-level `Agent YOLO` entry on the View menu + `Ctrl+W, Y` (mirrors VS's own `Ctrl+W, S/E` window shortcuts).

---

## 5. Hard rules to read before changing anything

1. **Do not upgrade to `Microsoft.VisualStudio.SDK` 18.x.** Currently pinned to `17.0.31902.203`. This machine builds against the VS2026 **v18 assemblies**; switching to 18.x NuGet would only create confusion.
2. **Do not delete `<ResourceName>Menus.ctmenu</ResourceName>` on `VSCTCompile` in `Yolo.csproj`.** → §7.1
3. **Do not delete `RootNamespace` / `AssemblyName` / `AssemblyTitle` from `Yolo.csproj`.** `RootNamespace` is load-bearing: the strongly-typed codegen for `Resources.resx` emits `CnSharp.VSIX.Yolo.Resources`; deleting it triggers a pile of CS0234.
4. **Do not delete the `Links/` directory.** The user explicitly said "Links is spare" — even though it is not currently wired into the main flow.
5. **`<Assets>` in `source.extension.vsixmanifest` MUST be a top-level sibling of `<Metadata>`**, not nested inside it.
6. **UI colors MUST follow the VS theme**: use `EnvironmentColors` + `VSColorTheme.GetThemedColor(...)`, write into a named `SolidColorBrush`, bind in XAML with `DynamicResource`, and refresh on `VSColorTheme.ThemeChanged`.
   **The only exception**: the ConPTY terminal surface itself must keep fixed console colors (ANSI colors are independent of the WPF theme).
7. **Threading**: before touching COM such as `DTE` / `IVsWindowFrame` / `IVsUIShell` / `Events`, you MUST `ThreadHelper.ThrowIfNotOnUIThread()`. Command registration (`AddCommand`) must also run on the UI thread. The build must have **0 VSTHRD0xx warnings**.
8. **Path conventions**: use forward slashes `/` in Bash commands in the repo; pass backslashes `\` to native Windows tools (VSCT.exe, CreateExpInstance.exe), otherwise you get "The given path's format is not supported".
9. **A priority command target must answer `OLECMDERR_E_NOTSUPPORTED`, never `E_NOTIMPL`.** → §6.8

---

## 6. Known pitfalls

### 6.1 VSIX menu command does not show — 99% it's `ResourceName`, not placement

The CTO is embedded as a **named entry** inside the `_EmptyResource.resources` blob. Without `ResourceName`, the SDK names it after the **.vsct filename** (i.e. `Yolo.CTMENU`), while `[ProvideMenuResource("Menus.ctmenu", 1)]` makes the registry look up `Menus.ctmenu` — **names don't match, VS silently shows nothing, no error, no log**.

The misleading part: **the package still loads and the tool window still pops up** (that path doesn't go through the command table), so it looks like a wrong menu placement. Signature: switching any `Parent` (`IDG_VS_WNDO_OTRWNDWS1` / `IDG_VS_VIEW_WINDOWS` / `IDM_VS_MENU_VIEW` / `IDM_VS_MENU_TOOLS`) fails **identically**.

> Note: `Microsoft.VSSDK.BuildTools` defines `<DefaultVSCTResourceName>Menus.ctmenu</DefaultVSCTResourceName>` in `build/Microsoft.VSSDK.BuildTools.props`, but **nothing in the whole package consumes it** — that default has never taken effect, so it must be written explicitly.

**How to verify it took effect** (don't use grep, see §6.3): parse the CLI metadata, enumerate `ManifestResource`, open `_EmptyResource.resources` with `ResourceReader`, and confirm an entry named `Menus.ctmenu` exists. A ~40-line `System.Reflection.Metadata` program does it.

### 6.2 Edited `.vsct` but it doesn't take effect

VS builds the command table from the registry **at startup**. Sequence: edit `.vsct` → **reset the experimental instance** (§2 command) → F5 again.

### 6.3 Don't `grep` binary files

`grep -a -o` on binaries in this machine's Git Bash **crashes outright** (exit `-1073741819`, access violation). Use a PowerShell byte loop, or write a small parser.

### 6.4 Diagnosis methodology: read authoritative data, don't infer from byte counts

Resource names live in the `#Strings` heap and inside `.resources` blobs (mostly UTF-16); counting ASCII bytes yields **misleading, non-comparable numbers**. For build artifacts (PE/CLI metadata, command tables, VSIX contents) **write a small tool that parses the real format**.

Similarly, before concluding "the SDK doesn't support X", **search the whole package**: once I grepped only `tools/vssdk/*.targets` and wrongly concluded an MSBuild metadata item was unused — the answer was one directory over in `build/*.props`.

### 6.5 `ActivityLog.xml` is in Roaming, and needs `/Log`

It is not generated by default; launching without `/Log` shows a **weeks-old stale log**, which you might mistake for a clue. Path in §2.

### 6.6 Experimental-instance reset MUST include the hash suffix

`/VSInstance=18.0_9602cece`. Writing `18.0` **silently creates a useless `18.0Exp` directory** and resets nothing.

### 6.7 `.vsct` top-level element order is XSD-constrained: `<Symbols>` MUST come before `<Commands>`

The order is fixed by `%VSINSTALLDIR%\Xml\Schemas\VSCT.xsd`:

```
[Extern | Include | Define | Symbols]*   Commands   [CommandPlacements | VisibilityConstraints | KeyBindings | UsedCommands]*
```

Putting `<Symbols>` last (which **Microsoft's own item template `VSPackage.vsct` does**, and most .vsct files online copy) **compiles fine** but the VS XML editor reports an XSD validation error:

> The element 'CommandTable' … has invalid child element 'Symbols' … List of possible elements expected: 'CommandPlacements, VisibilityConstraints, KeyBindings, UsedCommands'

So this is **editor-error / compiler-ok**. This repo's `Yolo.vsct` is already correctly ordered per the XSD (`Extern → Symbols → Commands → KeyBindings`); don't change it back.

### 6.8 Priority command target: "not handled" MUST be `OLECMDERR_E_NOTSUPPORTED`

`YoloToolWindowPane` implements `IOleCommandTarget` and registers via `SVsRegisterPriorityCommandTarget` (so the ConPTY terminal can take Ctrl+C/H/E/A/Z/Y/F/Del/Ctrl+V ahead of VS's editor bindings). A priority target sits **in front of the shell's whole command routing chain**, so **every IDE command is routed through it first** — not just terminal keys.

- Commands it does not handle MUST return `Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED`.
- **Never return `VSConstants.E_NOTIMPL`** (or `E_FAIL` / `S_FALSE`). Docs (`command-implementation.md`) are explicit: *"If you fail to pass the command on (usually by returning OLECMDERR_E_NOTSUPPORTED), Visual Studio may stop working properly."* `E_NOTIMPL` (0x80004001) is surfaced to the user as `The operation could not be completed. 尚未实现` instead of continuing routing.

**Incident:** commit `64adbae` ("forward VS editor commands to the focused terminal", shipped as **v1.0.3**) returned `E_NOTIMPL`, so `扩展 ▸ 管理扩展` **and** closing VS **and** anything else in the chain all failed with 尚未实现 — VS could not even exit. Fixed in **v1.1.1**.

**Signature that points here immediately:** several *unrelated* commands fail with the same error while `YoloPackage` loads fine and `ActivityLog.xml` has **0 errors** (routing-layer failures are not logged).

> Gotcha: this repo has its own `Constants` (`Constants.cs`), so the SDK one must be fully qualified as `Microsoft.VisualStudio.OLE.Interop.Constants`.

---

## 7. VSSDK v18 API breaks (verified on this machine)

This machine's VS2026 uses the v18 build target and resolves to **v18 assemblies** at compile time. The following v17 members have been removed:

| Removed | Replacement |
|---|---|
| `DialogPage.CreateControlCore()` | Use the default property grid; public properties auto-render |
| `DialogPage.OnDeactivated(CancelEventArgs)` | `SaveSettingsToStorage` / `LoadSettingsFromStorage` |
| `ToolWindowPane.OnVisibilityChanged(object, EventArgs)` | — |
| `VsShellUtilities.OpenDocumentAsync(...)` | Synchronous `VsShellUtilities.OpenDocument(..., out IVsTextView)` (returns `void`, last `out` is `IVsTextView` not `IVsTextBuffer`) |
| `VSConstants.LOGVIEWID.TextView` | Flat static field `VSConstants.LOGVIEWID_TextView` |

---

## 8. Outdated content in the old docs (don't follow these)

| What the old docs say | Actual state |
|---|---|
| `YoloVS.csproj` / `YoloVS.sln` | Actually `Yolo.csproj` / `Yolo.sln` |
| `Microsoft.VisualStudio.SDK` 17.14.x | Actually `17.0.31902.203` |
| "Terminal is a `TextBox` placeholder" (README risk section) | **A real hand-drawn ConPTY terminal is implemented**, see `Terminal/` |
| `Terminal/YoloTerminalHost.cs` | Does not exist; actually `Terminal/ConPtyTerminal.cs` |
| `Navigation/YoloNavigation.cs`, `Navigation/ProjectTypesSnapshot.cs` | **Deleted** |
| `Agents/CommandValidator.cs`, `Agents/IconResolver.cs`, `Agents/KnownAgents.cs` | **Deleted** (`KnownAgents` replaced by `AgentRegistry`) |
| "View > Other Windows > YOLO" | Now a **top-level View-menu** entry + `Ctrl+W, Y` |
| SPIKE.md go/no-go todo | Spike is done; terminal pick: **hand-drawn ConPTY** (route 2), not hosting the Windows Terminal control |

---

## 9. IntelliJ → VS module map (condensed from ARCHITECTURE.md)

> ⚠️ **The porting upstream is on this machine**: the IntelliJ source is at **`Y:\Projects\IdeaProjects\yolo`**
> (`Y:` is a share mount of the Mac home dir and may be disconnected). For any task "port X from the IDEA version", **look there first**,
> or download from https://github.com/cnsharp/yolo
> Don't rewrite from scratch, and don't ask the user "where is the source".
>
> | Upstream path | Content |
> |---|---|
> | `src/main/kotlin/com/cnsharp/yolo/` | Kotlin source (`settings/` `launcher/` `terminal/` `util/`) |
> | `src/main/resources/agents.json` | Agent metadata → this repo's `agents.json` |
> | `src/main/resources/META-INF/pluginIcon*.{png,svg}` | Brand tile icons → this repo's `Resources/pluginIcon.*` |
> | `src/main/resources/icons/` | `agents/*`, `skipY*`, `resume*` → this repo's `Resources/icons/` |
> | `src/main/resources/messages/YoloBundle*.properties` | en/zh strings → this repo's `Resources.resx` |

| IntelliJ source file | VS counterpart |
|---|---|
| `panel/YoloToolWindowFactory.kt` | `ToolWindow/YoloToolWindowPane.cs` + `YoloPanel.xaml` |
| `terminal/YoloJediTermWidget.kt` | `Terminal/ConPtyTerminal.cs` + `WpfTerminalView` + `TerminalSurface` + `TerminalEmulator` |
| `terminal/YoloColorPalette.kt` | `Terminal/TerminalPalette.cs` |
| `panel/YoloLinkPatterns.kt` (regex only) | `Links/YoloLinkPatterns.cs` (direct translation) |
| `panel/FileLinkFilter.kt` / `StackTraceLinkFilter.kt` | `Links/FileLinkFilter.cs` / `StackTraceLinkFilter.cs` |
| `settings/AgentDetector.kt` | `Agents/AgentDetector.cs` |
| `settings/InstalledAgents.kt` | `Agents/InstalledAgents.cs` |
| `settings/AgentExtenderConfigurable.kt` | `Options/YoloOptionsPage.cs` |
| `settings/AgentExtenderSettings.kt` | `Options/YoloSettings.cs` |
| `YoloBundle.kt` / `YoloConstants.kt` | `Resources.resx` / `Constants.cs` |

**Directly portable (pure logic)**: `YoloLinkPatterns`, `AgentRegistry`, `DefaultSkipEnvs`, `ExecutableNames`, detection strategy.
**Must be rewritten (depends on VS API)**: terminal, tool window, settings page, navigation.

---

## 10. Recommended change workflow

1. Read §5 (hard rules) and §6 (known pitfalls) of this file.
2. `dotnet build Yolo.csproj -c Debug --no-incremental` to confirm the baseline: 0 errors, 0 VSTHRD.
3. Change code.
4. Rebuild; **if you touched `.vsct` or the manifest, reset the experimental instance before F5**.
5. Before submitting, run a full build once more to confirm no new VSTHRD warnings were introduced.
