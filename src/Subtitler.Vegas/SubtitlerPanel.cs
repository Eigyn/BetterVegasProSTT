using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScriptPortal.Vegas;
using Subtitler.Core;

namespace Subtitler.VegasPlugin
{
    public sealed class SubtitlerPanel : DockableControl
    {
        private static readonly Color Back = PanelTheme.Background, Surface = PanelTheme.Surface, InputBack = PanelTheme.Input,
            Muted = PanelTheme.Muted, Ink = PanelTheme.Ink;
        private static readonly Font SwatchFont = new Font("Segoe UI", 11.25f);

        private readonly ScriptPortal.Vegas.Vegas vegas;
        private readonly HostService service;
        private readonly string settingsPath;
        private PanelSettings settings = new PanelSettings();
        private readonly TabControl tabs = new PageHost { Dock = DockStyle.Fill };
        private StudioSidebar sidebar;
        private Splitter splitter;
        private int sidebarWidth = PanelTheme.SidebarWidth;
        private readonly ToolTip hints = new ToolTip { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 15000, ShowAlways = true,
            OwnerDraw = true, BackColor = PanelTheme.Surface, ForeColor = PanelTheme.Ink };

        // Generate
        // Unlabelled, like VEGAS's own toolbar dropdowns: each value names itself.
        private readonly ComboBox scope = Choice(150, "Group of Audio Events", "Single Audio Event", "Entire Audio Track");
        private readonly ComboBox audioTrack = Choice(160);
        private readonly ComboBox existing = Choice(130, "Replace Existing", "Keep and Suggest");
        private readonly Label engineSummary = new Label { AutoSize = false, Height = PanelTheme.Row, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0), Margin = Padding.Empty, UseMnemonic = false };
        private Control sourceTrackField;
        private Button generate, cancel;

        // Options
        private readonly ComboBox pipeline = Choice(200);
        private readonly Label pipelineNote = Note("");
        private readonly CheckBox enableCorrection = Check("Correct words and punctuation");
        private readonly CheckBox censorProfanity = Check("Censor profanity");
        private readonly Label correctionNote = Note("");
        private readonly CheckBox enableDiarization = Check("Enable diarization");
        private readonly Label diarizerNote = Note("");
        private readonly FlowLayoutPanel diarizerLine = Line();
        private readonly NumericUpDown maxSpeakers = Number(0, 20, 0);
        private readonly CheckBox sharedSpeakerTrack = Check("Use one shared subtitle track");
        private readonly TableLayoutPanel modelFiles = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 2, 0, 2) };
        private string cacheDirectory;
        private readonly CheckBox snap = Check("Snap speech boundaries", true);
        private readonly ComboBox device = Choice(80, "auto", "cpu", "cuda");
        private readonly NumericUpDown maxWords = Number(1, 50, 7), maxLines = Number(1, 4, 2), maxDuration = Number(.2M, 30, 4, 1), pause = Number(.05M, 5, .4M, 2);
        private readonly CheckBox phrase = Check("Prefer sentence breaks", true), cross = Check("Group across source events"), fill = Check("Fill subtitle gaps");
        private readonly CheckBox force = Check("Transcribe again");
        private readonly CheckBox compress = Check("Fit animation to shorter subtitles");
        private readonly CheckBox retime = Check("Use suggested timing");

        // Subtitle list
        private readonly SubtitleViewport rows = new SubtitleViewport { Dock = DockStyle.Fill };
        private readonly ComboBox listTrack = Choice(170);
        private Control listTrackField, suggestionSeparator;
        private readonly StudioSearchBox search = new StudioSearchBox { Placeholder = "Search subtitle text", Width = 200 };
        private readonly System.Windows.Forms.Timer searchDelay = new System.Windows.Forms.Timer { Interval = 250 };
        private string[] searchTerms = new string[0];
        private readonly CheckBox checkAll = new StudioCheckBox { AutoCheck = false, AccessibleName = "Check all shown subtitles" };
        private readonly Label count = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(8, 0, 6, 0),
            AutoEllipsis = true, ForeColor = Muted, UseMnemonic = false };
        private Button acceptChecked, keepChecked;
        private readonly Label applyScope = Note("Scope: All Tracks");
        private bool rebuildingListTracks;

        // Presets
        private readonly ListBox presetList = new ListBox { Width = 500, Height = 132, IntegralHeight = false, BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, Margin = Padding.Empty };
        private readonly Label presetDetails = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 2), UseMnemonic = false };
        private readonly TextBox presetName = new TextBox { Width = 200, BackColor = InputBack, ForeColor = Ink, BorderStyle = BorderStyle.None };
        private readonly Label editorNote = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 6), UseMnemonic = false };
        private readonly TableLayoutPanel trackDefaults = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        private Panel editorBanner;
        private Button newPreset, editPreset, duplicatePreset, renamePreset, deletePreset, restoreDraft, repairTiming, applyChecked, applyAll;

        // Status
        private readonly Label status = new Label { AutoSize = false, Padding = new Padding(3, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, UseMnemonic = false, Text = "Ready" };
        private readonly StudioProgressBar progress = new StudioProgressBar { Visible = false };
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 900 };

        // Playhead: the rows of subtitles under it, and the shown subtitles' spans,
        // read once per list change rather than from VEGAS on every tick.
        private readonly System.Windows.Forms.Timer playhead = new System.Windows.Forms.Timer { Interval = 100 };
        private HashSet<string> playingIds = new HashSet<string>();
        private List<PlaySpan> spans;
        private struct PlaySpan { public long Start, End; public string Id; }

        private readonly HashSet<string> checkedIds = new HashSet<string>();
        private List<SubtitleItem> items = new List<SubtitleItem>();
        private List<AudioTrackItem> tracks = new List<AudioTrackItem>();
        private CancellationTokenSource cancellation;
        private string fingerprint, trackSignature, lastError;
        private bool refreshing, disposed, dirty = true, loading, building, modal, runtimeReady, correctionEngine;

        public SubtitlerPanel(ScriptPortal.Vegas.Vegas host, string storageDirectory = null, string instanceName = "Eigyn.Subtitler") : base(instanceName)
        {
            vegas = host;
            service = new HostService(host, storageDirectory);
            settingsPath = Path.Combine(service.StorageDirectory, "settings.json");
            DisplayName = "Subtitler";
            DefaultFloatingSize = new Size(860, 820);
            Font = new Font("Segoe UI", 9);
            BackColor = Back; ForeColor = Ink;
            ConfigureHints();
            tabs.TabPages.Add(BuildSubtitlesTab());
            tabs.TabPages.Add(BuildPresetsTab());
            tabs.TabPages.Add(BuildOptionsTab());
            BuildNavigation();
            // Docking runs from the last control added: the status bar spans the panel,
            // the sidebar and its splitter take the left edge and the view fills the rest.
            Controls.Add(tabs); Controls.Add(splitter); Controls.Add(sidebar); Controls.Add(BuildStatusBar());
            FitSidebar();
            LoadSettings();
            HookSettings();
            refreshTimer.Tick += OnRefreshTick;
            vegas.ProjectOpening += OnProjectOpening;
            vegas.ProjectClosed += OnProjectClosed;
            vegas.ProjectOpened += OnProjectOpened;
            vegas.ProjectSaving += OnProjectSaving;
            vegas.ProjectSaved += OnProjectSaved;
            vegas.TrackEventTimeChanged += OnTimelineChanged;
            vegas.TrackEventDataChanged += OnTimelineChanged;
            vegas.TrackEventCountChanged += OnTimelineChanged;
            vegas.TrackStateChanged += OnTimelineChanged;
            vegas.TrackCountChanged += OnTimelineChanged;
            vegas.MediaPoolChanged += OnTimelineChanged;
            vegas.PlaybackStarted += OnPlaybackStarted;
            vegas.PlaybackStopped += OnPlaybackStopped;
            vegas.TimeCursorPositionChanged += OnCursorMoved;
            playhead.Tick += (s, e) => Background(() => FollowPlayhead(true));
            Background(() => RefreshAll(true));
            UpdatePresetEditor();
            refreshTimer.Start();
        }

        protected override void OnLoaded(EventArgs args) { base.OnLoaded(args); rows.FlushPendingLayout(); }

        // ---------- Layout ----------

        // The sidebar replaces a tab strip: Subtitles (with one entry per subtitle track,
        // mirroring the track filter), Presets and Options. Below the rail width it
        // narrows to their glyphs and the track filter appears in the list toolbar.
        private void BuildNavigation()
        {
            sidebar = new StudioSidebar { Dock = DockStyle.Left, Width = sidebarWidth };
            splitter = new Splitter { Dock = DockStyle.Left, Width = 5, BackColor = Back, MinSize = 120, MinExtra = 240 };
            splitter.SplitterMoved += (s, e) => { if (!sidebar.Rail) sidebarWidth = sidebar.Width; };
            sidebar.HoverChanged += entry => Hint(sidebar, entry == null ? null : entry.Level == 0 ? entry.Text + "\n" + entry.Hint : entry.Hint);
            tabs.SelectedIndexChanged += (s, e) => { SyncSidebar(); SaveSettings(); if (tabs.SelectedIndex == 2) { RefreshModelFiles(); UpdateEngineSummary(); } };
            RebuildSidebar();
        }
        private void RebuildSidebar()
        {
            if (sidebar == null || disposed) return;
            var entries = new List<SidebarEntry> { new SidebarEntry { Text = "Subtitles", Glyph = "subtitles", Expandable = true, Select = () => tabs.SelectedIndex = 0,
                Hint = "Generate subtitles and edit their wording. Timing edits stay in the VEGAS timeline." } };
            for (int i = 0; i < listTrack.Items.Count; i++) {
                int index = i; var choice = (SubtitleTrackChoice)listTrack.Items[i];
                int total = choice.Id == null ? items.Count : items.Count(item => item.TrackId == choice.Id);
                entries.Add(new SidebarEntry { Level = 1, Text = choice.Name, Count = total.ToString("N0"),
                    Hint = choice.Id == null ? "Show subtitles from every subtitle track in chronological order." : "Show only the subtitles on " + choice.Name + ". Batch actions then apply to this track.",
                    Select = () => { listTrack.SelectedIndex = index; tabs.SelectedIndex = 0; } });
            }
            entries.Add(new SidebarEntry { Text = "Presets", Glyph = "presets", Count = service.Presets.Count.ToString("N0"), Select = () => tabs.SelectedIndex = 1,
                Hint = "Create styles in the native VEGAS Titles & Text editor and apply them to subtitle events." });
            entries.Add(new SidebarEntry { Text = "Options", Glyph = "options", Select = () => tabs.SelectedIndex = 2,
                Hint = "Grouping, recognition and animation options. Changes are saved automatically between sessions." });
            sidebar.SetEntries(entries, SidebarSelection());
        }
        private int SidebarSelection()
        {
            int trackCount = listTrack.Items.Count;
            return tabs.SelectedIndex == 0 ? 1 + Math.Max(0, Math.Min(trackCount - 1, listTrack.SelectedIndex)) : tabs.SelectedIndex == 1 ? 1 + trackCount : 2 + trackCount;
        }
        private void SyncSidebar() { if (sidebar != null) sidebar.Selected = SidebarSelection(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitSidebar(); }
        private void FitSidebar()
        {
            if (sidebar == null) return;
            bool rail = Width < PanelTheme.RailBelow;
            sidebar.Rail = rail;
            int width = rail ? PanelTheme.RailWidth : Math.Max(120, Math.Min(sidebarWidth, Width - 245));
            if (sidebar.Width != width) sidebar.Width = width;
            splitter.Enabled = !rail;
            // Assigned unconditionally: Visible reads false while its view is hidden.
            if (listTrackField != null) listTrackField.Visible = rail;
        }
        private Control BuildStatusBar()
        {
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
            bar.Controls.Add(status); bar.Controls.Add(progress); bar.Controls.Add(count);
            bar.Layout += (s, e) => ArrangeStatus(bar);
            foreach (var control in new Control[] { status, count }) control.TextChanged += (s, e) => bar.PerformLayout();
            progress.VisibleChanged += (s, e) => bar.PerformLayout();
            // The seam above the status bar is the panel background showing through.
            var footer = new Panel { Dock = DockStyle.Bottom, Height = PanelTheme.Row + 1, BackColor = Back, Padding = new Padding(0, 1, 0, 0) };
            footer.Controls.Add(bar);
            return footer;
        }
        // The message on the left; the counts at the right edge, giving way to the
        // message in a narrow dock; the progress bar fixed against the counts, so the
        // changing stage text never moves it. Widths come from each label's own
        // measure, which follows the host's text renderer.
        private void ArrangeStatus(Control bar)
        {
            int width = bar.ClientSize.Width, height = bar.ClientSize.Height;
            int counts = Math.Min(count.GetPreferredSize(Size.Empty).Width + 2, Math.Max(60, width / 2));
            count.SetBounds(width - counts, 0, counts, height);
            int room = width - counts;
            if (!progress.Visible) { status.SetBounds(0, 0, room, height); return; }
            int meter = Math.Max(0, Math.Min(Math.Max(100, Math.Min(240, room / 3)), room - 8));
            progress.SetBounds(room - meter, 0, meter, height);
            status.SetBounds(0, 0, Math.Max(0, room - meter - 8), height);
        }

        private TabPage BuildSubtitlesTab()
        {
            var page = Page("Subtitles");
            sourceTrackField = audioTrack;
            generate = Primary("Generate", Generate);
            cancel = Quiet("Cancel", () => cancellation?.Cancel(), "close", true); cancel.Enabled = false;
            engineSummary.ForeColor = Muted;
            var run = new StudioToolbar { Dock = DockStyle.Top };
            run.AddRow(new Control[] { generate, cancel, new StudioSeparator(), scope, sourceTrackField, existing }, engineSummary, 80);
            scope.SelectedIndexChanged += (s, e) => { sourceTrackField.Visible = scope.SelectedIndex == 2; };
            Hint(scope, "Source: generate from selected audio events, exactly one selected event, or every event on an audio track.");
            Hint(audioTrack, "Audio track: the source track. Each source track gets its own subtitle video track.");
            Hint(existing, "If subtitles exist: Replace Existing rebuilds them using the current grouping. Keep and Suggest keeps the current subtitles and places new wording below them for comparison. Corrected text is protected by a confirmation before replacement.");
            Hint(generate, "Generate native Titles & Text events from the chosen audio. Existing cached recognition is reused unless Transcribe again is enabled.");
            Hint(cancel, "Cancel recognition. No generated subtitles are inserted until the job completes.");
            Hint(checkAll, "Check or uncheck every subtitle shown by the track filter and the search. Checked rows they hide are preserved and excluded from batch actions.");

            listTrack.DropDownWidth = 480;
            listTrack.SelectedIndexChanged += (s, e) => { if (!rebuildingListTracks) ShowSubtitles(true); SyncSidebar(); };
            Hint(listTrack, "Show subtitles from one video track, or every subtitle track in chronological order. This filters the list and batch actions; audio for generation is chosen above.");
            // Each search rebuilds the visible caption editors, so typing is debounced.
            search.Input.TextChanged += (s, e) => { searchDelay.Stop(); if (search.Text.Length == 0) ApplySearch(); else searchDelay.Start(); };
            search.Input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; ApplySearch(); } };
            searchDelay.Tick += (s, e) => ApplySearch();
            search.Input.AccessibleName = "Search subtitle text";
            string searchHelp = "Show only subtitles whose text or suggested wording contains every word typed here. All, the checked actions and the preset Apply buttons then act on the matches; checked subtitles hidden by the search are kept for later. Esc clears it.";
            Hint(search, searchHelp); Hint(search.Input, searchHelp);
            checkAll.Click += (s, e) => CheckAll();
            acceptChecked = Quiet("Accept Checked", () => { service.Accept(Targets(), retime.Checked, false); RefreshAll(true); }, "check");
            keepChecked = Quiet("Keep Checked", () => { service.Accept(Targets(), false, true); RefreshAll(true); }, "close");
            Hint(acceptChecked, "Accept suggestions for checked rows and timeline-selected subtitles among those shown. Subtitles hidden by the track filter or the search are excluded. Existing timing is preserved unless Use suggested timing is enabled.");
            Hint(keepChecked, "Dismiss suggestions for checked rows and timeline-selected subtitles among those shown, keeping their current wording. Subtitles hidden by the track filter or the search are excluded.");
            var adopt = Quiet("Add Existing", () => { service.AdoptSelected(); RefreshAll(true); }, "plus");
            var refresh = Quiet("Refresh", () => RefreshAll(true), "refresh", true);
            Hint(adopt, "Add native Titles & Text events selected in VEGAS, including events on selected video tracks, to this list.");
            Hint(refresh, "Refresh the subtitle list from the current VEGAS project. Timeline changes also refresh automatically.");

            // The sidebar holds the track filter; a narrow dock shows it here instead.
            listTrackField = Field("Track", listTrack); listTrackField.Visible = false;
            suggestionSeparator = new StudioSeparator();
            var tools = new StudioToolbar { Dock = DockStyle.Top };
            tools.AddRow(new Control[] { adopt, refresh, suggestionSeparator, acceptChecked, keepChecked }, null, 0, listTrackField, search);
            var list = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
            list.Controls.Add(rows); list.Controls.Add(rows.ScrollBar);
            DockViews(page, list, run, tools, new SubtitleListHeader(rows, checkAll) { Dock = DockStyle.Top });
            return page;
        }

        private TabPage BuildPresetsTab()
        {
            var page = Page("Presets");
            var stack = Stack(); stack.BackColor = Surface;

            presetList.BackColor = Surface; presetList.ForeColor = Ink;
            presetList.DrawItem += DrawPreset;
            Hint(presetList, "Choose a style. Double-click to edit it in the native VEGAS Titles & Text window.");
            presetList.SelectedIndexChanged += (s, e) => { if (!loading) { settings.PresetId = ChosenOrNull()?.Id; SaveSettings(); } UpdatePresetDetails(); };
            presetList.DoubleClick += (s, e) => { if (!service.IsEditing && ChosenOrNull() != null) Interactive(() => OpenPreset(ChosenOrNull())); };
            editPreset = Primary("Edit in Titles & Text...", () => OpenPreset(ChosenPreset()));
            newPreset = Quiet("New...", () => OpenPreset(null), "plus");
            duplicatePreset = Quiet("Duplicate", DuplicatePreset, "copy", true);
            renamePreset = Quiet("Rename...", RenamePreset, "rename", true);
            deletePreset = Quiet("Delete", DeletePreset, "trash", true);
            restoreDraft = Quiet("Restore Draft", () => OpenPreset(service.ReadDraft()), "revert");
            repairTiming = Quiet("Repair Timing", RepairTiming, "refresh");
            applyChecked = Quiet("Apply to Checked", () => ApplyPreset(Targets()), "check");
            applyAll = Quiet("Apply to Shown", () => ApplyPreset(ShownItems()), null);
            Hint(newPreset, "Create a style using VEGAS's own text, color, outline, placement and keyframe controls.");
            Hint(editPreset, "Open a temporary reference title in VEGAS. Save here to capture the style and its animation keyframes.");
            Hint(duplicatePreset, "Duplicate: make a separate copy of the selected preset.");
            Hint(renamePreset, "Rename the preset without changing the appearance of existing subtitles.");
            Hint(deletePreset, "Delete the preset from the library. Existing subtitles keep their current appearance.");
            Hint(restoreDraft, "Continue a preset edit preserved when a project was saved or closed.");
            Hint(repairTiming, "Repair keyframe times damaged by an older build, then explicitly reapply the preset to update existing subtitles.");
            Hint(applyChecked, "Apply the style to checked rows and timeline-selected subtitles shown in Subtitles. Subtitles hidden by the track filter or the search are excluded. Wording and timing are preserved.");
            Hint(applyAll, "Apply the style to every subtitle shown in Subtitles. Choose All Tracks in the sidebar and clear the search to apply across the project. Saving a preset alone never updates existing subtitles.");
            var tools = new StudioToolbar { Dock = DockStyle.Top };
            tools.AddRow(new Control[] { editPreset, newPreset, duplicatePreset, renamePreset, deletePreset, restoreDraft, repairTiming, new StudioSeparator(), applyChecked, applyAll }, null, 0);

            editorNote.ForeColor = Muted;
            var naming = Line(); naming.Controls.Add(Field("Preset name", presetName));
            var save = Primary("Save Preset", SavePreset); save.Margin = new Padding(0, 0, 4, 4);
            Hint(save, "Capture all settings and animation keyframes from the VEGAS reference title. Existing subtitles update only when you apply the preset.");
            naming.Controls.Add(save);
            naming.Controls.Add(Button("Cancel Edit", () => { service.CancelEditor(); UpdatePresetEditor(); SetStatus("Preset edit cancelled."); }));
            editorBanner = (Panel)Section("Editing in VEGAS", editorNote, naming);
            stack.Controls.Add(editorBanner);

            stack.Controls.Add(presetList);
            presetDetails.ForeColor = Muted;
            Hint(applyScope, "The scope comes from the track filter and the search in Subtitles. Subtitles they hide are excluded from both Apply actions.");
            var details = Stack(); details.AutoSize = true; details.AutoSizeMode = AutoSizeMode.GrowAndShrink; details.Padding = new Padding(10, 6, 10, 10);
            details.Controls.Add(presetDetails); details.Controls.Add(applyScope);
            stack.Controls.Add(details);

            trackDefaults.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            trackDefaults.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            stack.Controls.Add(Section("Defaults by Audio Track", trackDefaults));
            DockViews(page, new StudioScroller(stack) { Dock = DockStyle.Fill }, tools);
            return page;
        }

        private TabPage BuildOptionsTab()
        {
            var page = Page("Options");
            var stack = Stack(); stack.BackColor = Surface;
            var limits = Line();
            limits.Controls.Add(Field("Words", maxWords)); limits.Controls.Add(Field("Lines", maxLines));
            limits.Controls.Add(Field("Max seconds", maxDuration)); limits.Controls.Add(Field("Pause seconds", pause));
            var engine = Line(); engine.Controls.Add(Field("Tier", pipeline)); engine.Controls.Add(Field("Device", device));
            stack.Controls.Add(Section("Recognition Pipeline", engine, pipelineNote, force, snap));
            Hint(censorProfanity, "Replace the letters of profane words with asterisks in new subtitles, keeping their length and punctuation: \"What the hell?!\" becomes \"What the ****?!\". Uses a built-in word list, so it works without a correction model. Existing subtitles change when they are generated again.");
            diarizerLine.Controls.Add(Field("Max speakers (0 = auto)", maxSpeakers));
            stack.Controls.Add(Section("Correction and Diarization", enableCorrection, correctionNote, censorProfanity,
                enableDiarization, diarizerLine, diarizerNote, sharedSpeakerTrack));
            stack.Controls.Add(Section("Grouping", limits, phrase, cross, fill));
            Hint(enableDiarization, "Identify speakers after transcription and alignment with NVIDIA Nemotron 3 Diarization, which counts up to eight speakers automatically. Off generates subtitles without loading it. Models run one at a time, releasing GPU memory between stages.");
            Hint(maxSpeakers, "Maximum speaker outputs in each recording/stream. Set 2 for a two-person conversation; 0 keeps automatic detection. Established voices are retained; excess labels are matched to them. Uncertain words receive a provisional existing-speaker assignment and stay highlighted for review, instead of creating an extra Unknown track. Changing this limit reuses transcription and alignment.");
            Hint(sharedSpeakerTrack, "Off creates one subtitle video track per source audio track and detected speaker. On shares one track per source; overlapping captions must be reviewed. Positions come from presets.");
            modelFiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            modelFiles.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            modelFiles.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            stack.Controls.Add(Section("Local Storage", modelFiles));
            stack.Controls.Add(Section("Animation and Suggestions", compress, retime));
            Hint(maxWords, "Maximum words in a subtitle. Sentence boundaries, pauses, line count and duration also influence grouping.");
            Hint(maxLines, "Maximum lines per subtitle. Grouping uses words and speech boundaries instead of a fixed character count.");
            Hint(maxDuration, "Maximum subtitle duration in seconds. Subtitles normally end when speech ends.");
            Hint(pause, "A speech pause this long starts a new subtitle, in seconds.");
            Hint(phrase, "Prefer sentence and phrase boundaries when grouping recognized words.");
            Hint(cross, "Allow one subtitle to contain speech from adjacent source events. Off keeps source event boundaries separate.");
            Hint(fill, "Extend each subtitle up to the next subtitle on its track, removing visible gaps. Off ends subtitles when speech ends.");
            Hint(device, "Auto chooses an available recognition device. CUDA uses the NVIDIA GPU; CPU keeps recognition off the GPU.");
            Hint(snap, "Refine word boundaries against the source audio to improve alignment. Audio is read before track effects and calibrated to VEGAS's decoder.");
            Hint(force, "Ignore cached recognition results and transcribe the selected audio again. Normally, cached words can be regrouped without running recognition again.");
            Hint(compress, "Compress keyframe times only when the preset's last keyframe would fall after a subtitle ends. Off preserves the original keyframe timing.");
            Hint(retime, "When accepting suggested wording, also use its newly detected speech timing. Off preserves your subtitle's current start and end.");
            DockViews(page, new StudioScroller(stack) { Dock = DockStyle.Fill });
            return page;
        }

        // ---------- Settings ----------

        private void LoadSettings()
        {
            loading = true;
            try {
                try { if (File.Exists(settingsPath)) settings = Json.Read<PanelSettings>(File.ReadAllText(settingsPath)) ?? new PanelSettings(); }
                catch (Exception error) { Log(error); settings = new PanelSettings(); }
                WorkerSettings worker = null;
                try { worker = WorkerSettings.Load(); } catch (InvalidOperationException) { }
                scope.SelectedIndex = Math.Max(0, Math.Min(2, settings.Scope));
                sourceTrackField.Visible = scope.SelectedIndex == 2;
                existing.SelectedIndex = settings.ReplaceExisting ? 0 : 1;
                pipeline.Items.Clear();
                pipeline.Items.AddRange(RecognitionPipeline.All);
                pipeline.SelectedItem = RecognitionPipeline.Find(settings.Pipeline) ?? RecognitionPipeline.All[0];
                runtimeReady = worker != null;
                cacheDirectory = worker?.CacheDirectory;
                enableDiarization.Checked = settings.EnableDiarization;
                sharedSpeakerTrack.Checked = settings.SharedSpeakerTrack;
                maxSpeakers.Value = Clamp(maxSpeakers, settings.MaxSpeakers);
                correctionEngine = worker != null && worker.HasCorrectionEngine;
                enableCorrection.Checked = settings.EnableCorrection;
                RefreshModelFiles();
                device.SelectedItem = settings.Device ?? worker?.Device ?? "auto";
                if (device.SelectedIndex < 0) device.SelectedItem = "auto";
                var grouping = settings.Grouping ?? new GroupingSettings();
                maxWords.Value = Clamp(maxWords, grouping.MaxWords); maxLines.Value = Clamp(maxLines, grouping.MaxLines);
                maxDuration.Value = Clamp(maxDuration, (decimal)grouping.MaxDuration); pause.Value = Clamp(pause, (decimal)grouping.PauseSeconds);
                phrase.Checked = grouping.PreferPhrases; cross.Checked = grouping.CrossEvents; fill.Checked = grouping.FillGaps;
                censorProfanity.Checked = grouping.CensorProfanity;
                force.Checked = settings.Retranscribe; snap.Checked = settings.SnapToSpeech; compress.Checked = settings.CompressAnimation; retime.Checked = settings.UseNewTiming;
                tabs.SelectedIndex = Math.Max(0, Math.Min(tabs.TabCount - 1, settings.Tab));
                UpdateEngineSummary();
            } finally { loading = false; }
        }
        private static decimal Clamp(NumericUpDown box, decimal value) { return Math.Max(box.Minimum, Math.Min(box.Maximum, value)); }
        private void HookSettings()
        {
            foreach (var box in new[] { scope, existing, pipeline, device }) box.SelectedIndexChanged += (s, e) => { UpdateEngineSummary(); SaveSettings(); };
            foreach (var number in new[] { maxWords, maxLines, maxDuration, pause, maxSpeakers }) number.ValueChanged += (s, e) => SaveSettings();
            foreach (var check in new[] { phrase, cross, fill, force, snap, compress, retime, enableDiarization, sharedSpeakerTrack, enableCorrection, censorProfanity })
                check.CheckedChanged += (s, e) => { UpdateEngineSummary(); SaveSettings(); };
        }
        private void SaveSettings()
        {
            if (loading) return;
            settings.Scope = scope.SelectedIndex; settings.ReplaceExisting = existing.SelectedIndex == 0;
            settings.Pipeline = ChosenPipeline().Id; settings.Device = device.Text; settings.Grouping = ReadGrouping();
            settings.Retranscribe = force.Checked; settings.SnapToSpeech = snap.Checked; settings.CompressAnimation = compress.Checked; settings.UseNewTiming = retime.Checked;
            settings.Tab = tabs.SelectedIndex;
            settings.EnableDiarization = enableDiarization.Checked; settings.SharedSpeakerTrack = sharedSpeakerTrack.Checked;
            settings.MaxSpeakers = (int)maxSpeakers.Value;
            settings.EnableCorrection = enableCorrection.Checked;
            try { Json.Save(settingsPath, settings); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { SetStatus("Options could not be saved: " + error.Message); }
        }
        private GroupingSettings ReadGrouping()
        {
            return new GroupingSettings { MaxWords = (int)maxWords.Value, MaxLines = (int)maxLines.Value, MaxDuration = (double)maxDuration.Value,
                PauseSeconds = (double)pause.Value, PreferPhrases = phrase.Checked, CrossEvents = cross.Checked, FillGaps = fill.Checked,
                CensorProfanity = censorProfanity.Checked };
        }
        private RecognitionPipeline ChosenPipeline() { return pipeline.SelectedItem as RecognitionPipeline ?? RecognitionPipeline.All[0]; }
        private void UpdateEngineSummary()
        {
            var chosen = ChosenPipeline();
            engineSummary.Text = chosen + " · " + device.Text + (enableDiarization.Checked ? " · Nemotron 3 Diarization" : "") +
                (chosen.Correction && enableCorrection.Checked ? " · " + CorrectionModel.Name + " correction" : "");
            enableCorrection.Enabled = chosen.Correction && cancellation == null;
            // Only a reason correction cannot run is shown under it; the model's download
            // and load state is in the check box's tooltip.
            correctionNote.Text = !chosen.Correction ? "Not used: " + chosen.Recognizer.Label + " punctuates itself" : !correctionEngine ? "Engine not installed" : "";
            correctionNote.ForeColor = !chosen.Correction ? Muted : PanelTheme.Warning;
            correctionNote.Visible = correctionNote.Text.Length > 0;
            bool downloaded = cacheDirectory != null && CorrectionModel.Downloaded(cacheDirectory);
            Hint(enableCorrection, "After transcription, " + CorrectionModel.Name + ", a local language model, reads the whole conversation, including subtitles on other tracks, " +
                "and fixes punctuation, capitalization and words misheard as a similar-sounding word (weave for we've). It never adds, drops or rephrases words, and timing is unchanged. " +
                "Corrected words are highlighted in blue in Subtitles.\n" + CorrectionModel.Name + " Q6_K (" + ModelStore.Describe(CorrectionModel.Size) + ") " +
                (downloaded ? "is downloaded and loads after transcription" : "downloads into the recognition cache on first use") + " on Subtitler's own llama.cpp engine. " +
                "When it fits beside VEGAS in GPU memory it corrects a video in seconds; otherwise part of it runs from system memory, which takes longer. Corrections are cached, so regenerating reuses them.");
            Hint(engineSummary, engineSummary.Text + "\n" + chosen.Description + " Change the pipeline in Options.");
            string help = chosen.Description + (runtimeReady ? " The recognizer is released before the aligner loads, so they never share GPU memory." :
                " Not set up yet: close VEGAS and run Setup.cmd.");
            pipelineNote.Text = runtimeReady ? chosen.Requirement + " · aligned by Qwen3-FA Ultra" : "Not set up · run Setup.cmd";
            pipelineNote.ForeColor = runtimeReady ? Muted : PanelTheme.Warning;
            Hint(pipeline, help); Hint(pipelineNote, help);
            diarizerLine.Enabled = sharedSpeakerTrack.Enabled = enableDiarization.Checked;
            maxSpeakers.Enabled = enableDiarization.Checked;
            diarizerNote.Text = enableDiarization.Checked ? "Nemotron 3 · up to 8 speakers" : "Optional · disabled";
            Hint(diarizerNote, DiarizationModel.All[0].Description);
        }

        // ---------- Downloaded models ----------

        private void RefreshModelFiles()
        {
            if (disposed) return;
            modelFiles.SuspendLayout();
            try {
                foreach (Control old in modelFiles.Controls.Cast<Control>().ToArray()) old.Dispose();
                modelFiles.RowStyles.Clear(); modelFiles.RowCount = 0;
                if (cacheDirectory == null) { modelFiles.Controls.Add(Note("Recognition is not set up yet.")); return; }
                var entries = ModelStore.Downloaded(cacheDirectory);
                foreach (var entry in entries) {
                    var current = entry;
                    var name = Caption(entry.Label, 120); name.Dock = DockStyle.Fill; Hint(name, entry.Label);
                    var delete = Quiet("Delete", () => DeleteModel(current), "trash");
                    delete.Margin = new Padding(0, 2, 0, 2);
                    Hint(delete, "Free " + ModelStore.Describe(entry.Size) + " on disk. This model downloads again the next time it is used.");
                    modelFiles.Controls.Add(name);
                    modelFiles.Controls.Add(Caption(ModelStore.Describe(entry.Size), 60));
                    modelFiles.Controls.Add(delete);
                }
                var results = ModelStore.Results(cacheDirectory);
                if (results.Count > 0) {
                    var cached = Caption("Recognition cache · " + results.Count, 120); cached.Dock = DockStyle.Fill;
                    Hint(cached, "Saved transcription and alignment results, and decoded audio. Reusing them lets you change grouping without transcribing again, and trim events without reading long recordings again.");
                    var clear = Quiet("Clear", ClearResults, "trash"); clear.Margin = new Padding(0, 2, 0, 2);
                    Hint(clear, "Delete cached recognition results. The next generation transcribes again.");
                    modelFiles.Controls.Add(cached);
                    modelFiles.Controls.Add(Caption(ModelStore.Describe(results.Sum(f => new FileInfo(f).Length)), 60));
                    modelFiles.Controls.Add(clear);
                }
                if (entries.Count == 0 && results.Count == 0) modelFiles.Controls.Add(Note("Nothing downloaded yet."));
                foreach (var button in modelFiles.Controls.OfType<Button>()) button.Enabled = cancellation == null;
            } finally { modelFiles.ResumeLayout(); }
        }
        private void DeleteModel(ModelStore.Entry entry)
        {
            if (cancellation != null) throw new InvalidOperationException("Wait for recognition to finish before deleting a model.");
            if (!Confirm("Delete " + entry.Label + " (" + ModelStore.Describe(entry.Size) + ")?\n\nIt downloads again the next time it is used.")) return;
            try { ModelStore.Delete(cacheDirectory, entry); }
            finally { RefreshModelFiles(); }
            SetStatus("Deleted " + entry.Label + ".");
        }
        private void ClearResults()
        {
            if (cancellation != null) throw new InvalidOperationException("Wait for recognition to finish before clearing cached results.");
            foreach (var file in ModelStore.Results(cacheDirectory)) File.Delete(file);
            RefreshModelFiles();
            SetStatus("Cleared cached transcription, alignment and speaker results, and decoded audio.");
        }

        // ---------- Generation ----------

        private async void Generate() { await GenerateAsync(); }
        private async Task GenerateAsync()
        {
            if (cancellation != null) return;
            CancellationTokenSource job = null;
            try {
                var sources = service.Snapshot(scope.SelectedIndex, audioTrack.SelectedItem as AudioTrackItem);
                bool replace = existing.SelectedIndex == 0;
                if (replace) {
                    var affected = service.ExistingFor(sources);
                    if (affected.Item2 > 0 && !Confirm(affected.Item1 + " existing subtitle(s) will be replaced, including " + affected.Item2 +
                        " with text you corrected. Those corrections will be lost (Undo in VEGAS restores them).\n\nReplace them?")) {
                        SetStatus("Nothing was generated. Choose Keep and Suggest instead of Replace Existing to review new wording without losing corrections.");
                        return;
                    }
                }
                int session = service.Session;
                var worker = WorkerSettings.Load();
                var chosen = ChosenPipeline();
                worker.Model = chosen.Model; worker.Aligner = chosen.Aligner; worker.Device = device.Text; worker.SnapToSpeech = snap.Checked;
                worker.EnableDiarization = enableDiarization.Checked; worker.DiarizationModel = DiarizationModel.All[0].Id;
                worker.MaxSpeakers = (int)maxSpeakers.Value;
                bool correct = enableCorrection.Checked && chosen.Correction;
                worker.Corrector = correct ? CorrectionModel.Key : null; worker.CorrectorLabel = correct ? CorrectionModel.Name : null;
                var context = correct ? service.CorrectionContext(sources) : null;
                var grouping = ReadGrouping();
                bool shortenAnimation = compress.Checked, retranscribe = force.Checked;
                bool sharedSpeakers = sharedSpeakerTrack.Checked;
                cancellation = job = new CancellationTokenSource();
                SetBusy(true);
                var token = job.Token;
                // Renders a few seconds of each new compressed source through VEGAS so the
                // worker can match its decode to the timeline (HostService.PrepareSync). The
                // render pumps messages, so the job is marked busy first.
                syncFolder = Path.Combine(worker.CacheDirectory, "sync");
                SetStatus("Preparing audio");
                modal = true;
                try { service.PrepareSync(sources, syncFolder); } finally { modal = false; }
                var recognition = await WorkerClient.RecognizeWithProgress(sources, worker, retranscribe,
                    message => { _ = UiThread.RunAsync(this, () => { if (ReferenceEquals(cancellation, job)) SetProgress(message); }); }, token, context).ConfigureAwait(false);
                var words = recognition.Words;
                await UiThread.RunAsync(this, () => {
                    foreach (var source in sources) {
                        double delay;
                        if (recognition.MeasuredDelays.TryGetValue(source.Id, out delay)) service.RememberSync(source, delay);
                    }
                    token.ThrowIfCancellationRequested();
                    service.ValidateSources(sources, session);
                    List<SpeakerChoice> speakers = null;
                    if (worker.EnableDiarization && words.Count > 0) {
                        service.RestoreSpeakerEdits(words);
                        SetStatus("Review speakers"); progress.Report(100);
                        modal = true;
                        try {
                            using (var review = new SpeakerReview(words, recognition.Speakers, service.SpeakerChoices(), service.Presets, sharedSpeakers, grouping)) {
                                if (review.ShowDialog(this) != DialogResult.OK) { SetStatus("Speaker review cancelled. No subtitles were inserted."); return; }
                                speakers = review.Choices;
                            }
                        } finally { modal = false; }
                        token.ThrowIfCancellationRequested(); service.ValidateSources(sources, session);
                    }
                    SetStatus("Building subtitles"); progress.Report(null); status.Update();
                    var result = service.CommitGeneration(sources, words, grouping, shortenAnimation, replace, speakers, sharedSpeakers);
                    RefreshAll(true);
                    SetStatus(string.Join(" ", new[] { Summary(result, sources.Count, words.Count) }.Concat(recognition.Notes)));
                }).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                await UiThread.RunAsync(this, () => SetStatus("Recognition cancelled. No subtitles were inserted.")).ConfigureAwait(false);
            } catch (Exception error) {
                await UiThread.RunAsync(this, () => Report(error, true)).ConfigureAwait(false);
            } finally {
                await UiThread.RunAsync(this, () => {
                    if (ReferenceEquals(cancellation, job)) cancellation = null;
                    SetBusy(false);
                }).ConfigureAwait(false);
                job?.Dispose();
                DeleteSyncReferences();
            }
        }
        private string syncFolder;
        private void DeleteSyncReferences()
        {
            if (syncFolder == null || !Directory.Exists(syncFolder)) return;
            foreach (var file in Directory.GetFiles(syncFolder, "sync-*.wav"))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private static string Summary(GenerationResult result, int sources, int words)
        {
            if (words == 0) return "No speech was detected in the selected audio. Existing subtitles were left unchanged.";
            string from = " from " + sources + " audio event(s).";
            if (result.Replaced > 0) return "Replaced " + result.Replaced + " subtitle(s) with " + result.Created + " new one(s)" + from + " Undo in VEGAS restores the previous set.";
            if (result.Suggested > 0) return "Created " + result.Created + " subtitle(s)" + from + " " + result.Suggested + " existing subtitle(s) have suggested wording below them.";
            return "Created " + result.Created + " subtitle(s)" + from;
        }
        private void SetBusy(bool busy)
        {
            if (disposed) return;
            generate.Enabled = !busy; cancel.Enabled = busy; progress.Visible = busy;
            if (busy) progress.Report(null);
            scope.Enabled = audioTrack.Enabled = existing.Enabled = !busy;
            pipeline.Enabled = device.Enabled = enableDiarization.Enabled = !busy;
            enableCorrection.Enabled = !busy && ChosenPipeline().Correction;
            diarizerLine.Enabled = sharedSpeakerTrack.Enabled = !busy && enableDiarization.Checked;
            foreach (var button in modelFiles.Controls.OfType<Button>()) button.Enabled = !busy;
            // A finished job may have downloaded a model.
            if (!busy) RefreshModelFiles();
        }

        // ---------- Subtitle list ----------

        private sealed class SubtitleTrackChoice
        {
            public string Id, Name;
            public override string ToString() { return Name; }
        }
        // The track filter and the search decide what is shown, and so what batch actions change.
        private List<SubtitleItem> ShownItems()
        {
            string id = (listTrack.SelectedItem as SubtitleTrackChoice)?.Id;
            return items.Where(i => (id == null || i.TrackId == id) && Matches(i)).ToList();
        }
        // Every typed word must appear in the wording or its suggestion, ignoring
        // case and accents.
        private bool Matches(SubtitleItem item)
        {
            foreach (string term in searchTerms)
                if (!Contains(item.Text, term) && !Contains(item.Candidate?.Text, term)) return false;
            return true;
        }
        private static bool Contains(string text, string term)
        {
            return text != null && CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, term,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType) >= 0;
        }
        private void ApplySearch()
        {
            searchDelay.Stop();
            var terms = search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (terms.SequenceEqual(searchTerms)) return;
            searchTerms = terms;
            ShowSubtitles(true);
        }
        private List<SubtitleItem> Targets() { return ShownItems().Where(i => checkedIds.Contains(i.RowId) || i.Event.Selected).ToList(); }

        private void RebuildListTracks()
        {
            string selected = (listTrack.SelectedItem as SubtitleTrackChoice)?.Id;
            rebuildingListTracks = true; listTrack.BeginUpdate();
            try {
                listTrack.Items.Clear();
                listTrack.Items.Add(new SubtitleTrackChoice { Name = "All Tracks" });
                foreach (var group in items.Where(i => i.TrackId != null).GroupBy(i => i.TrackId).OrderBy(g => g.First().TrackIndex)) {
                    var item = group.First();
                    listTrack.Items.Add(new SubtitleTrackChoice { Id = group.Key, Name = (item.TrackIndex + 1) + " · " + item.TrackName });
                }
                listTrack.SelectedItem = listTrack.Items.Cast<SubtitleTrackChoice>().FirstOrDefault(c => c.Id == selected) ?? listTrack.Items[0];
                listTrack.Enabled = listTrack.Items.Count > 1;
            } finally { listTrack.EndUpdate(); rebuildingListTracks = false; }
            RebuildSidebar();
        }
        private void ShowSubtitles(bool resetScroll)
        {
            var shown = ShownItems();
            spans = null;
            if (shown.Count == 0 && items.Count > 0) rows.SetItems(shown, CreateRow, "No matching subtitles", "Try other words, or clear the search.");
            else rows.SetItems(shown, CreateRow);
            if (resetScroll) rows.ScrollToItem(0);
            applyScope.Text = "Scope: " + (listTrack.SelectedItem?.ToString() ?? "All Tracks") +
                (searchTerms.Length > 0 ? " · matching \"" + string.Join(" ", searchTerms) + "\"" : "");
            UpdateCounts();
        }

        private void RefreshAll(bool forceRefresh)
        {
            if (refreshing) return;
            refreshing = true;
            try {
                var current = service.AudioTracks();
                string signature = string.Join("\u0001", current.Select(t => t.Id + "=" + t.Name));
                if (forceRefresh || signature != trackSignature) {
                    trackSignature = signature;
                    string selected = (audioTrack.SelectedItem as AudioTrackItem)?.Id;
                    tracks = current;
                    audioTrack.BeginUpdate();
                    try {
                        audioTrack.Items.Clear(); audioTrack.Items.AddRange(tracks.ToArray());
                        audioTrack.SelectedItem = tracks.FirstOrDefault(t => t.Id == selected) ?? tracks.FirstOrDefault(t => t.Track.Selected) ?? tracks.FirstOrDefault();
                    } finally { audioTrack.EndUpdate(); }
                    RebuildTrackDefaults();
                }
                if (forceRefresh) RefreshPresets();
                var updated = service.Items();
                string rowsSignature = Json.Write(updated.Select(i => new { i.RowId, i.Text, i.Metadata.SourceLabel, i.Metadata.PresetId, i.Metadata.Words,
                    Start = i.Event.Start.Nanos, Length = i.Event.Length.Nanos, i.TrackId, i.TrackName, i.TrackIndex, i.Candidate }));
                if (!forceRefresh && rowsSignature == fingerprint) return;
                fingerprint = rowsSignature; items = updated;
                checkedIds.IntersectWith(items.Select(i => i.RowId));
                RebuildListTracks();
                ShowSubtitles(false);
            } finally { refreshing = false; }
        }
        private void UpdateCounts()
        {
            var shown = ShownItems();
            int suggestions = shown.Count(i => i.Candidate != null), marked = shown.Count(i => checkedIds.Contains(i.RowId));
            bool filtered = (listTrack.SelectedItem as SubtitleTrackChoice)?.Id != null || searchTerms.Length > 0;
            count.Text = shown.Count + (filtered ? " of " + items.Count : "") + (items.Count == 1 ? " subtitle" : " subtitles") +
                (suggestions > 0 ? " · " + suggestions + " suggestions" : "") + (marked > 0 ? " · " + marked + " checked" : "");
            acceptChecked.Visible = keepChecked.Visible = suggestionSeparator.Visible = suggestions > 0;
            checkAll.Enabled = shown.Count > 0;
            checkAll.CheckState = marked == 0 ? CheckState.Unchecked : marked == shown.Count ? CheckState.Checked : CheckState.Indeterminate;
        }
        private void CheckAll()
        {
            var shown = ShownItems();
            if (shown.Count > 0 && shown.All(i => checkedIds.Contains(i.RowId))) checkedIds.ExceptWith(shown.Select(i => i.RowId));
            else checkedIds.UnionWith(shown.Select(i => i.RowId));
            ShowSubtitles(false);
        }

        // A details-view row: check, start, end, source, the editable caption, preset and
        // the Locate and Revert Corrections tools, with any suggestion beneath.
        private Control CreateRow(SubtitleItem item)
        {
            string start = item.Event.Start.ToString(), end = item.Event.End.ToString();
            bool speakerReview = item.Metadata.Words.Any(w => w.SpeakerReview);
            var preset = service.FindPreset(item.Metadata.PresetId);
            var row = new SubtitleRowView { Width = rows.RowWidth, Tag = item, Start = start, End = end, LayoutText = item.Text, SourceWarning = speakerReview,
                Source = (item.Metadata.SpeakerName == null ? "" : item.Metadata.SpeakerName + " · ") + (item.Metadata.SourceLabel ?? "Existing subtitle") + (speakerReview ? " · Speaker review" : ""),
                Preset = preset != null ? preset.Name : item.Metadata.PresetId == null ? "No preset" : "Deleted preset" };
            Hint(row, start + " → " + end + "\nSource: " + row.Source + (speakerReview ? "\nSpeaker assignment contains ambiguous or overlapping speech. Diarization does not separate mixed voices." : "") +
                "\n" + (preset != null ? "Style: " + preset.Name + ". Change it in Presets, then apply it to update this subtitle." : "This subtitle has no saved style in the library."));
            var selected = new StudioCheckBox { Checked = checkedIds.Contains(item.RowId), AccessibleName = "Check " + start };
            Hint(selected, "Check this subtitle for batch actions. Times follow the native event when you edit it in the VEGAS timeline.");
            selected.CheckedChanged += (s, e) => { if (selected.Checked) checkedIds.Add(item.RowId); else checkedIds.Remove(item.RowId); UpdateCounts(); };
            row.Check = selected;
            row.Locate = Quiet("Locate", () => service.Locate(item), "locate", true);
            Hint(row.Locate, "Locate: select this subtitle's title event in VEGAS and move the playhead to its current start.");
            // Offered while the correction model's wording remains; a manual edit replaces it.
            string uncorrected = item.Metadata.UncorrectedText;
            if (uncorrected != null && HostService.Collapse(uncorrected) != HostService.Collapse(item.Text)) {
                row.Revert = Quiet("Revert Corrections", () => { service.RevertCorrections(item); RefreshAll(true); }, "revert", true);
                Hint(row.Revert, "Revert corrections: restore the recognized wording \"" + HostService.Collapse(uncorrected) + "\". Undo in VEGAS brings the corrections back.");
            }
            var text = new CaptionEditor { WheelTarget = rows, Text = item.Text, Font = PanelTheme.Caption, BorderStyle = BorderStyle.None,
                BackColor = Surface, ForeColor = PanelTheme.InkBright, DetectUrls = false, AcceptsTab = false, ScrollBars = RichTextBoxScrollBars.Vertical };
            Func<string, string> shown = Shown(item.Text);
            var changes = item.Metadata.Words.Where(w => Corrected(w) && w.Original.Length > 0).Select(w => shown(w.Original) + " → " + shown(w.Text.Trim())).ToList();
            Hint(text, "Edit the subtitle here. Enter saves; Shift+Enter inserts a line break. Underlined amber words are below 65% recognition confidence." +
                (changes.Count > 0 ? "\nBlue marks words the correction model changed: " + string.Join(", ", changes) + "." : ""));
            Highlight(text, item.Metadata.Words);
            text.SelectionStart = 0; text.SelectionLength = 0;
            text.Leave += (s, e) => Interactive(() => { service.SetText(item, text.Text); item.Text = text.Text; fingerprint = null; dirty = true; });
            text.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; selected.Focus(); } };
            row.Editor = text;
            if (item.Candidate != null) {
                row.Suggestion = SubtitleViewport.CandidateText(item);
                row.Accept = Quiet("Accept", () => { service.Accept(new[] { item }, retime.Checked, false); RefreshAll(true); }, "check");
                row.Keep = Quiet("Keep", () => { service.Accept(new[] { item }, false, true); RefreshAll(true); }, "close");
                Hint(row.Accept, "Use the suggested wording. Current timing is preserved unless Use suggested timing is enabled in Options.");
                Hint(row.Keep, "Dismiss this suggestion and keep the current wording and timing.");
            }
            row.Attach();
            row.Id = item.RowId; row.Playing = playingIds.Contains(row.Id);
            return row;
        }
        // Blue marks a word the correction model changed, an amber underline an unchanged
        // low-confidence word. Punctuation and capitalization fixes are not marked.
        private static void Highlight(RichTextBox text, List<Word> words)
        {
            Func<string, string> shown = Shown(text.Text);
            int offset = 0;
            foreach (var word in words) {
                string token = shown(word.Text.Trim());
                int at = token.Length == 0 ? -1 : text.Text.IndexOf(token, offset, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                if (Corrected(word)) {
                    text.Select(at, token.Length); text.SelectionBackColor = PanelTheme.AccentDeep; text.SelectionColor = PanelTheme.OnAccent;
                } else if (word.Probability.HasValue && word.Probability.Value < .65) {
                    text.Select(at, token.Length); text.SelectionColor = PanelTheme.Warning; text.SelectionFont = PanelTheme.CaptionUnderline;
                }
                offset = at + token.Length;
            }
        }
        // Words keep their profanity when the caption censors it; a caption with none
        // left uncensored shows its words censored.
        private static Func<string, string> Shown(string caption)
        {
            if (Profanity.Censor(caption ?? "") == (caption ?? "")) return Profanity.Censor;
            return word => word;
        }
        private static bool Corrected(Word word) { return word.Original != null && Comparable(word.Original) != Comparable(word.Text); }
        private static string Comparable(string text)
        {
            return new string((text ?? "").ToLowerInvariant().Replace('’', '\'').Where(c => char.IsLetterOrDigit(c) || c == '\'').ToArray());
        }
        private bool EditingCaption() { return Descendants(rows).OfType<RichTextBox>().Any(c => c.ContainsFocus); }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
        }

        // ---------- Presets ----------

        private sealed class PresetChoice
        {
            public AppearancePreset Value;
            public string Detail;
            public override string ToString() { return Value == null ? "(none)" : Value.Name; }
        }
        private AppearancePreset ChosenOrNull() { return (presetList.SelectedItem as PresetChoice)?.Value; }
        private AppearancePreset ChosenPreset()
        {
            var preset = ChosenOrNull();
            if (preset == null) throw new InvalidOperationException("Create or choose a preset first.");
            return preset;
        }
        private void RefreshPresets(string select = null)
        {
            loading = true;
            try {
                string selected = select ?? ChosenOrNull()?.Id ?? settings.PresetId;
                presetList.BeginUpdate();
                try {
                    presetList.Items.Clear();
                    foreach (var preset in service.Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) {
                        string font = ""; try { font = PresetFont(preset); } catch (Exception) { }
                        presetList.Items.Add(new PresetChoice { Value = preset, Detail = (font.Length == 0 ? "Titles & Text" : font) + " · " +
                            (preset.Parameters.Any(p => p.Animated && p.Keys.Count > 1) ? "Animated" : "Static") });
                    }
                    // Retain an explicit height: FlowLayout can collapse a
                    // ListBox with an unconstrained maximum height to zero. Every
                    // preset is shown, so the page scrolls rather than the list.
                    int libraryHeight = presetList.ItemHeight * Math.Max(2, presetList.Items.Count);
                    presetList.MinimumSize = new Size(0, libraryHeight); presetList.Height = libraryHeight;
                    presetList.SelectedItem = presetList.Items.Cast<PresetChoice>().FirstOrDefault(p => p.Value.Id == selected);
                    if (presetList.SelectedIndex < 0 && presetList.Items.Count > 0) presetList.SelectedIndex = 0;
                } finally { presetList.EndUpdate(); }
            } finally { loading = false; }
            UpdatePresetDetails();
            RebuildTrackDefaults();
        }
        private static readonly Dictionary<string, string> ParameterLabels = new Dictionary<string, string> {
            { "TextColor", "text color" }, { "Scale", "scale" }, { "Location", "location" }, { "OutlineWidth", "outline width" }, { "OutlineColor", "outline color" },
            { "Background", "background" }, { "Tracking", "tracking" }, { "LineSpacing", "line spacing" }, { "ShadowColor", "shadow color" },
            { "ShadowOffsetX", "shadow offset" }, { "ShadowOffsetY", "shadow offset" }, { "ShadowBlur", "shadow blur" }, { "Text", "text" } };
        private void UpdatePresetDetails()
        {
            var preset = ChosenOrNull();
            bool idle = !service.IsEditing;
            editPreset.Enabled = duplicatePreset.Enabled = renamePreset.Enabled = deletePreset.Enabled = idle && preset != null;
            newPreset.Enabled = idle;
            applyChecked.Enabled = applyAll.Enabled = preset != null;
            repairTiming.Visible = false; presetDetails.ForeColor = Muted;
            if (preset == null) { presetDetails.Text = service.Presets.Count == 0 ? "Create your first style with New." : ""; return; }
            var animated = preset.Parameters.Where(p => p.Animated && p.Keys.Count > 1).Select(p => ParameterLabels.TryGetValue(p.Name, out var label) ? label : p.Name).Distinct().ToList();
            string font = "";
            try { font = PresetFont(preset); } catch (Exception) { }
            bool collapsed = HostService.HasCollapsedTiming(preset, vegas.Project.Video.FrameRate);
            repairTiming.Visible = collapsed && idle;
            presetDetails.ForeColor = collapsed ? PanelTheme.Warning : Muted;
            presetDetails.Text = (collapsed ? "Timing needs repair · " : "") + (animated.Count == 0 ? "Static style" : "Animates " + string.Join(", ", animated)) + " · revision " + preset.Revision;
            Hint(presetDetails, (font.Length > 0 ? font + ". " : "") + (collapsed ? "Its whole animation lasts under one frame: an earlier build shrank its keyframes. Repair timing restores them (×1000 ÷ " +
                (preset.AuthoringFrameRate > 0 ? preset.AuthoringFrameRate : vegas.Project.Video.FrameRate).ToString("0.###") + " fps); then reapply it to subtitles that use it." :
                "Each subtitle uses the preset's keyframes at their original times. Editing a preset does not change existing subtitles until you apply it."));
        }
        private static string PresetFont(AppearancePreset preset)
        {
            var text = preset.Parameters.FirstOrDefault(p => p.Name == "Text");
            if (text == null) return "";
            using (var box = new RichTextBox()) {
                box.Rtf = Json.Read<string>(text.Value.Json);
                box.Select(0, Math.Min(1, box.TextLength));
                return box.SelectionFont == null ? "" : box.SelectionFont.Name + " " + box.SelectionFont.SizeInPoints.ToString("0.#") + " pt";
            }
        }
        private void OpenPreset(AppearancePreset preset)
        {
            service.OpenPreset(preset);
            presetName.Text = preset == null ? "New preset" : preset.Name;
            UpdatePresetEditor();
            SetStatus("Adjust the VEGAS Titles & Text window, then click Save Preset in Presets.");
        }
        private void SavePreset()
        {
            var saved = service.SaveEditor(presetName.Text);
            settings.PresetId = saved.Id; SaveSettings();
            RefreshPresets(saved.Id);
            UpdatePresetEditor();
            fingerprint = null; dirty = true;
            SetStatus("Preset \"" + saved.Name + "\" saved. Apply it to update existing subtitles.");
        }
        private void DuplicatePreset()
        {
            var copy = service.DuplicatePreset(ChosenPreset());
            RefreshPresets(copy.Id);
            SetStatus("Created \"" + copy.Name + "\".");
        }
        private void RenamePreset()
        {
            var preset = ChosenPreset();
            string name = Prompt("Rename preset", "Preset name", preset.Name);
            if (name == null || name.Trim() == preset.Name) return;
            service.RenamePreset(preset, name);
            RefreshPresets(preset.Id);
            fingerprint = null; dirty = true;
        }
        private void DeletePreset()
        {
            var preset = ChosenPreset();
            if (!Confirm("Delete the preset \"" + preset.Name + "\"?\n\nSubtitles that already use it keep their appearance. Audio tracks that use it as their default will have no default preset.")) return;
            service.DeletePreset(preset);
            RefreshPresets();
            fingerprint = null; dirty = true;
            SetStatus("Deleted \"" + preset.Name + "\".");
        }
        private void RepairTiming()
        {
            var preset = ChosenPreset();
            if (!Confirm("Rescale the keyframes of \"" + preset.Name + "\" so its animation plays at its intended speed?\n\nSubtitles that already use it change only when you apply it again.")) return;
            service.RepairPresetTiming(preset);
            RefreshPresets(preset.Id);
            SetStatus("Repaired \"" + preset.Name + "\". Apply it again to update subtitles that use it.");
        }
        private void ApplyPreset(IEnumerable<SubtitleItem> targets)
        {
            var preset = ChosenPreset();
            var list = targets.ToList();
            service.Apply(preset, list, compress.Checked);
            RefreshAll(true);
            SetStatus("Applied \"" + preset.Name + "\" to " + list.Count + " subtitle(s).");
        }
        private void UpdatePresetEditor()
        {
            bool editing = service.IsEditing;
            editorBanner.Visible = editing;
            restoreDraft.Visible = !editing && service.HasDraft;
            if (editing) {
                var edited = service.EditedPreset;
                double shortest = service.ShortestSubtitle, reference = service.EditingDuration;
                editorNote.Text = "Reference " + reference.ToString("0.###") + " s" + (shortest > 0 ? " · shortest subtitle " + shortest.ToString("0.###") + " s" : "");
                Hint(editorNote, (edited == null ? "Designing a new preset" : "Editing \"" + edited.Name + "\"") + " on a temporary title at the cursor on the top track. " +
                    "Adjust text, colors and keyframes in VEGAS, then save here. The reference is removed when you save or cancel." +
                    (shortest > 0 && shortest < reference ? " The animation is cut off at the end of the shortest subtitle unless fitting is enabled." : ""));
            }
            UpdatePresetDetails();
        }
        private void RebuildTrackDefaults()
        {
            if (building) return;
            building = true;
            trackDefaults.SuspendLayout();
            try {
                foreach (Control control in trackDefaults.Controls.Cast<Control>().ToArray()) control.Dispose();
                trackDefaults.RowStyles.Clear(); trackDefaults.RowCount = 0;
                if (tracks.Count == 0) {
                    var empty = Note("No audio tracks in this project."); trackDefaults.Controls.Add(empty); trackDefaults.SetColumnSpan(empty, 2); return;
                }
                foreach (var track in tracks) {
                    var box = Choice(170); box.Dock = DockStyle.Fill; box.MinimumSize = new Size(90, 0);
                    Hint(box, "Default style for new subtitles from " + track.Name + ". Existing subtitles keep their current style.");
                    box.Items.Add(new PresetChoice());
                    foreach (var preset in service.Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) box.Items.Add(new PresetChoice { Value = preset });
                    var assigned = service.AssignedPreset(track);
                    box.SelectedItem = box.Items.Cast<PresetChoice>().FirstOrDefault(c => c.Value == assigned) ?? box.Items[0];
                    var target = track;
                    box.SelectedIndexChanged += (s, e) => {
                        if (building) return;
                        var preset = ((PresetChoice)box.SelectedItem).Value;
                        Interactive(() => {
                            service.Assign(target, preset);
                            SetStatus(preset == null ? "\"" + target.Name + "\" has no default preset." : "New subtitles for \"" + target.Name + "\" will use \"" + preset.Name + "\".");
                        });
                    };
                    var name = Caption(track.Name, 130); name.Dock = DockStyle.Fill; Hint(name, track.Name);
                    trackDefaults.Controls.Add(name); trackDefaults.Controls.Add(box);
                }
            } finally { trackDefaults.ResumeLayout(); building = false; }
        }

        // ---------- VEGAS events ----------

        private void OnRefreshTick(object sender, EventArgs args)
        {
            if (disposed || modal) return;
            if (dirty && !refreshing && !rows.IsResizing && !EditingCaption())
                Background(() => { RefreshAll(false); dirty = false; FollowPlayhead(playhead.Enabled); });
            // ProjectSaved restores keys compensated for a save; this covers a save that failed.
            var started = service.SaveStarted;
            if (started.HasValue && DateTime.UtcNow - started.Value > TimeSpan.FromMinutes(1)) Background(() => service.FinishSave());
        }
        // A moved or trimmed event changes the spans the playhead is matched against.
        private void OnTimelineChanged(object sender, EventArgs args) { dirty = true; spans = null; }
        private void OnPlaybackStarted(object sender, EventArgs args) { playhead.Start(); }
        private void OnPlaybackStopped(object sender, EventArgs args) { playhead.Stop(); Background(() => FollowPlayhead(false)); }
        private void OnCursorMoved(object sender, EventArgs args) { if (!playhead.Enabled) Background(() => FollowPlayhead(false)); }
        // Highlights the subtitles under the play cursor during playback, or under the
        // edit cursor otherwise, and scrolls the first into view when it changes,
        // unless a caption is being edited.
        private void FollowPlayhead(bool playing)
        {
            if (disposed || modal) return;
            var transport = vegas.Transport;
            long at = (playing && transport.IsPlaying ? transport.PlayCursorPosition : transport.CursorPosition).Nanos;
            // An event deleted since the list was read fails with E_FAIL; the next
            // refresh rereads the list and the highlight resumes.
            if (spans == null)
                try { spans = ShownItems().Select(i => new PlaySpan { Start = i.Event.Start.Nanos, End = i.Event.End.Nanos, Id = i.RowId }).ToList(); }
                catch (System.Runtime.InteropServices.COMException) { return; }
            var now = new HashSet<string>(); int first = -1;
            for (int i = 0; i < spans.Count; i++)
                if (spans[i].Start <= at && at < spans[i].End) { now.Add(spans[i].Id); if (first < 0) first = i; }
            if (now.SetEquals(playingIds)) return;
            playingIds = now;
            foreach (var row in rows.Rows.OfType<SubtitleRowView>()) row.Playing = playingIds.Contains(row.Id);
            if (first >= 0 && !EditingCaption()) rows.EnsureVisible(first);
        }

        // The old project is still open while VEGAS opens another one.
        private void OnProjectOpening(object sender, EventArgs args)
        {
            if (service.IsEditing) Background(() => { service.StoreEditorDraft(presetName.Text); service.CancelEditor(); });
            ResetProject();
        }
        private void OnProjectClosed(object sender, EventArgs args) { ResetProject(); }
        private void OnProjectOpened(object sender, EventArgs args)
        {
            ResetProject();
            Background(() => RefreshAll(true));
        }
        // Everything bound to the previous project's tracks and events is dropped:
        // those objects keep reporting IsValid() but fail with E_FAIL on use.
        private void ResetProject()
        {
            if (disposed) return;
            cancellation?.Cancel();
            service.ProjectChanged();
            items = new List<SubtitleItem>(); tracks = new List<AudioTrackItem>();
            checkedIds.Clear(); fingerprint = null; trackSignature = null; dirty = true;
            searchTerms = new string[0]; search.Text = "";
            audioTrack.Items.Clear();
            RebuildListTracks();
            ShowSubtitles(true);
            RebuildTrackDefaults();
            UpdatePresetEditor();
        }
        private void OnProjectSaving(object sender, EventArgs args)
        {
            // The draft is captured first: compensation rewrites uncommitted keys for the save.
            if (service.IsEditing) Background(() => {
                service.StoreEditorDraft(presetName.Text); service.CancelEditor(); UpdatePresetEditor();
                SetStatus("Preset edit preserved as a draft before saving the project. Use Restore Draft in Presets to continue.");
            });
            Background(() => service.PrepareSave());
        }
        private void OnProjectSaved(object sender, EventArgs args) { Background(() => service.FinishSave()); }
        protected override void OnAppWindowClosing(System.ComponentModel.CancelEventArgs args)
        {
            if (service.IsEditing) Background(() => { service.StoreEditorDraft(presetName.Text); service.CancelEditor(); });
            base.OnAppWindowClosing(args);
        }

        // ---------- Errors and dialogs ----------

        // User actions report failures in a dialog; timer and VEGAS-event work only in
        // the status bar, because the timer keeps firing while a dialog is open.
        private void Interactive(Action action) { try { action(); } catch (Exception error) { Report(error, true); } }
        private void Background(Action action) { try { action(); } catch (Exception error) { Report(error, false); } }
        private void Report(Exception error, bool showDialog)
        {
            if (disposed) return;
            SetStatus(error.Message);
            string key = error.GetType().FullName + ": " + error.Message;
            if (showDialog || key != lastError) Log(error);
            lastError = key;
            if (!showDialog || modal) return;
            modal = true;
            try { MessageBox.Show(this, error.Message, "Subtitler", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { modal = false; }
        }
        private bool Confirm(string message)
        {
            if (modal) return false;
            modal = true;
            try { return MessageBox.Show(this, message, "Subtitler", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes; }
            finally { modal = false; }
        }
        private string Prompt(string title, string caption, string value)
        {
            if (modal) return null;
            using (var form = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false,
                MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(380, 104), BackColor = Surface, ForeColor = Ink, Font = Font }) {
                var box = new TextBox { Text = value, Location = new Point(12, 34), Width = 356, BackColor = InputBack, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle };
                var ok = new StudioButton { Text = "OK", DialogResult = DialogResult.OK, Tone = ButtonTone.Primary,
                    AutoSize = false, Location = new Point(212, 70), Size = new Size(75, PanelTheme.Row) };
                var no = new StudioButton { Text = "Cancel", DialogResult = DialogResult.Cancel,
                    AutoSize = false, Location = new Point(293, 70), Size = new Size(75, PanelTheme.Row) };
                form.Controls.Add(new Label { Text = caption, Location = new Point(12, 12), AutoSize = true });
                form.Controls.Add(box); form.Controls.Add(ok); form.Controls.Add(no);
                form.AcceptButton = ok; form.CancelButton = no;
                modal = true;
                try { return form.ShowDialog(this) == DialogResult.OK ? box.Text : null; }
                finally { modal = false; }
            }
        }
        private void Log(Exception error)
        {
            try {
                Directory.CreateDirectory(service.StorageDirectory);
                File.AppendAllText(Path.Combine(service.StorageDirectory, "subtitler.log"), DateTime.UtcNow.ToString("o") + " " + error + Environment.NewLine);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private void SetStatus(string message) { if (!disposed) { status.Text = message; Hint(status, message); } }
        private void SetProgress(WorkerMessage message)
        {
            if (disposed) return;
            SetStatus(message.Message);
            if (!string.IsNullOrWhiteSpace(message.Detail)) Hint(status, message.Message + "\n" + message.Detail);
            progress.Report(message.Percent);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed) {
                refreshTimer.Stop(); refreshTimer.Dispose(); searchDelay.Stop(); searchDelay.Dispose(); cancellation?.Cancel();
                vegas.ProjectOpening -= OnProjectOpening; vegas.ProjectClosed -= OnProjectClosed;
                vegas.ProjectOpened -= OnProjectOpened; vegas.ProjectSaving -= OnProjectSaving; vegas.ProjectSaved -= OnProjectSaved;
                vegas.TrackEventTimeChanged -= OnTimelineChanged;
                vegas.TrackEventDataChanged -= OnTimelineChanged;
                vegas.TrackEventCountChanged -= OnTimelineChanged;
                vegas.TrackStateChanged -= OnTimelineChanged;
                vegas.TrackCountChanged -= OnTimelineChanged;
                vegas.MediaPoolChanged -= OnTimelineChanged;
                vegas.PlaybackStarted -= OnPlaybackStarted;
                vegas.PlaybackStopped -= OnPlaybackStopped;
                vegas.TimeCursorPositionChanged -= OnCursorMoved;
                playhead.Stop(); playhead.Dispose();
                try { service.StoreEditorDraft(presetName.Text); service.CancelEditor(); } catch (Exception) { }
                disposed = true;
                hints.Dispose();
            }
            base.Dispose(disposing);
        }

        // A TabControl without its tab strip or border: the panel draws its own
        // navigation, and the native frame painted a bright border in dark mode.
        private sealed class PageHost : TabControl
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x1328 /* TCM_ADJUSTRECT */ && !DesignMode) { m.Result = (IntPtr)1; return; }
                base.WndProc(ref m);
            }
        }

        // ---------- Control factories ----------

        private void ConfigureHints()
        {
            hints.Popup += (s, e) => {
                string value = hints.GetToolTip(e.AssociatedControl);
                var size = TextRenderer.MeasureText(value, Font, new Size(340, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                e.ToolTipSize = new Size(size.Width + 24, size.Height + 22);
            };
            hints.Draw += (s, e) => {
                using (var fill = new SolidBrush(Surface)) e.Graphics.FillRectangle(fill, e.Bounds);
                using (var border = new Pen(PanelTheme.Thumb)) e.Graphics.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, Font, new Rectangle(12, 10, e.Bounds.Width - 24, e.Bounds.Height - 20), Ink,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            };
        }
        private void Hint(Control control, string text)
        {
            if (disposed || control.IsDisposed) return;
            hints.SetToolTip(control, text); control.AccessibleDescription = text;
            // A recycled caption must release its native tooltip association.
            control.Disposed -= HintDisposed; control.Disposed += HintDisposed;
        }
        private void HintDisposed(object sender, EventArgs e)
        {
            if (!disposed) hints.SetToolTip((Control)sender, null);
        }
        // A flat 44 px row: an "Aa" swatch on the Video Preview's black, the name and
        // its kind, divided from the next preset by a seam.
        private void DrawPreset(object sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            using (var background = new SolidBrush(Surface)) g.FillRectangle(background, e.Bounds);
            if (e.Index < 0) return;
            var choice = (PresetChoice)presetList.Items[e.Index];
            bool active = (e.State & DrawItemState.Selected) != 0;
            var row = new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height - 1);
            if (active) using (var fill = new SolidBrush(PanelTheme.Accent)) g.FillRectangle(fill, row);
            using (var seam = new SolidBrush(Back)) g.FillRectangle(seam, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Width, 1);
            var swatch = new Rectangle(row.X + 4, row.Y + (row.Height - 36) / 2, 64, 36);
            using (var black = new SolidBrush(Color.Black)) g.FillRectangle(black, swatch);
            TextRenderer.DrawText(g, "Aa", SwatchFont, swatch, PanelTheme.InkBright,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int x = swatch.Right + 8, width = Math.Max(1, row.Right - x - 8);
            TextRenderer.DrawText(g, choice.ToString(), Font, new Rectangle(x, row.Y + 6, width, 16), active ? PanelTheme.OnAccent : Ink, flags);
            TextRenderer.DrawText(g, choice.Detail, PanelTheme.Small, new Rectangle(x, row.Y + 24, width, 14), active ? PanelTheme.OnAccent : Muted, flags);
            if (active && (e.State & DrawItemState.Focus) != 0) using (var focus = new Pen(PanelTheme.OnAccent)) g.DrawRectangle(focus, row.X + 1, row.Y + 1, row.Width - 3, row.Height - 3);
        }

        private Button Button(string title, Action action)
        {
            string glyph = title.StartsWith("Generate") ? "play" : title.StartsWith("Edit") ? "edit" : title.StartsWith("Save") ? "check" : null;
            var button = new StudioButton { Text = title, Glyph = glyph };
            button.Click += (s, e) => Interactive(action);
            return button;
        }
        private Button Primary(string title, Action action)
        {
            var button = (StudioButton)Button(title, action); button.Tone = ButtonTone.Primary; button.Padding = new Padding(8, 0, 12, 0);
            return button;
        }
        // A VEGAS tool button; a glyph-only one keeps its title as its accessible name.
        private Button Quiet(string title, Action action, string glyph, bool iconOnly = false)
        {
            var button = (StudioButton)Button(title, action); button.Tone = ButtonTone.Quiet; button.Glyph = glyph; button.IconOnly = iconOnly;
            button.Padding = new Padding(glyph == null ? 6 : 3, 0, 6, 0);
            return button;
        }
        // A heading with a seam, then the content, on the view's surface: no card.
        private Control Section(string title, params Control[] content)
        {
            var section = new StudioStack { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Surface, Padding = new Padding(10, 6, 10, 10) };
            section.Controls.Add(new StudioHeading { Text = title });
            // Settings sit 6 px apart; a note stays under the setting it describes.
            for (int i = 0; i < content.Length; i++) {
                var control = content[i]; var margin = control.Margin;
                if (i > 0 && !(control is Label)) control.Margin = new Padding(margin.Left, Math.Max(margin.Top, 6), margin.Right, margin.Bottom);
                section.Controls.Add(control);
            }
            return section;
        }
        // A colon label before its input, as VEGAS writes "Project:". In a narrow
        // section the input wraps below the label.
        private static Control Field(string title, Control input)
        {
            var field = new StudioLine { Margin = new Padding(0, 0, 4, 4), Padding = new Padding(0, 0, 8, 0) };
            field.Controls.Add(new Label { Text = title + ":", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 4, 4, 3), UseMnemonic = false });
            // Text and number boxes have no well of their own.
            var control = input is TextBox || input is NumericUpDown ? new StudioInputFrame(input) : input;
            control.Margin = Padding.Empty;
            field.Controls.Add(control); return field;
        }
        // Docks a view's toolbars from the top in order, each followed by a 1px seam,
        // above the control that fills the rest. Docking runs from the last control added.
        private static void DockViews(Control page, Control fill, params Control[] topDown)
        {
            page.Controls.Add(fill);
            foreach (var control in topDown.Reverse()) {
                if (control is StudioToolbar) page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Back });
                page.Controls.Add(control);
            }
        }
        private static TabPage Page(string title) { return new TabPage(title) { BackColor = Back, ForeColor = Ink, Padding = Padding.Empty }; }
        private static Label Caption(string title, int width)
        {
            return new Label { Text = title, UseMnemonic = false, AutoSize = false, Width = width, Height = PanelTheme.Row, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                Margin = new Padding(0, 0, 4, 4) };
        }
        private static Label Note(string text) { return new Label { Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Muted, Font = PanelTheme.Small, Margin = new Padding(0, 0, 0, 4) }; }
        private static ComboBox Choice(int width, params string[] values)
        {
            var box = new StudioComboBox { Width = width };
            box.Items.AddRange(values); if (values.Length > 0) box.SelectedIndex = 0; return box;
        }
        private static NumericUpDown Number(decimal min, decimal max, decimal value, int places = 0)
        {
            return new StudioNumber { Minimum = min, Maximum = max, Value = value, DecimalPlaces = places, Increment = places == 0 ? 1 : .1M, Width = 64, Margin = Padding.Empty };
        }
        private static CheckBox Check(string title, bool value = false) { return new StudioCheckBox { Text = title, Checked = value }; }
        private static FlowLayoutPanel Line() { return new StudioLine(); }
        private static StudioStack Stack() { return new StudioStack(); }
    }
}
