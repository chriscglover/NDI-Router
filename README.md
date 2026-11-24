<img src="assets/NdiRouter-256.png" width="96" align="right" alt="" />

# NDI Router (SW-P-08 / TSL)

A Windows NDI router with a router control panel and a live preview monitor, plus:

- **Pro-Bel SW-P-08 control over TCP**, so any broadcast control system or panel can route it
  like a hardware router.
- **TSL UMD output** (3.1, 4.0 or 5.0, UDP or TCP), so a multiviewer can show what each output
  is carrying.

Each output is an NDI *routing* source. A receiver that watches it is switched between NDI
sources by the NDI runtime itself, with no decode or re-encode and no extra latency.

## Download

Get the latest build from the [Releases](https://github.com/chriscglover/NDI-Router/releases) page:

| Asset | Use this if |
|---|---|
| `NdiRouter-win-x64-standalone.exe` | You want to just run it. Needs nothing installed apart from NDI - the .NET runtime is bundled. |
| `NdiRouter-win-x64.exe` | You already have the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) and want a smaller download. |

Each is a single `.exe`: no installer, nothing else to unpack. `SHA256SUMS.txt` has their checksums.
Windows SmartScreen may warn on first run, because they are unsigned.

The version is shown in the title bar and beside the name at the top of the window. It is set in
one place, [Directory.Build.props](Directory.Build.props), which every project shares.

## Requirements

- Windows 10/11 x64.
- The NDI 6 (or 5) runtime, from [ndi.tv](https://ndi.tv). The application finds it via
  `NDI_RUNTIME_DIR_V6` (or `_V5`). Without it the application still runs and still answers
  SW-P-08 and TSL, but publishes nothing on the network and has no preview. The status lights and
  log say which applies.
- To build: the .NET 10 SDK.

## Build and run

```powershell
dotnet build
dotnet run --project src/NdiRouter
dotnet test            # 100 tests; three use the real NDI runtime when it is installed
```

The GUI tests run headless and never touch the network or the saved settings. Set
`NDIROUTER_SCREENSHOTS` to a folder and they also save a screenshot of every tab, for checking the
layout by eye.

To produce the two release executables:

```powershell
# standalone - no .NET needed on the target machine
dotnet publish src/NdiRouter -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o <folder>

# framework-dependent - needs the .NET 10 Desktop Runtime
dotnet publish src/NdiRouter -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o <folder>
```

Each leaves one `NdiRouter.exe`. The property is `IncludeNativeLibrariesForSelfExtract`; MSBuild
silently ignores a misspelling, and then the window library's native DLLs (Skia, HarfBuzz, ANGLE)
are left loose beside the executable.

The window uses the Avalonia UI library (Fluent theme, dark) and CommunityToolkit.Mvvm. The router
itself - NDI, SW-P-08, TSL and settings, in `NdiRouter.Core` - uses no packages at all.

## The router

Along the top of the window are three status lights: NDI (runtime and sources found), SW-P-08
(listening and controllers connected) and TSL (sending). The log runs along the bottom; drag the
divider to resize it.

The **Router** tab has the sources on the left, the destinations on the right, and the preview
monitor with **Take** and **Undo** between them. Presets run along the bottom.

- **Route:** click a source to pick it (yellow edge), click a destination to select it, then press
  **Take**. Either can be clicked first. Ctrl+click selects several destinations, which are then
  routed together. The line above Take says what it will do. Clicking the picked source again
  un-picks it.
- **Double-click a destination** to take the picked source straight to it (and to any other
  destinations already selected with Ctrl). With no source picked, a double-click just selects.
- **Keys** (Router tab only, so they never interfere with typing in the tables): **Enter** takes,
  **Ctrl+Z** undoes, **Esc** un-picks the source.
- **Tally:** with several destinations selected, a source fed to all of them is filled red, and one
  fed to only some has a dashed red edge.
- **Undo** puts back the routes the last Take, Clear or preset recall changed. It remembers the
  last 50, and its tooltip and the line under it say what the next Undo will put back.
- **Clear destination** takes the selected destinations off any source.
- **Presets:** press *Store preset*, then a preset, to save every destination's route in it. Click
  a preset to recall it.
- **Right-click** any button to set it up: assign an NDI source to an input (sources are listed
  by machine), rename, set the SW-P-08 names, change the NDI output name or UMD address, or style
  the button (background and text colour, size, bold/italic/underline).

Inputs whose source is not on the network are dimmed and marked *offline*. They can still be
routed, and the NDI runtime connects as soon as the source appears. Each output shows the input
it carries and how many NDI receivers are watching it.

Re-assigning an input's NDI source re-routes every output on that input, as re-plugging a
hardware router's input would.

### Preview

Clicking a source shows that source on the monitor. Clicking a destination shows whatever is
routed to it, and keeps following it: when the destination is re-routed, from the panel, a preset
or a SW-P-08 controller, the monitor switches with it. A green **PVW** badge marks the button on
the monitor. Under the picture are its size, frame rate and NDI source, or why there is no
picture (no source assigned, nothing routed, no video).

The monitor watches the NDI source directly, through one receiver named *NDI Router Preview*.
It uses the source's low-bandwidth stream, so previewing costs much less network than a
full-quality connection. It never connects to the router's own outputs, so it does not add to
their receiver counts.

### Inputs, Outputs and Setup

The **Inputs** and **Outputs** tabs show the same router as editable tables, which is quicker for
setting up a large one. An edit takes effect when you leave the field, not on every keystroke, so
typing a source name does not re-route the outputs on that input letter by letter. The NDI source
box suggests the sources on the network as you type. The Outputs table also previews each
output's UMD text.

The **Setup** tab sets the number of inputs (up to 1000), outputs (up to 256) and presets, the NDI
discovery groups and extra IPs, and the groups outputs are published in.

### Importing a layout

**Import from NDI Tools...** (top right, or on the Setup tab) reads the layout saved by the NDI
Tools router (`%LOCALAPPDATA%\NDI\NDI_Router\Application.NDI.Router.json`): inputs with their labels and
sources, outputs with their labels, routing names and current routes, button styles and preset
names. Preset contents are not in that file. Imported outputs keep their NDI names, so the import
warns if the application that saved them is still running and publishing the same names. New
outputs created here are named `Router Out n`.

## SW-P-08 control

Listens on TCP port **8910** by default, on all interfaces. Each TCP connection is an independent
SW-P-08 link. The protocol is described in the
[Router Technical Reference Guide](https://community.grassvalley.com/support/s/productmanualmaster/a2NPo000002lwz0MAA/router-technical-reference-guide)
(SW-P-88).

| Wire | Router |
|---|---|
| Destination 0 | Output 1 |
| Source 0 | Input 1 |
| Source 1023 (*clear source*, configurable) | No source: connecting it clears the output, and a cleared output reports it |
| Matrix 0 (configurable) | This router; other matrices are ACKed and ignored |
| Any level (configurable) | NDI carries video, audio and metadata together, so by default every level addresses the one real level |

The settings tab shows the same numbers 1-based, as controllers display them.

**Implemented:**

| Command | | Reply |
|---|---|---|
| 0x01 / 0x81 | Crosspoint interrogate (normal / extended) | 0x03 / 0x83 tally |
| 0x02 / 0x82 | Crosspoint connect | 0x04 / 0x84 connected, to **every** controller |
| 0x15 | Crosspoint tally dump | 0x16 (byte) while every destination and source is 191 or less, as SW-P-88 8.2.3 requires; otherwise 0x17 (word). At most 64 per message, 133 bytes |
| 0x95 | Extended tally dump | 0x97 |
| 0x64 / 0x66 / 0xE4 / 0xE6 | Source / destination names, 4, 8 or 12 characters | 0x6A / 0x6B / 0xEA / 0xEB |
| 0x61 | Protocol implementation | 0x62 |
| 0x08 | Dual controller status | 0x09 `02 00`: master, **active**, idle controller OK |
| (sent) | NAMES UPDATED | 0x1E, 5 s after the last name edit, so controllers re-read names |

A route change from *anywhere* (another controller, the panel, a preset, the outputs table) is
announced with CROSSPOINT CONNECTED on every link, so every controller and panel stays in step.
A controller that uses an extended command is answered with extended messages from then on.

A controller that sends PROTOCOL IMPLEMENTATION (0x61) on connecting is answered with 0x62, listing
every command above. Controllers that wait for that reply before asking for names and tallies then
go on to read them.

**Link layer:** every well-formed message is ACKed at once and a corrupt one NAKed. Messages to
a controller go one at a time, each waiting up to the ACK timeout (1 s) and resent on NAK or
timeout. As SW-P-88 7.2.2 has a Pro-Bel controller do, each is re-sent up to 5 times (6 sends;
configurable). Some IP control systems never send ACKs. A controller that has not ACKed
anything after three timeouts is then sent to without waiting, so its link never stalls, and the
connected-controllers list marks it.

The listener holds its port exclusively, so a second copy of the application, or anything else
already on 8910, fails loudly instead of silently sharing the port.

**Names.** Name commands are not in SW-P-88 Issue 4b: they are SW-P-08 extensions (SW-P-02 has
none), implemented here as deployed routers and controllers use them. Controllers ask for names at
one length, 4, 8 or 12 characters, and show that length on their buttons. So every input and
output has three names, each generated from its label unless one is typed in:

| Label | 4 | 8 | 12 |
|---|---|---|---|
| Cam 1 | `Cam1` | `Cam 1` | `Cam 1` |
| Studio Camera 1 | `SCm1` | `StdCmr1` | `Std Camera 1` |
| Full Screen Cam | `FSCm` | `FllScrCm` | `Fll Scrn Cam` |

Generation closes up spaces first. Then it drops a word's vowels, longest word first. Then it
shortens words from the end. It never drops a digit, nor runs two numbers together. A generated
name follows the label when the label changes; a typed one stays as typed.

On the Inputs and Outputs tabs, generated names are grey. Type over one to fix it; clear it, or type
what would have been generated, to go back to generating it. *Generate SW-P-08 names from label*
(toolbar or right-click; selected rows or all) puts typed names back to generated. On the panel,
right-click a button for *SW-P-08 names...*. Names are printable ASCII and space padded.

Some controllers also ask for 16 or 32 character names (length codes 3 and 4). These are answered
with the label fitted to that length; nothing is stored or edited for those lengths.

**Not implemented:** protect, salvos (connect-on-go), single-name requests (0x65/0x67), UMD label
requests (0x68) and SW-P-02. Serial (RS-422) links are not supported, only TCP.

## TSL UMD output

Each output has a UMD address (by default output *n* is address *n*). The display shows what the
output is carrying, using the **Routed** template, or the **Cleared** template when nothing is
routed. The protocol is described in
[TSL UMD Protocol](https://tslproducts.com/wp-content/uploads/TSL-UMD-protocol.pdf).

| Token | Value |
|---|---|
| `{input.label}` | The routed input's label |
| `{input.name4}` `{input.name8}` `{input.name12}` | Its 4, 8 or 12 character SW-P-08 name |
| `{input.ndi}` `{input.machine}` `{input.channel}` | Its NDI source, whole or split into `MACHINE` and `Source` |
| `{input.number}` | Its input number |
| `{output.label}` `{output.name4}`... `{output.ndi}` `{output.number}` | The output itself |
| `{addr}` `{addr:000}` | The UMD address, optionally zero padded |

A token can list alternatives, `{input.name12|input.ndi}`, and the first that resolves wins. A
quoted alternative is literal text: `{input.label|"NO SOURCE"}`.

TSL 3.1 and 4.0 carry 16 characters. Longer text is truncated, cut to its end, or squeezed by
dropping vowels (digits are always kept). Lamps can be driven from whether the output is routed.

A changed display is sent at once. Every display is also refreshed on the repeat interval, with
packets spaced evenly rather than sent in a burst, because some TCP receivers read only one
message per read and would otherwise drop all but the first. A destination that is not listening
yet is retried every 5 seconds.

## Settings

`NdiRouter.exe --config <file>` runs with a settings file of its own instead, for a second router
on the same machine (give it different output names and a different SW-P-08 port) or for trying a
new build without touching the everyday settings.

Everything, including the current routes, is saved to `%APPDATA%\NdiRouter\config.json` as it
changes and on exit, and restored at start-up. Outputs come back on the network carrying what they
carried before. The file is written to a temporary file and then swapped in, so a crash mid-save
cannot corrupt it. A file that cannot be read is kept aside as `config.json.unreadable` rather than
overwritten.

## Source layout

| Path | Contents |
|---|---|
| [src/NdiRouter.Core/Ndi](src/NdiRouter.Core/Ndi) | NDI runtime loading, P/Invoke, routing outputs, source discovery, the preview receiver |
| [src/NdiRouter.Core/Core](src/NdiRouter.Core/Core) | The router model and state (`RouterCore`), SW-P-08 names, and `RouterHost` which runs everything |
| [src/NdiRouter.Core/Swp08](src/NdiRouter.Core/Swp08) | SW-P-08 framing, messages and the TCP server |
| [src/NdiRouter.Core/Tsl](src/NdiRouter.Core/Tsl), [Net](src/NdiRouter.Core/Net), [Umd](src/NdiRouter.Core/Umd) | TSL packets and transports, UMD templates and the paced sender |
| [src/NdiRouter.Core/Config](src/NdiRouter.Core/Config) | Saved settings and the layout importer |
| [src/NdiRouter](src/NdiRouter) | The window ([MainWindow.axaml](src/NdiRouter/MainWindow.axaml)), its view models and dialogs |
| [tests/NdiRouter.Tests](tests/NdiRouter.Tests) | Router, SW-P-08, TSL, names and NDI tests |
| [tests/NdiRouter.Gui.Tests](tests/NdiRouter.Gui.Tests) | Headless window tests |
| [tools/IconGenerator](tools/IconGenerator) | Draws [assets/NdiRouter.ico](assets) (16-256 px): `dotnet run --project tools/IconGenerator -- assets` |

## Testing

- The SW-P-08 framing and crosspoint encoding are tested byte for byte against the worked examples
  in the SW-P-88 Issue 4b specification (Appendix 1). The server is tested over real TCP sockets.
- The NDI tests publish test sources, route between them, and confirm that a receiver watching a
  router output receives whichever is routed, without reconnecting. They also confirm that the
  preview receiver shows a source's picture and follows a change of source.
- The window tests cover picking and taking, double-click take, the keyboard shortcuts, Undo, and
  what the preview monitor is pointed at.

Checked against SW-P-88 Issue 4b itself:

- The 0x62 reply's layout, *(count sent, count accepted, sent list, accepted list)*, is the one in
  Appendix 1, 10.1; that example is reproduced byte for byte in the tests.
- The byte-form tally dump is limited to 191, and re-sends to 5, as the document says.
- The 0x09 dual-controller status: the document (11.5.1) says only that bit 1 of byte 1 tells active
  from idle. Which value means active, and the second byte, come from SW-P-02 Issue 37's definition
  of the same message: bit 1 set = active. The router answers `02 00` (master, active).

## References

- NDI SDK and runtime (the NDI API components this application uses): [ndi.tv](https://ndi.tv)
- SW-P-08:
  [Router Technical Reference Guide](https://community.grassvalley.com/support/s/productmanualmaster/a2NPo000002lwz0MAA/router-technical-reference-guide)
- TSL UMD: [TSL UMD Protocol](https://tslproducts.com/wp-content/uploads/TSL-UMD-protocol.pdf)

## Version history

### 1.1.0

The Router tab is rearranged: sources on the left, destinations on the right, and a preview
monitor with Take and Undo in the middle. A source is now picked and then taken, instead of routed
on the click; double-clicking a destination takes straight to it. Undo puts back the last 50
takes, clears and preset recalls. Enter, Ctrl+Z and Esc take, undo and un-pick. The preview
monitor shows a clicked source, or follows whatever a clicked destination carries. The import
button is now *Import from NDI Tools...*.

### 1.0.0

First release: inputs, outputs and presets with NDI routing; SW-P-08 control over TCP (normal and
extended crosspoints, tally dumps, 4, 8 and 12 character names, NAMES UPDATED, protocol
implementation and dual controller status); TSL UMD 3.1, 4.0 and 5.0 output; layout import; the
control window.

## Licence

[MIT](LICENSE)
