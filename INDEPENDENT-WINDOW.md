# SW2025 independent window and automatic drafts

Custom build 1.7.11.0, based on the user's existing Unicode / coordinate-system fixes to ros/solidworks_urdf_exporter. The original project license is retained. For the maintained Chinese issue and fix history, see [VERSION_HISTORY.md](VERSION_HISTORY.md).

## 1.7.11 SolidWorks mass property overrides

When assembly link inertia computation is enabled, `IMassProperty2` reads the effective properties of the selected component instances, including an entire subassembly assigned to one Link. SOLIDWORKS combines the components in SI units in the Link coordinate frame, with inertia taken about the center of mass. A parent selection subsumes its descendants within one Link; overlap across Links is rejected. An override on a whole subassembly cannot be apportioned between Links. Failed reads stop preparation rather than falling back to the old body calculation. Build and 99 isolated checks pass; a live SW export of an overridden subassembly remains to be verified.

## 1.7.10 interactive component highlight

Automatic face/edge picks now append the owning component to the SW selection to provide whole-component feedback. Only that persistent reference is resolved and selected; no full-Link scan, clear or reselect is performed. Direct component picks retain their native selection. Repicking an existing association restores feedback without adding a duplicate row, and programmatic highlight notifications are ignored during capture. Build and 93 isolated checks pass, including a 300-component Link. The actual SW viewport feedback and performance remain to be verified live.

## 1.7.9 joint-frame export preflight

When joint-kinematics computation is disabled, Next now compares each stored joint origin with the transform of its current SolidWorks parent and child coordinate systems. A position gap of at least 2 mm or orientation gap of at least 1 degree prompts before mesh export. The user may enable joint and inertial recomputation, explicitly keep manual values, or cancel. This catches the observed J6 case, where a moved STL frame and stale joint origin differed by about 87.3 mm. Explicit frames are cached during the check; automatically generated frames are skipped. The build and 90 isolated checks pass; live SolidWorks frame lookup and export still need verification.

## 1.7.8 picking and stale-reference cleanup

Interactive picks now read only the latest selected component via UserSelectionPostNotify and append one list row without re-highlighting every component in the Link. Rapid picks coalesce draft writes to approximately 500 ms while close, save and export flush synchronously. An explicit full-Link highlight remains available. The toolbar can remove invalid/deleted references, as well as unloaded external components whose referenced file is missing, across all Links; suppressed references and loaded or lightweight components with an existing file are retained. A separate current-Link clear command handles references that SW still considers valid. The cleanup stops continuous picking and checkpoints immediately. Build and 83 isolated checks pass; live SolidWorks performance and missing-file behavior still need verification.

## 1.7.7 URDF structure repair

An exported 1.7.6 package contained a stale wheel joint immediately after base_link and the actual wheel joint near the end, giving one duplicate joint name. Another fixed joint initially had empty parent and child references when joint computation was skipped. Export now reconstructs parent/child references from the Link tree, suppresses the root joint, and checks duplicate names. The disabled root-joint controls no longer display stale values. The build and 80 isolated checks pass; full export with live SolidWorks remains to be verified.

## 1.7.6 export selection isolation

Previously, continuous picking remained checked while the export form changed SW selections, and the editor's export flag was set only after preparation. The editor could import and reselect components highlighted by export Link navigation. Next now stops continuous picking and blocks capture and highlighting before preparing the export. Queued selection callbacks are invalidated when the picking session or current Link changes.

Returning from export leaves picking stopped until the user activates it again. Preparation failure restores the editable window and draft checkpoints. Export Link/Joint highlighting retains its existing clear-then-select implementation; background editor events can no longer append to those selections.

78 isolated checks pass, including events during preparation, queued events across export and return, multiple simulated export Link selections, draft isolation, restarting picking and failed preparation. These use a simulated workspace and do not replace live SolidWorks verification. Earlier sections describe historical releases.

## 1.7.5 surface picking and individual row highlight correction

This supersedes the picking behavior described for 1.7.4 below. A modeless list does not install the native PropertyManager component selection filter. Rejecting faces and edges therefore prevented normal clicks on component surfaces from being imported. Both continuous and manual picking now resolve the owning Component2 using GetSelectedObjectsComponent4 before storing its persistent ID. No face or edge is stored as a Link component.

Selecting a list row highlights that row's component; MouseDown and focus changes no longer overwrite it with full-Link highlighting. Enter highlights selected rows. Switching Link and adding components still highlight the full Link. Row indices resolve directly against persistent IDs so a missing preceding component cannot shift the highlight to a different component.

The delayed reselect after ClearSelectionsNotify has been removed. Global selection clearing and selections without an owning component leave associations intact without forcing the previous SW selection back. The resizable list, per-document drafts and independent window remain.

69 isolated regression checks pass, including simulated surface-owner picks, successive picks, row selection followed by MouseDown, Enter, and global selection clearing without reselection. These are managed editor tests with a simulated workspace; they do not prove live SolidWorks mouse-event or COM behavior. Live picking and export still require verification in SolidWorks.

## 1.7.4 original selection flow and resizable component list

The independent editor now follows the original PropertyManager selection flow: opening the editor or changing Link activates the component selection target and highlights all components already associated with that Link. Clicking the association list restores the same state. Direct component selections are appended continuously, filtered to components like the original selection box, and the full Link selection remains highlighted. Removing list entries immediately refreshes the SW selection.

SolidWorks commands may clear the global selection. The editor restores the Link highlight without deleting stored associations, preserving the recovery behavior that motivated the independent window. Explicit removal remains available from the list, Delete key and context menu.

The associated-components list has a draggable lower grip. Its height can be adjusted from 90 to 520 logical pixels; the surrounding form scrolls when extra space is needed.

## 1.7.3 native selection events and association-list commands

Clicking or focusing the associated-components list now starts continuous picking. SolidWorks NewSelectionNotify events trigger the transfer immediately, and selections of a component, face or edge are mapped to the owning component. Newly added components are highlighted together, while selecting one or more rows highlights only those rows.

The independent list now has a right-click menu for starting or stopping continuous pick, highlighting selected rows or the whole Link, removing or clearing associations, selecting all, copying component names, and refreshing model references. Delete, Ctrl+A and Enter provide the matching keyboard actions. Closing the editor unsubscribes from the assembly event. The 800 ms status timer no longer reads the SW selection, reducing idle interaction overhead.

## 1.7.2 layout, picking and document-binding correction

Field labels and the link-tree heading now reserve their measured single-line width, with wider split-panel limits for Windows display scaling. A continuous-pick mode imports directly selected SW components into the current Link without a separate Add click for every selection. Adding a component, selecting the association list, or selecting a Link in the tree now triggers highlighting; selection is verified by reading the SW selection count. Continuous pick stops when the bound document becomes unavailable.

The add-in now reuses a configuration window only when both the live SW document identity and full path match. Switching assemblies checkpoints and closes the old window, then opens a new window for the current document. Reopening the same path as a new SW document also rebinds. Consequently, a new assembly without its own recovery draft loads its embedded v1.4 configuration instead of showing the previous assembly's window state.

## 1.7.1 startup correction

The user's 1.7.0 log shows a COM failure inside AssemblyWorkspace.IsAvailable while constructing the window. That version discarded the original exception, so the specific failing API cannot be determined from the captured log. Version 1.7.1 removes ISldWorks.IsSame and GetEditTargetComponent from the document guard: document identity uses locally compared IUnknown pointers (with balanced releases), and edit state uses IModelDoc2.IsEditingSelf. It logs the failing operation, HRESULT and original exception if a query fails.

Status checks no longer prevent window construction. ExportHelper is initialized lazily after SW is available. Existing drafts can open offline. Without a draft, the window waits for the original document configuration; failed reads cannot create an empty checkpoint. Transient COM failures retry automatically, while permanent loading failures stop retrying. The full-path draft format and existing recovery files are unchanged.

## Build on Windows

Requires Visual Studio 2022 MSBuild with .NET Framework 4.8 development tools, SolidWorks 2025 interop assemblies, and NuGet. Restore packages with `nuget restore SW2URDF.sln` (packages are omitted from this source archive).

Run `Build-Independent.ps1 -MSBuild <MSBuild.exe> -SolidWorksDir <SolidWorks installation directory>`. This builds the DLL without COM registration or changes to the installed add-in.

For an installer, also pass `-InnoCompiler <ISCC.exe> -OutputDirectory <output directory>`. This release used signed Inno Setup 7.1.0 compiler binaries distributed in Tools.InnoSetup 7.1.0 on NuGet. Installation requires SolidWorks to be closed. Installer execution is separate from compilation.

## Regression checks

After building, run `RecoveryTests\Run.ps1 -Compiler <Roslyn csc.exe>`. Use a fresh output directory each time; the default creates a unique directory. The tests exercise real WinForms controls with a simulated IAssemblyWorkspace, write only their test drafts, and do not connect to SolidWorks or modify CAD documents.

Version 1.7.11 passes 99 isolated checks covering draft recovery, UTF-8 names, component references, document isolation, selection and highlighting, export isolation, URDF structure repair, coordinate preflight, and mass property handling. Native document identity comparison uses separate managed wrappers for a Windows Scripting.Dictionary COM object.

The release has not completed live SolidWorks section view, in-context editing, and full URDF/STL export regression. The execution environment could not retrieve the running SolidWorks COM object (MK_E_UNAVAILABLE). Validate these operations with a copy of an assembly before production use.

## Main changes

- `ExportConfigurationForm`: modeless managed editor with explicit transfer of SW selections, per-edit checkpoints, document-aware action buttons, CSV merge, and recovery-aware detailed export.
- `AssemblyWorkspace`: SW interaction boundary, persistent-reference resolution, rebuild and export checks.
- `DraftStore`: per-full-path identity, XML validation, SHA-256 integrity check, flushed temporary writes, atomic replacement, previous-generation recovery.
- `AssemblyExportForm`: valid-state snapshots every second and on close, preserving the detached joint subtree.
- `SwAddin`: holds one independent configuration window and cleans it up when unloading.

Drafts are stored under `%LOCALAPPDATA%\SW2URDF\Drafts`. Saving configuration into the document does not replace the user's normal SW file-save operation. Moving or renaming an assembly does not automatically migrate drafts. Invalid partially typed numeric values in the detail dialog retain the previous valid snapshot.
