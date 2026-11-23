# ScorePlus roadmap

A staged plan for turning the console into a full show-control product: run of show, data-bound
graphics, content, distribution, broadcast, venue control and operations. It is written as a build
order, not a wish list, and each stage is self-contained enough to ship on its own.

## Rules

1. **Every stage ends with something a venue can run a game on.** None of it is infrastructure-only
   from the operator's point of view; each stage ships a usable surface.
2. **Everything is implemented in-house.** Features are described by behaviour. Third-party SDKs are
   integrated through their published interfaces only; no code is copied into the project.
3. **Stages stack.** Later work reuses the earlier plumbing — the event bus, the shared condition
   evaluator, the binding layer, the cue list. Doing them out of order means building the same thing
   twice.
4. **Replace, don't duplicate.** No stage may add a second version of something ScorePlus already
   has. New capability lands inside the existing window, tab, model or file, and the old path is
   retired in the same release, with migration. A second surface that does the same job is a bug,
   not progress.

Stages are named, not versioned. Each gets cut into releases under whatever version is current
(b1.2 today).

| Stage | Theme | Headline | Size |
| --- | --- | --- | --- |
| 0 | Spine | One show spine: events, conditions, timers, schema versions | M |
| 1 | Run of show | Cue list, workspaces, two-operator split | L |
| 2 | Graphics | Data-bound elements, element logic, components, team switch | L |
| 3 | Content | Template gallery, brand kit, content packs | M–L |
| 4 | Data and distribution | Feeds, scheduler, display fleet, in-venue channel | L |
| 5 | Broadcast | Broadcast profiles, key/fill output, dual-channel clip server | L–XL |
| 6 | Venue control | OSC/MIDI/Art-Net/GPIO/webhooks, timecode | L |
| 7 | Operations | Failover, roles/audit, remote monitoring | XL |

## Replacement map — what each stage absorbs

Every row reads as "the new thing becomes the old thing", never "the new thing sits beside it".

| Existing today | Stage | What happens to it |
| --- | --- | --- |
| Event log window | 0 | Gains a **Triggers** page (active rules, timers, last fire). No second diagnostics window. |
| Macros tab + macro step editor | 1 | Becomes the run of show: cue list on top, macro board underneath as the palette. Same steps, same editor, same file. |
| Automation rules (Layout / Media / Macro scopes) | 0, 2 | One evaluator behind all of them. The Layout scope's element actions migrate onto the elements themselves; the scope keeps only show-level rules. |
| Studio element text-source/token picker | 2 | Becomes the Bindings page. The old picker is removed once bindings cover the same sources. |
| Copied-and-pasted element groups across layouts | 2 | Become component instances; existing copies are converted, not left running alongside. |
| Per-layout team colours + team identity dialog | 2 | One show-level identity document; layouts reference it and the dialog edits the document. |
| Studio "new layout" presets, `ScoreboardTemplates`, `ScorebugPresets` | 3 | Become the template gallery's catalog — same files, browsable with previews. The old preset menus go. |
| League Cloud window, ticker source setup, hardware inbound data | 4 | Become source types in the DATA tab; those windows stop owning their own fetch paths. |
| Display picker/selector and multiview display list | 4 | One output registry and fleet view; every selector reads names from it instead of keeping a copy. |
| Ticker rotation window | 4 | Becomes one schedule type in the venue scheduler. |
| Output window's display list + audience display picker | 5 | Key/Fill is another mode on the same output window, not a parallel output app. |
| Media tab clips and replay | 5 | Dual-channel server = second channel selector on the existing `MediaSession`/MEDIA tab. No second clip player. |
| Keybinds, control-surface entries, hardware window | 6 | The device map owns addresses and connections; Keybinds stays a shortcut layer onto the same commands, and existing entries are imported as devices. |
| Autosaves, event log, phone remote | 7 | Failover replicates the existing show file and event log; no second show format, no second remote. |

## Stage 0 — The spine

**Why first.** Nearly every capability below is "something reacts to game data": element logic,
clip triggers, venue cues, data overlays. ScorePlus already reacts — automation rules, macros,
score-triggered videos — but each consumer reads game state on its own schedule and its own timer.
Adding more consumers before unifying that multiplies the bugs.

Ships:

- **A show event stream.** `GameEngine` raises typed events (score, clock start/stop, period,
  possession, penalty, media on air, replay, layout published, custom value changed) with a
  monotonic sequence number, so consumers can order and de-duplicate.
- **One condition evaluator.** The matching logic behind automation rules becomes a service that
  rules, cues, elements and media triggers all call.
- **One timer.** A single tick/waits/timecode service replaces per-feature timers; it will drive the
  cue list and delayed reveals later.
- **Schema versions and migration** for saved games, layouts, themes and future cue lists, so later
  stages can add fields without breaking b1.2 files.

UI changes: nothing new to learn. A **Show health** item in the status bar (last trigger, event
rate, timers running), and a **Triggers** page inside the existing Event log window — not a separate
diagnostics window — listing active rules, timers and their last fire.

Program changes: event surface on `GameEngine`; new Core types (`ShowEvent`, dispatcher, trigger
engine); `AutomationRules`/`AutomationRuntime`/`MacroRunner` subscribe instead of polling;
`MediaSession`/`AudioRouter` publish events; `VideoSurface`/`OutputWindow` report publish and
measured frame rate. Tests for ordering, idempotence and migration; a smoke assertion that a
scripted game produces the expected event order.

Exit: every existing macro and automation behaves identically, driven through the bus, and one test
proves ordered delivery across a simulated game.

## Stage 1 — Run of show

A show is not a pile of buttons, it is an ordered run.

Ships:

- **Cue list per game**: ordered cues made of the macro step vocabulary (`SwitchLayout`, media on
  air, ticker, audio, waits, sport values), with PREV / GO / NEXT, arm/disarm, skip, jump-to-cue,
  cue notes and reordering. Saved in the game file.
- **Two-operator split**: a compact scoring surface (big score/clock/period targets) beside the
  graphics surface; the phone remote gains cue control and a scoring page.
- **Workspace presets**: named tab/section arrangements per operator role ("scorekeeper",
  "graphics", "cues").
- **One-press replay**: capture-last-30-seconds and replay from the media desk as a single action.

UI changes: no fifth tab. The existing **MACROS** tab becomes **RUN OF SHOW** — cue list and
transport at the top, the macro board below as the palette you drag from. A cue is a macro plus
position, notes and arm state, so the editor is the one that already exists. A **transport bar**
under the header shows PREV / GO / NEXT and the next cue's name; Space = GO behind a guard, Esc =
panic to a safe layout. The remote page gets the same cue strip. Preferences → Operators gains named
profiles, a preset per profile and permission toggles (score / graphics / cues).

Program changes: Core `CueList`/`Cue`/`CueRun` models; `MacroDock` gains the cue list above the
board and `MainWindow` keeps its four tabs instead of gaining a dock; `MacroRunner` gains a cue
execution context (hold, stop, resume); saved macros migrate into cues keeping their ids, so
existing games reopen with the same buttons; `Keybinds` additions; permissions in `Theme`;
`RemotePage` endpoints for cues and score with the existing auth and same-origin checks; undo must
understand cue runs (undo the steps, not the cue press).

Exit: a pre-game → halftime → final show runs end to end from the cue list, by one operator, and by
two operators on separate surfaces without fighting over the same state.

## Stage 2 — Graphics that think

The real differentiator is not 3D, it is that the graphic knows things: data-bound, logic-driven,
components, and one team switch.

Ships:

- **Bindings**: any element binds to a game value, custom value, roster stat or feed field, with
  format (prefix/suffix, decimals, ordinal, clock style) and a live sample in the editor.
- **Element logic**: per-element conditions and enter/exit/loop actions, evaluated by the Stage 0
  engine — "third down → pulse", "first down → flash + one-shot animation".
- **Components**: a reusable group of elements with their bindings and logic ("down & distance
  plate"), instanced across layouts; edit once, update everywhere.
- **Team identity switch**: one control swaps colours, wordmarks and logos across every layout and
  component instance, with a preflight diff of what changes.

UI changes: the element inspector's existing source picker becomes the **Bindings** page (same
sources, plus formatting and a live sample) — not a second binding UI. A **Logic** tab in that same
inspector replaces element actions in the Layout automation scope; existing layout rules are
migrated onto the elements they target and the scope stops accepting new element actions. A
**Components** panel drags onto the canvas, with a "convert selection to component" action for
groups already copied around; the canvas badges bound and logic-driven elements. The main window
gets a **TEAM** switch (Home / Away / Neutral identity) with a preview diff; the existing team
identity dialog becomes its editor rather than a per-layout colour picker.

Program changes: `ScorebugLayout` gains binding, logic and component-reference models; a Core
resolve/bind pass feeds `ScorebugRender` instead of scattered token substitution; `ScorebugHtml`
mirrors bindings; `ScorebugCanvasView` inspector extensions; the existing team identity document
becomes shared show data rather than per-layout state. Migration is part of the stage: element-action
rules move onto their elements, the old token path is deleted once bindings cover it, and no element
may be written by both systems at once.

Exit: switching teams updates every layout at once, and a layout carries its own reactive behaviour
with no per-layout rewiring.

## Stage 3 — Content system

The cheapest real value: new users start branded instead of empty.

Ships: a template gallery by sport and slot (full screen, ribbon, scorebug, lower third, sponsor), a
brand kit (colours, fonts, logo, wordmark, safe areas), mock-data preview, an asset library, and
content packs that export/import as one file.

UI changes: the Studio's existing new-layout presets and the `ScoreboardTemplates`/`ScorebugPresets`
catalog become the **CONTENT** gallery — same files, browsable with previews — instead of a library
beside them. Brand kit joins it (and Preferences), replacing the accent-colour-only branding setup.
"New from template" opens the same gallery. The old preset menus and template dialogs are removed in
that release, not left as a shortcut.

Program changes: Core `ContentPack` model, template instantiation into layouts, asset path
resolution, pack validation on import, offscreen thumbnails, and a pack version field tied to Stage
0's schema work. Existing templates are read into packs rather than duplicated, so there is one
catalog and one id space.

Exit: a new operator brands a show and publishes a template-based layout in minutes.

## Stage 4 — Data and distribution

Ships:

- **Feeds**: pluggable providers (manual, HTTP/JSON poll, league cloud, CSV, websocket) mapping into
  game and custom values, with health, last-value and error state.
- **Scheduling**: layout/playlist scheduling for pre-game, halftime, intermission and sponsor
  rotations.
- **Display fleet**: every output in the venue with preview, current content, resolution, last-seen
  health and per-display control.
- **In-venue channel**: a camera feed plus overlays pushed to concourse/suite screens over the
  existing IP/HTTP output path, with its own playlist.

UI changes: a **DATA** tab that absorbs the League Cloud window's feed role, the ticker's source
setup and the hardware window's inbound data — one editor per source type, opened from the same
place. A **Venue** window replaces the separate display picker: fleet grid, schedule calendar,
playlists, and the names the main-window selector and multiview read from. Ticker rotation becomes a
schedule entry. The channel wizard is a page in the Venue window.

Program changes: Core data-source abstraction and HTTP client with retry/backoff; bindings from
Stage 2 consume it; Stage 0 timer drives the scheduler; an output registry with heartbeats from
`OutputWindow`, `MultiviewWindow` and `AudiencePreview`; overlay rendering for the channel;
credentials handled the same way as the league cloud secrets. The league cloud fetch and the
ticker's own poller are retired into the data-source layer, and display names live only in the
registry, so `DisplayPickerWindow`/`DisplaySelector` stop keeping their own copies.

Exit: sponsor rotations and concourse content run unattended beside the score show, with health
visible at a glance.

## Stage 5 — Broadcast

Ships: per-layout broadcast profiles (safe areas, broadcast sizing, lower-third and ticker stacks),
**alpha-aware output** (key/fill, NDI first, SDI via capture hardware only if a venue asks), a
**dual-channel clip server** with internal or external keying, and external surface mapping
(MIDI/control surface, GPIO).

UI changes: broadcast settings (guides, presets, field picker) join the existing layout and output
settings rather than a separate app. **Key/Fill** is another mode on the output window that already
manages audience displays. Channels are a second selector on the existing MEDIA tab — not a second
clip player — and the broadcast panel is the current output list with take/edit, not another window.

Program changes: an output abstraction with NDI and SDI backends; an alpha render path through
`ScorebugRender`/`VideoSurface`; clip-server channels inside `MediaSession`, reusing the existing
clip list and audio routing; timecode/sync hooks. The old single-channel playback path becomes
channel one rather than a legacy branch. Licensing caution: the NDI SDK is free to use but
proprietary, and SDI backends carry their own terms — integrate through their SDK, do not copy
third-party code.

Exit: a keyed scorebug reaches a switcher, and two clips play with fill+key on separate channels.

## Stage 6 — Venue control

Ships: OSC in/out, MIDI notes/CC/timecode, Art-Net/sACN lighting triggers, GPIO relays, HTTP
webhooks and serial — as a **device map** with online state, test/fire buttons and learn mode, plus
**timecode** as a cue master with drift shown.

UI changes: a **Venue Control** window that replaces the ad-hoc control-surface entries in Keybinds
and the hardware window's one-off connection settings — one device list with online state, addresses
and test buttons, and existing entries imported as devices. A **Send** step is a new step kind in
the existing macro editor (`MacroActionKind`), not a second scripting language. The timeline view
extends the run-of-show list rather than adding a separate track editor.

Program changes: device adapters in the desktop app behind a Core `VenueCommand` abstraction; rate
limiting and confirmation for destructive commands; everything logged in the event log; venue
configuration stored per venue, not per game.

Exit: one GO fires graphic + lighting + audio in a demo rig, and an inbound MIDI note can run a cue.

## Stage 7 — Operations at scale

Ships: primary/backup with hot standby and documented failover, role-based operator accounts with
audit, LAN monitoring, versioned backups, and a rehearsal mode that runs cues without publishing.

UI changes: Preferences → Operators/Roles, a failover indicator in the status bar, a monitoring
page, and a Rehearse/Live toggle in the show menu.

Program changes: show-state replication of the existing show file and event log, lease/election
between machines, encrypted LAN transport, audit log — no second show format and no second remote
page.

Exit: pulling the primary machine leaves the backup running, by a procedure that is written down and
tested.

## Cross-cutting work (every stage)

- **Performance budget**: 60 fps audience output on integrated graphics; profile at each stage; no
  blocking IO on the UI thread.
- **Reliability**: preview/backup output path, crash-safe autosave, undo that covers new state
  (cues, bindings, fleet changes).
- **Compatibility**: schema version + migration for game files, layouts, themes, cue lists and
  content packs; b1.2 files must keep opening.
- **Testing**: Core unit tests per new model, UiSmoke assertions for every new dock and window (fits
  without scrolling, named controls, handlers exist), and a simulated full-show smoke run.
- **Docs**: every new surface needs a one-page operator explanation, because a control room cannot
  adopt what it cannot read.
- **No parallel surfaces**: a stage that introduces a control must retire the old one in the same
  release, with a migration path for saved games. Shipping both "for a while" is how a product ends
  up with two scoreboards inside it.
- **Migration tests**: every retired path gets a test that opens a b1.2 file and proves it arrives
  in the new model, not that it silently falls back to the old one.

## If the plan has to be cut

- Two stages only: **0 + 1**. The cue list is the single biggest operator win.
- Three: **+ 2**. Bindings and the team switch make every later graphic cheap.
- Then **3** (content), then **5** (broadcast, the differentiator for venues that stream), then
  **4**, **6**, **7**.
- Never start **5** before **2**, and never start **6** before **1** — otherwise there is nothing
  for devices and timecode to drive.
- If a replacement cannot land with its stage, the stage slips. Shipping the new surface beside the
  old one is the one outcome this plan rules out.

## Terms used above

- **Compositor** — a stack of layers drawn into one picture each frame; **binding** is a live link
  from an element to a value; a **component** is a reusable group of elements with its bindings and
  logic, and an **instance** stays linked to the master.
- **Key and fill** — broadcast graphics leave as two pictures: the colour **fill** and a
  black-and-white **key** matte, white meaning "show here". The switcher combines them so a scorebug
  can sit over live camera. **Internal keying** sends one combined picture; **external keying**
  sends fill and key separately for the switcher to layer.
- **NDI and SDI** — NDI is video over ordinary Ethernet with no extra hardware; SDI is the broadcast
  coax standard (3G-SDI carries 1080p60) and needs a capture/playout card.
- **Key/Fill, timecode, genlock** — timecode keeps every device on the same show clock (LTC as
  audio, MTC as MIDI); chase follows its position, trigger fires a cue at an exact value; genlock
  locks devices to one video sync reference.
- **OSC / MIDI / Art-Net / sACN / GPIO** — the wiring of venue control: UDP message patterns, MIDI
  notes and control changes, DMX lighting over Ethernet, and physical relays.
- **Feed polling vs push** — asking a provider on a timer versus subscribing to it; the important
  design choice is what happens when the source dies.
- **Primary/backup** — two machines run the show with the second mirroring state (**hot standby**);
  a **lease** or election prevents both from believing they are live.
- **Frame budget** — at 60 fps there is about 16.6 ms per frame for bindings, layout, effects, video
  and output.
