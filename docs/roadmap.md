# ScorePlus roadmap

A staged plan for turning the console into a full show-control product: run of show, data-bound
graphics, content, data sources, playback machines, broadcast, venue control and operations. It is a
build order, not a wish list, and each stage is self-contained enough to ship on its own.

## Rules

1. **Every stage ends with something a venue can run a game on.** None of it is infrastructure-only
   from the operator's point of view; each stage ships a usable surface.
2. **Everything is implemented in-house.** Features are described by behaviour. Third-party SDKs are
   integrated through their published interfaces only; no code is copied into the project.
3. **Stages stack.** Later work reuses the earlier plumbing — the event bus, the shared condition
   evaluator, the binding layer, the cue list, the node registry. Doing them out of order means
   building the same thing twice.
4. **Replace, don't duplicate.** No stage may add a second version of something ScorePlus already
   has. New capability lands inside the existing window, tab, model or file, and the old path is
   retired in the same release, with migration. A second surface that does the same job is a bug,
   not progress.
5. **Software only.** Everything runs on ordinary computers already in a control room, plus the
   venue's own screens and TVs. No proprietary media players, video processors, switchers, scoring
   consoles or appliance servers are required. Video in and out uses standard capture devices,
   display outputs, NDI/SDI converters or IP; lights, audio and effects use standard USB or network
   adapters. A feature that would only work with a vendor's appliance does not belong here.

Stages are named, not versioned. Each gets cut into releases under whatever version is current
(b1.2 today).

| Stage | Theme | Headline | Size |
| --- | --- | --- | --- |
| 0 | Spine | One show spine: events, conditions, timers, schema versions | M |
| 1 | Run of show | Cue list, workspaces, smart buttons, sport depth, operator modes | L |
| 2 | Graphics | Bindings, element logic, components, team switch, zones, animation | XL |
| 3 | Content | Storyboard/timeline authoring, template gallery, brand kit, packs | L |
| 4 | Data and distribution | Feeds, data viewer, playlists, scheduling, proof of play, headend | L |
| 5 | Playback nodes | Software player/processor nodes, sync groups, deployment | L |
| 6 | Broadcast and video | Graphics output, clip server, switcher, replay, stream overlays | XL |
| 7 | Venue control | Devices, lighting/audio scenes, priority, takeover, emergency, wayfinding | L |
| 8 | Operations | Failover, roles/audit, monitoring, rollouts, backups | XL |

## Deployment topology

The road map is drawn around what a venue actually is: several computers and signal paths, not one
PC with windows. Stages 0–4 work on a single machine; Stage 5 distributes them over ordinary PCs.

- **Control console** — the operator UI and the single source of show state (scores, clocks, cues,
  layouts). Everything else follows it.
- **Graphics output machine** — a second PC (or a second process) whose picture goes to the video
  router as fill+key, triggered and frame-synced from the console. Needed once a graphic must sit
  over live camera rather than appear on an LED display.
- **Clip server** — one or two channels of video with audio and alpha, triggered from the console,
  running on a normal PC with a capture/playout device.
- **Headend channel service** — takes a camera feed plus overlays and publishes a stream the venue's
  TVs can tune to over IPTV or streaming.
- **Display nodes** — a player node per display group (a ribbon, a ring, a corner board) and a
  processor node where a display needs scaling, pixel mapping or colour correction. They render
  locally from synced state, so a network hiccup does not blank a board.
- **Monitor nodes** — multiviewers showing program, preview, cameras and node health.
- **Cameras and router/switcher** — video sources and the machine that cuts between them; the
  console triggers the router rather than replacing it.
- **Stream encoder** — pushes the broadcast picture and overlays to a streaming provider.

Two planes run between them: a **control plane** (show state, cues, health, content packages) and a
**media plane** (SDI or NDI video and audio). A **sync group** ties selected nodes to one frame clock
so a takeover, sweep or reveal starts on the same frame everywhere. A **priority model** decides who
owns the screens when several things want them; Stage 7 defines it.

## Capability coverage

Every capability below is either already in ScorePlus or assigned to a stage. Nothing from the
researched tool set is left out, and nothing here requires dedicated appliance hardware.

| Capability | Stage | Today |
| --- | --- | --- |
| Layered real-time compositing of graphics, video and text | 2 | Layouts and elements exist; layers are limited |
| Properties: position, scale, rotation, colour, effects | 2 | Partly |
| Inline text with auto shrink-to-fit | 2 | Partly |
| Keyframe animation timeline | 3 | No |
| Storyboard sequencing with transitions between scenes | 3 | No |
| Simultaneous playback of several clips in one scene | 2 | Single focus clip |
| Transition logic: graphics that react to game events | 2 | Automation exists, not per element |
| Data-bound fields with formatting | 2 | Token substitution |
| Data transitions (animate on value change) | 2 | No |
| Reusable components and instances | 2 | Copy-paste groups |
| One-switch team identity (colours, logos, wordmarks, rosters) | 2 | Per-layout colours |
| Canvas zones (video, stats, sponsor, out-of-town scores) | 2 | Layout areas only |
| 2D/3D primitives and scene lighting | 2 | No |
| Transition and effects bank | 3 | Basic element transitions |
| Broadcast-standard template set (scorebug, lower third, comparison) | 3 | Templates exist |
| Customisable workspaces (pages, tabs, containers, button size, thumbnails, themes) | 1 | Fixed docks |
| Smart buttons with step-by-step authoring and broken-reference check | 1 | Macros exist |
| Multi-sport scoring: football, basketball, hockey, soccer, volleyball, wrestling, lacrosse, baseball/softball | 1 | Several sports |
| Per-league rule profiles with updates | 1 | Per-sport settings |
| Fanfare: score change fires a graphic | 1 | Automation, indirect |
| Practice timer with named segments | 1 | No |
| Scoring from phone, tablet or laptop; one- or two-operator modes | 1 | Phone remote |
| Admin restriction of video control | 1 | No |
| Cue list with GO/PREV/NEXT, arm, notes, reorder | 1 | No |
| One-press replay of the last 30 seconds | 1 | Replay booth |
| Data sources: Excel, CSV, JSON, XML, local and cloud | 4 | League cloud only |
| League sport feeds and stat apps | 4 | League cloud only |
| Weather and other non-sport feeds | 4 | No |
| Data viewer with filtering and player/team detail | 4 | No |
| Source health and last-value display | 4 | Partial |
| Display oversight dashboard with notifications | 4 | Display picker |
| Playlists targeted to one or many displays | 4 | Ticker rotation |
| Scheduling, including conditional schedules from live data | 4 | No |
| Proof-of-play reporting with export | 4 | No |
| Sponsor inventory, ad slots and rotation weights | 4 | No |
| Headend channel: camera plus overlays to venue TVs over IPTV | 4 | No |
| Software playback nodes with sync groups | 5 | No |
| Processor node: scaling, pixel mapping, colour match, low latency | 5 | No |
| High resolution and HDR output | 5 | No |
| Clip server: two channels, audio, alpha, internal or external keying | 6 | Single clip player |
| Audio normalisation and per-clip levels | 6 | Partial |
| Graphics output to a router as fill+key | 6 | No |
| Switcher: six sources, preview, transitions, M/E buses, keys, keys per bus, picture-in-picture, multiview | 6 | Camera wall only |
| Independent mix outputs (board and stream composed differently) | 6 | No |
| Internal media stores: two clip players, two graphics channels, stills, sound | 6 | Media tab |
| Switcher macros, control surfaces, GPIO triggers | 6 | Keybinds only |
| Integrated audio mixer with embedded and network audio | 6 | Audio routing only |
| Latency and genlock reporting, standards-compliant signal chain | 6 | No |
| Replay desk: multi-angle, variable slow motion, frame stepping, tagged clips, export | 6 | Basic replay |
| Clip export to social, network share or FTP | 6 | No |
| Stream overlays (scorebug and stats over a stream) | 6 | No |
| Stream destinations | 6 | No |
| Lighting control: cue stacks, fades, effects for small rigs | 7 | No |
| Audio scene control (play, stop, levels, routing presets) | 7 | Partial |
| Effects sync: fountains, relays, dry contact | 7 | No |
| OSC, MIDI, DMX/Art-Net, GPIO, webhooks, serial | 7 | Keybinds only |
| Screen priority: emergency > takeover > cue > schedule > default | 7 | No |
| Takeover by zone, release back to schedule | 7 | No |
| Emergency messaging preset | 7 | No |
| Wayfinding content and scheduling | 7 | No |
| Exclusive moments: one-GO full-venue sequence | 7 | Macros only |
| External integration API for house systems | 7 | No |
| Timecode (LTC/MTC) as cue master | 7 | No |
| Genlock and frame sync | 6, 7 | No |
| Roles, permissions and audit trail | 8 | No |
| Primary/backup with hot standby | 8 | Autosave only |
| Automatic backups and versioned restore | 8 | Autosave |
| Central monitoring of computers, nodes and displays | 8 | Event log |
| Central software and content rollouts with rollback | 8 | No |
| Rehearsal mode (run the show without publishing) | 8 | No |
| In-app guidance: tours, tips, searchable help | Cross-cutting | No |
| Automatic application updates from a release feed | 5, 8 | Exists |

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
| Display picker/selector and multiview display list | 4 | One output registry and fleet view; every selector reads names from it. Stage 5 extends it with node discovery. |
| Ticker rotation window | 4 | Becomes one schedule type in the venue scheduler. |
| Audience and output windows rendering on the control PC | 5 | Keep working, but each can be hosted by a node instead; both paths share one renderer. |
| Per-display layout assignment inside one process | 5 | Moves into the node registry: a node owns its display's playlist, version and drift. |
| Output window's display list + audience display picker | 6 | Key/Fill is another mode on the same output window, not a parallel output app. |
| Media tab clips and replay | 6 | Dual-channel server = second channel selector on the existing `MediaSession`/MEDIA tab. No second clip player. |
| Camera wall | 6 | Becomes the switcher's input wall: same previews, plus program/preview, keys and record. |
| Keybinds, control-surface entries, hardware window | 7 | The device map owns addresses and connections; Keybinds stays a shortcut layer onto the same commands, and existing entries are imported as devices. |
| Autosaves, event log, phone remote | 8 | Failover replicates the existing show file and event log; no second show format, no second remote. |

## Stage 0 — The spine

**Why first.** Nearly every capability below is "something reacts to game data": element logic,
clip triggers, venue cues, data overlays, fanfare, conditional schedules. ScorePlus already reacts —
automation rules, macros, score-triggered videos — but each consumer reads game state on its own
schedule and its own timer. Adding more consumers before unifying that multiplies the bugs.

Ships:

- **A show event stream.** `GameEngine` raises typed events (score, clock start/stop, period,
  possession, penalty, media on air, replay, layout published, custom value changed) with a
  monotonic sequence number, so consumers can order and de-duplicate.
- **One condition evaluator.** The matching logic behind automation rules becomes a service that
  rules, cues, elements, media triggers, schedules and device actions all call.
- **One timer.** A single tick/waits/timecode service replaces per-feature timers; it drives the cue
  list, node sync messages, reveal delays and animated data later.
- **One clock and one state snapshot.** Every consumer reads the same immutable snapshot per frame,
  so a graphic, a scorebug and a lighting cue cannot disagree about the score.
- **Schema versions and migration** for saved games, layouts, themes, cue lists, schedules and future
  packs, so later stages can add fields without breaking b1.2 files.

UI changes: nothing new to learn. A **Show health** item in the status bar (last trigger, event
rate, timers running, source count), and a **Triggers** page inside the existing Event log window —
not a separate diagnostics window — listing active rules, timers and their last fire.

Program changes: event surface on `GameEngine`; new Core types (`ShowEvent`, dispatcher, trigger
engine); `AutomationRules`/`AutomationRuntime`/`MacroRunner` subscribe instead of polling;
`MediaSession`/`AudioRouter` publish events; `VideoSurface`/`OutputWindow` report publish and
measured frame rate. Tests for ordering, idempotence and migration; a smoke assertion that a
scripted game produces the expected event order.

Exit: every existing macro and automation behaves identically, driven through the bus, and one test
proves ordered delivery across a simulated game.

## Stage 1 — Run of show

A show is not a pile of buttons, it is an ordered run — and the scoring that drives it has to be
good enough for every sport in the building.

Ships:

- **Cue list per game**: ordered cues made of the macro step vocabulary (`SwitchLayout`, media on
  air, ticker, audio, waits, sport values, device sends later), with PREV / GO / NEXT, arm/disarm,
  skip, jump-to-cue, cue notes and reordering. Saved in the game file.
- **Customisable workspaces**: pages, tabs and containers of buttons, each button sized, placed,
  labelled and given a thumbnail; workspace background and accent colours saved per operator
  profile. This replaces the fixed dock arrangement as the primary surface, while the four tabs
  remain the default workspace.
- **Smart buttons**: a step-by-step authoring wizard for each button (play animation, switch layout,
  run a cue, send a command, wait, run a script-like sequence), and a **broken button** check that
  finds steps pointing at deleted layouts, clips, cues or devices before a show starts.
- **Scoring from any device**: phone, tablet or laptop, with one-operator (scoring only),
  one-operator (scoring and video) and two-operator (dedicated scorer plus video operator) modes;
  administrators can lock video control so a scorekeeper cannot change the board.
- **Sport depth**: football (down, distance, ball on, possession), basketball (player points, fouls,
  bonus, shot-clock presets), hockey (penalty clocks, shots), soccer (cards, stoppage time),
  volleyball (sets, aces, kills), wrestling (weight classes, match score), lacrosse (penalty
  clocks), baseball/softball (count, innings, lineups, positions, at-bat stats).
- **League rule profiles**, selectable per game, with rule updates applied as data rather than a
  new build; a game keeps the profile it started with.
- **Fanfare**: a scoring button can fire a graphic or clip directly, so a touchdown, three-pointer or
  goal runs without a second operator.
- **Practice timer**: named segments with custom drill and break lengths, driven by the same clock.
- **Two-operator split and workspace presets**: a compact scoring surface beside the graphics
  surface, arrangements saved per role ("scorekeeper", "graphics", "cues").
- **One-press replay**: capture the last 30 seconds and replay it from the media desk as a single
  action, using the existing replay booth pipeline.

UI changes: no fifth tab. The existing **MACROS** tab becomes **RUN OF SHOW** — cue list and
transport at the top, the macro board below as the palette you drag from. A cue is a macro plus
position, notes and arm state, so the editor is the one that already exists. A **transport bar**
under the header shows PREV / GO / NEXT and the next cue's name; Space = GO behind a guard, Esc =
panic to a safe layout. The remote page gets the same cue strip and the full scoring surface.
Preferences → Operators gains named profiles, a workspace per profile, permission toggles (score /
graphics / cues / video) and a rule profile.

Program changes: Core `CueList`/`Cue`/`CueRun`, `Workspace`, `ButtonPage`, `RuleProfile`,
`SportMode` models; `MacroDock` gains the cue list above the board; `MacroRunner` gains a cue
execution context (hold, stop, resume); saved macros migrate into cues keeping their ids;
`Keybinds` additions; permissions in `Theme`; `RemotePage` endpoints for cues and score with the
existing auth and same-origin checks; sport models grow the stat fields above; undo must understand
cue runs (undo the steps, not the cue press).

Exit: a pre-game → halftime → final show runs end to end from the cue list, by one operator, and by
two operators on separate surfaces without fighting over the same state; every supported sport can
be scored from a phone.

## Stage 2 — Graphics that think

The real differentiator is not 3D, it is that the graphic knows things: data-bound, logic-driven,
composed of reusable parts, animating smoothly, and switching identity in one move.

Ships:

- **Bindings**: any element binds to a game value, custom value, roster stat or feed field, with
  format (prefix/suffix, decimals, ordinal, clock style), shrink-to-fit text and a live sample in the
  editor. Drag a data field onto the canvas to create a bound text element.
- **Element logic**: per-element conditions and enter/exit/loop actions, evaluated by the Stage 0
  engine — "third down → pulse", "first down → flash + one-shot animation", "score change → count
  up". This is the general form of trigger-based playback.
- **Data transitions**: a value change can animate — roll, flash, odometer, colour emphasis — not
  just swap text.
- **Components**: a reusable group of elements with its bindings and logic ("down & distance
  plate"), instanced across layouts; edit once, update everywhere.
- **Team identity switch**: one control swaps colours, wordmarks and logos across every layout and
  component instance, with a preflight diff of what changes.
- **Canvas zones**: a layout can be divided into regions (live video, game stats, sponsor, out-of-town
  scores, ticker) that are composed and replaced independently while the rest of the board stays put;
  switching a zone is instant.
- **Layered composition**: several video clips, animations and graphics playing at once in one
  scene, with layer ordering, blend and opacity.
- **Video as an element**: a live input or file clip can be placed and masked inside a layout, not
  only played full screen.
- **Motion**: keyframed position, scale, rotation, opacity and colour, with easing presets and
  per-layer in/out timing; one-shot and looping animations; element level transitions.
- **3D primitives and scene lighting** as a later sub-step of this stage: simple 3D objects, a
  camera and lights for depth-styled graphics. Deliberately last — valuable to few venues, expensive
  to do well.

UI changes: the element inspector's existing source picker becomes the **Bindings** page (same
sources, plus formatting and a live sample) — not a second binding UI. A **Logic** tab in that same
inspector replaces element actions in the Layout automation scope; existing layout rules are
migrated onto the elements they target and the scope stops accepting new element actions. An
**Animate** tab joins the inspector for keyframes instead of a separate animation program. A
**Components** panel drags onto the canvas, with "convert selection to component" for groups already
copied around. A **Zones** tool on the canvas reuses the existing area/selection model. The main
window gets a **TEAM** switch (Home / Away / Neutral identity) with a preview diff; the existing team
identity dialog becomes its editor.

Program changes: `ScorebugLayout` gains binding, logic, component-reference, zone and animation
models; a Core resolve/bind pass feeds `ScorebugRender` instead of scattered token substitution;
`ScorebugHtml` mirrors bindings; `ScorebugCanvasView` inspector extensions; the existing team
identity document becomes shared show data rather than per-layout state. Migration is part of the
stage: element-action rules move onto their elements, the old token path is deleted once bindings
cover it, and no element may be written by both systems at once.

Exit: switching teams updates every layout at once; a graphic reacts to the game without a cue; and
a zone-based board replaces video, stats and sponsor panels independently.

## Stage 3 — Content system

Authoring, templates and the cheapest real value: new users start branded instead of empty.

Ships:

- **Storyboard and timeline authoring**: a storyboard view that arranges layouts into a sequence with
  transitions between scenes, and a timeline view that arranges video, animation, image and text
  with in/out points — both editing the same layout file.
- **Asset import and layering**: images, video, audio and 3D objects; layers reordered, shown and
  hidden; inline text frames resized and formatted any time.
- **Transition and effects bank**: fades, dissolves, scrolls and wipes between scenes, plus
  real-time effects on layers (blur, glow, colour, distortion), all usable from cues.
- **A template gallery** by sport and slot (full screen, ribbon, scorebug, lower third, sponsor,
  comparison, lineup, stat panel), with broadcast-standard layouts included, mock-data preview and
  a one-click "new from template".
- **Brand kit**: colours, fonts, logo, wordmark and safe areas, applied to every template and layout;
  brand colours are matched to the display so logos render as intended.
- **Content packs** that export and import as one file, with asset paths, fonts, thumbnails and
  validation on import.
- **Sponsor-ready content**: tagged sponsor assets and placeholder regions so sales inventory maps
  to real boards.

UI changes: the Studio's existing new-layout presets and the `ScoreboardTemplates`/`ScorebugPresets`
catalog become the **CONTENT** gallery — same files, browsable with previews — instead of a library
beside them. Brand kit joins it (and Preferences), replacing the accent-colour-only branding setup.
"New from template" opens the same gallery. The old preset menus and template dialogs are removed in
that release, not left as a shortcut.

Program changes: Core `ContentPack`, `BrandKit`, `Storyboard`, `Timeline` models; template
instantiation into layouts; asset path resolution; pack validation; offscreen thumbnails; a pack
version field tied to Stage 0's schema work. Existing templates are read into packs rather than
duplicated, so there is one catalog and one id space.

Exit: a new operator brands a show, picks a template, adds a storyboard sequence and publishes in
minutes.

## Stage 4 — Data and distribution

Ships:

- **Feeds**: pluggable providers — Excel, CSV, JSON, XML, spreadsheets, HTTP/JSON poll, league sport
  APIs, weather, manual entry, websocket — mapped into game and custom values, each with health,
  last-value, last-error and a manual test refresh.
- **Sport data apps**: sport-specific views over league feeds (score, stats, rosters, headshots),
  filtering and comparing players and teams, and quick "send to graphic" actions.
- **A data viewer**: browse a source, filter rows, preview values as they would appear on a board,
  and pin a player or team for the show.
- **Conditional scheduling**: schedule entries can depend on live data — score, period, weather,
  time of day — not only on the clock.
- **Playlists**: ordered, weighted or shuffled lists of layouts, clips and pages, targeted at one
  display, a group or every display, with loop counts and expiry.
- **Scheduling**: pre-game, halftime, intermission, post-game, sponsor rotations, wayfinding and
  daily house content, per display or zone; ticker rotation becomes one schedule type.
- **Proof of play**: a report of what played, where, when and for how long, with sponsor totals and
  CSV export.
- **Sponsor inventory**: named ad slots with rotation weights and targeting, so a sold package maps
  to real playback.
- **Display oversight**: every display and TV in one list with current content, preview, resolution,
  last-seen health and alerts, plus the ability to push content from that list.
- **Headend channels**: a camera feed plus overlays turned into a channel the venue's TVs can tune to
  over IPTV or streaming, with its own playlist, trigger points and overlaid game data.

UI changes: a **DATA** tab that absorbs the League Cloud window's feed role, the ticker's source
setup and the hardware window's inbound data — one editor per source type, opened from the same
place. A **Venue** window replaces the separate display picker: fleet grid, schedule calendar,
playlists, reports and channels, and the names the main-window selector and multiview read from. The
channel wizard is a page in the Venue window.

Program changes: Core data-source abstraction and HTTP client with retry/backoff; bindings from
Stage 2 consume it; Stage 0 timer drives the scheduler; an output registry with heartbeats from
`OutputWindow`, `MultiviewWindow` and `AudiencePreview`; channel encoding and overlay rendering; a
report store with export; credentials handled the same way as the league cloud secrets. The league
cloud fetch and the ticker's own poller are retired into the data-source layer, and display names
live only in the registry, so `DisplayPickerWindow`/`DisplaySelector` stop keeping their own copies.

Exit: sponsor rotations, conditional schedules and concourse channels run unattended beside the
score show, with health and proof of play visible at a glance.

## Stage 5 — Playback nodes and sync

The change of shape. Until now one machine renders everything it can see. A venue has ribbon boards,
rings, corner boards and concourse screens fed by separate computers, and they must change together.

Ships:

- **A node agent**: a headless mode of the same application, installed on any ordinary PC on the
  venue network. It receives show state and content packages, renders locally, reports health and
  frame stats, and keeps playing its last known state if the control machine disappears.
- **Node roles**: player node (layouts, clips, ticker), processor node (scaling, pixel mapping,
  colour matching, curved and irregular display shapes, low-latency output), monitor node (multiview
  of program, preview, cameras and node health).
- **Sync groups**: selected nodes share a frame clock and a trigger, so a takeover, sweep or reveal
  starts on the same frame; per-node drift is measured and shown, not guessed.
- **Output standards**: resolution, refresh, HDR and colour settings per node, with a test pattern
  and a per-display calibration view.
- **Central deployment**: push layouts, media, schedules and content packs to nodes as versioned
  packages, with preview, rollback and per-node logs.
- **Graceful degradation**: a node that loses the network keeps its playlist and marks itself stale;
  the console shows stale state instead of pretending everything is live.

UI changes: a **Nodes** page in the Venue window (the fleet extended with role, version, sync group,
last frame, drift, stale flag); **Send to nodes** on layouts, media items and packs; a per-node
preview tile; a sync-group editor; node health in the status bar.

Program changes: Core `DisplayNode`/`SyncGroup`/`NodeAssignment` models; the node agent reuses the
existing renderer through the output abstraction rather than a second render path; authenticated
local transport for state and packages; sequence-stamped state with drift measurement; content
packaging; discovery on the LAN; tests with two local agents; a smoke run that kills the control
machine and asserts the node keeps rendering.

Exit: two nodes plus the console, one GO changes both on the same frame, unplugging the console
leaves the boards running, and a node can map a non-rectangular display correctly.

## Stage 6 — Broadcast and video production

Ships:

- **Per-layout broadcast profiles**: safe areas, broadcast sizing, lower-third and ticker stacks, and
  output standards (1080p/720p, 50/60 Hz) per destination.
- **A dedicated graphics output role**: one machine (or one process on a spare core) whose picture
  goes to the router as **fill + key**, triggered and frame-synced from the console, with the console
  still owning the graphic and its data.
- **Alpha-aware output**: fill and key over NDI first, SDI through a capture/playout device when a
  venue asks; internal keying for simple setups, external keying for the router.
- **A dual-channel clip server**: two independent channels with video, audio and alpha, internal or
  external keying, triggered from cues, with per-clip levels, audio normalisation and shrink-to-fit
  name plates.
- **A software switcher**: six simultaneous sources in any mix of capture inputs (720p60 or
  1080p60) and resolution-independent network inputs, of which up to two can carry key and fill,
  with preview of up to four live feeds. Cut, dissolve, wipe and sting transitions with direction and duration;
  multiple **M/E buses** with video re-entry, one effect channel per bus drawing on up to four
  sources, four key layers on the main bus and two on each of the others; downstream keys for
  scorebugs, logos and text; picture-in-picture and split layouts; layer configurations saved and
  recalled per bus; a multiview of program, preview and sources. Uses the existing camera wall as
  its input rack.
- **Independent mix outputs**: up to three separately composed pictures at once — for example one to
  the venue's scoreboard and a different one to the stream — delivered over IP and SDI
  simultaneously, each with its own keys and audio mix, plus one resolution-independent stream
  output that can be archived while it is live.
- **Media stores inside the switcher**: two clip players, two graphic channels, a sound channel and
  four still stores, so a show can cut to a clip, a still or a full-frame graphic without leaving the
  switcher. Supported import formats cover the common video, image and audio types.
- **A replay desk**: continuous capture on one configurable recording channel, mark in/mark out,
  multi-angle playback, variable-speed
  slow motion (including a T-bar style speed control), frame-by-frame stepping, configurable default
  replay length, replay rating and game tags (clock, period, score) so a saved clip can be searched
  later, and one-button "play that again" from the show. Saves to the media library and can be
  exported.
- **Switcher macros and control surfaces**: record and edit sequences of switcher actions, attach
  them to internal events and state changes (audio, media, a specific transition), and fire them from
  a keyboard, a MIDI/control surface, GPIO contacts or on-screen widgets embedded in the operator
  surface — three ways to run the same show, no separate scripting tool.
- **An integrated audio mixer**: per-source levels, embedded audio through NDI/SDI, network audio in
  and out, and the same mix available to a second output.
- **Precision and standards**: alpha-carrying processing, high sample-rate audio, published
  processing and throughput latency targets shown in the UI, genlock against SD, HD or a network
  reference, and a standards-compliant signal chain (3G/HD/SD-SDI, embedded and analog audio
  levels).
- **Stream overlays**: a scorebug and stat graphics rendered over any outgoing stream — browser
  source, NDI or key/fill — so streaming audiences see the same data as the venue.
- **Stream destinations**: saved endpoints for one or more platforms, with the live state and health
  visible on air.

UI changes: broadcast settings (guides, presets, field picker) join the existing layout and output
settings rather than a separate app. **Key/Fill** is another mode on the output window that already
manages audience displays. Channels are a second selector on the existing MEDIA tab — not a second
clip player — and the broadcast panel is the current output list with take/edit. A **Vision** page
holds the switcher's program/preview and the replay desk; the camera wall becomes its source rack. A
**Graphics output** toggle marks which machine renders the router feed; a **Destinations** page holds
stream endpoints.

Program changes: an output abstraction with NDI and SDI backends; an alpha render path through
`ScorebugRender`/`VideoSurface`; clip-server channels inside `MediaSession`, reusing the existing
clip list and audio routing; a switcher compositor over the same renderer; replay capture using the
existing booth pipeline plus disk ring buffer; trigger and genlock hooks from Stage 5's sync; an
encoder backend for stream destinations. The old single-channel playback path becomes channel one
rather than a legacy branch. Licensing caution: the NDI SDK is free to use but proprietary, and SDI
devices carry their own terms — integrate through their SDK, do not copy third-party code.

Exit: a keyed scorebug reaches a router, two clips play with fill+key on separate channels, a camera
cut and a replay run from one operator surface, and a stream carries the same picture with overlays.

## Stage 7 — Venue control

Ships:

- **Devices**: OSC in/out, MIDI notes/CC/timecode, DMX over Art-Net/sACN, GPIO relays, HTTP
  webhooks, serial and IR — a device map with online state, addresses, test/fire buttons, learn mode
  and saved device profiles per venue.
- **Lighting control for small rigs**: fixtures patched to DMX universes, cue stacks with fades and
  chases, colour and position presets, and lighting cues that sit in the run of show beside graphics
  cues.
- **Audio scene control**: play, stop, fade and duck cues; routing and output-level presets; linking
  a cue to a sound effect or music bed.
- **Effects sync**: fountains, relays, dry contacts and other show effects triggered on the same
  frame as the graphic, with run-once and loop modes.
- **One-click moments**: a saved "moment" bundles a graphic sequence, lighting look, audio cue and
  device sends, fired by one button or one GO — the practical form of a venue-wide show moment.
- **A priority model**: what wins when several things want the screens. Emergency overrides
  everything, then an operator takeover, then cues, then schedules, then the default layout — with
  the active priority always visible.
- **Takeover and zones**: take a group of displays (the bowl, the concourse, everything) with one
  control, and release it back to the schedule.
- **Emergency messaging**: a rehearsed, un-interceptable preset for evacuation or weather notices,
  available from the console and from the phone remote.
- **Wayfinding**: a content type and schedule for directional boards, distinct from show content.
- **Exclusive moments**: a full-venue sequence that runs once, triggered by one GO, that ordinary
  cues cannot partially override.
- **Custom integrations**: an authenticated local API plus webhooks so house systems can read state
  and fire cues; a documented command list so third-party controllers can drive the show.
- **Timecode**: LTC/MTC as a cue master with drift shown, alongside manual GO and genlock status.

UI changes: a **Venue Control** window that replaces the ad-hoc control-surface entries in Keybinds
and the hardware window's one-off connection settings — tabs for Devices, Lighting, Audio, Takeover,
Emergency, Integrations and Timecode, with one device list, zone pickers and test buttons. A
**Send** step is a new step kind in the existing macro editor (`MacroActionKind`), not a second
scripting language. The timeline view extends the run-of-show list rather than adding a separate
track editor.

Program changes: device adapters in the desktop app behind a Core `VenueCommand` abstraction;
priority arbitration in the publish path (emergency and takeover validated before cue and schedule
work); a zone model over displays and nodes; authenticated integration endpoints; rate limiting and
confirmation for destructive commands; everything logged in the event log; venue configuration
stored per venue, not per game.

Exit: one GO fires graphic + lighting + audio in a demo rig, an inbound MIDI note runs a cue, an
emergency preset takes every screen in under a second, and the priority that won is shown.

## Stage 8 — Operations at scale

Ships: primary/backup with hot standby and documented failover, role-based operator accounts with an
audit trail, LAN monitoring of computers, nodes and displays, versioned backups and restore points,
central software and content rollouts with rollback, and a rehearsal mode that runs cues without
publishing.

UI changes: Preferences → Operators/Roles, a failover indicator in the status bar, a monitoring page
covering machines and nodes, a rollout page, and a Rehearse/Live toggle in the show menu.

Program changes: show-state replication of the existing show file and event log, lease/election
between machines, encrypted LAN transport, audit log, signed node packages with staged rollout — no
second show format and no second remote page.

Exit: pulling the primary machine leaves the backup running, and a node update can be staged,
rolled back and verified, by procedures that are written down and tested.

## Cross-cutting work (every stage)

- **Performance budget**: 60 fps audience output on integrated graphics; profile at each stage; no
  blocking IO on the UI thread.
- **One time base**: cues, nodes, timecode and stream share a clock; drift is measured and displayed,
  never assumed.
- **In-app guidance**: first-run tours, contextual tips, a searchable help panel and a "what does
  this button do" mode, so a new operator can learn without the manual.
- **Reliability**: preview/backup output path, crash-safe autosave, automatic backups, undo that
  covers new state (cues, bindings, fleet, zones, node changes), and nodes that degrade instead of
  blanking.
- **Compatibility**: schema version + migration for game files, layouts, themes, cue lists, node
  packages and content packs; b1.2 files must keep opening.
- **Testing**: Core unit tests per new model, UiSmoke assertions for every new dock and window (fits
  without scrolling, named controls, handlers exist), two-agent node tests, and a simulated
  full-show smoke run.
- **Docs**: every new surface needs a one-page operator explanation, because a control room cannot
  adopt what it cannot read.
- **No parallel surfaces**: a stage that introduces a control must retire the old one in the same
  release, with a migration path for saved games. Shipping both "for a while" is how a product ends
  up with two scoreboards inside it.
- **Migration tests**: every retired path gets a test that opens a b1.2 file and proves it arrives
  in the new model, not that it silently falls back to the old one.

## If the plan has to be cut

- Two stages only: **0 + 1**. The cue list and sport depth are the single biggest operator win.
- Three: **+ 2**. Bindings, zones and the team switch make every later graphic cheap.
- Then **3** (content), then **4** (data, scheduling, proof of play), then **6** (broadcast, the
  differentiator for venues that stream), then **5**, **7**, **8**.
- Never start **6** before **2**, and never start **7** before **1** — otherwise there is nothing for
  devices, priorities and timecode to drive.
- Don't start **5** before **4**: nodes need the output registry and fleet to be told what they are.
- If a replacement cannot land with its stage, the stage slips. Shipping the new surface beside the
  old one is the one outcome this plan rules out.

## Terms used above

- **Compositor** — a stack of layers drawn into one picture each frame; **binding** is a live link
  from an element to a value; a **component** is a reusable group of elements with its bindings and
  logic, and an **instance** stays linked to the master.
- **M/E bus** — a mix/effect bus: an independent layer stack with its own background, keys and
transitions, which can be re-entered into another bus. **DSK** (downstream key) sits on top of a
program output; **DDR** is a clip player inside the switcher; a **still store** holds full-frame
images; a **COMP** is a saved layer configuration for a bus.
- **Key and fill** — broadcast graphics leave as two pictures: the colour **fill** and a
  black-and-white **key** matte, white meaning "show here". The switcher combines them so a scorebug
  can sit over live camera. **Internal keying** sends one combined picture; **external keying** sends
  fill and key separately for the router to layer.
- **NDI and SDI** — NDI is video over ordinary Ethernet with no extra hardware; SDI is the broadcast
  coax standard (3G-SDI carries 1080p60) and needs a capture/playout card.
- **Timecode and genlock** — timecode keeps every device on the same show clock (LTC as audio, MTC
  as MIDI); chase follows its position, trigger fires a cue at an exact value; genlock locks devices
  to one video sync reference.
- **OSC / MIDI / Art-Net / sACN / GPIO** — the wiring of venue control: UDP message patterns, MIDI
  notes and control changes, DMX lighting over Ethernet, and physical relays.
- **Feed polling vs push** — asking a provider on a timer versus subscribing to it; the important
  design choice is what happens when the source dies.
- **Node** — a computer that renders one part of the venue from synced show state; a **sync group**
  is a set of nodes that change on the same frame.
- **Priority** — the order that decides who owns the screens: emergency, takeover, cues, schedule,
  default.
- **Primary/backup** — two machines run the show with the second mirroring state (**hot standby**);
  a **lease** or election prevents both from believing they are live.
- **Frame budget** — at 60 fps there is about 16.6 ms per frame for bindings, layout, effects, video
  and output.
