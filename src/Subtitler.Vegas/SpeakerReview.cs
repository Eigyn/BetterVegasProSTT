using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Subtitler.Core;

namespace Subtitler.VegasPlugin
{
    // Review works on the in-memory result. Cancel writes nothing to the project.
    internal sealed class SpeakerReview : Form
    {
        private readonly DataGridView speakers = Grid(), captions = Grid();
        private readonly List<SpeakerChoice> choices;
        private readonly List<Caption> groups;
        public List<SpeakerChoice> Choices { get { return choices; } }
        private sealed class Option
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }
        public SpeakerReview(List<Word> words, List<SpeakerChoice> detected, List<SpeakerChoice> saved, List<AppearancePreset> presets,
            bool shared, GroupingSettings grouping = null)
        {
            Text = "Review speakers"; BackColor = PanelTheme.Background; ForeColor = PanelTheme.Ink; Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(640, 480); Size = new Size(920, 720);
            ShowInTaskbar = false; MinimizeBox = false;
            var activeIds = new HashSet<string>(words.Select(w => w.SpeakerId));
            choices = detected.Concat(saved.Where(s => activeIds.Contains(s.Id) && !detected.Any(d => d.Id == s.Id))).Select(s => {
                var old = saved.FirstOrDefault(p => p.Id == s.Id);
                return new SpeakerChoice { Id = s.Id, Name = old?.Name ?? s.Name, PresetId = old?.PresetId, Recording = s.Recording };
            }).ToList();
            groups = Grouping.Build(words, grouping ?? new GroupingSettings(), 1000);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            int uncertain = words.Count(w => w.SpeakerReview);
            layout.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 8), Text = choices.Count + " speaker label(s) · " + uncertain + " word(s) to review" +
                (shared && words.Any(w => w.SpeakerOverlap) ? " · Shared track: overlapping speech needs review" : "") });
            speakers.Columns.Add(new DataGridViewTextBoxColumn { Name = "Recording", HeaderText = "Recording / stream", ReadOnly = true, FillWeight = 160 });
            speakers.Columns.Add(new DataGridViewTextBoxColumn { Name = "Speaker", HeaderText = "Speaker name", FillWeight = 80 });
            speakers.Columns.Add(new DataGridViewComboBoxColumn { Name = "Preset", HeaderText = "Preset", FlatStyle = FlatStyle.Flat, DisplayMember = "Name", ValueMember = "Id",
                DataSource = new[] { new Option { Id = "", Name = "Source track default" } }.Concat(presets.Select(p => new Option { Id = p.Id, Name = p.Name })).ToList() });
            foreach (var choice in choices) speakers.Rows.Add(choice.Recording, choice.Name, presets.Any(p => p.Id == choice.PresetId) ? choice.PresetId : "");
            layout.Controls.Add(speakers);
            layout.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0, 10, 0, 8), Text = "Check speaker assignments below. Highlighted rows contain uncertain or overlapping speech." });
            captions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", ReadOnly = true, FillWeight = 45 });
            captions.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Subtitle", ReadOnly = true, FillWeight = 220 });
            var assignments = new DataGridViewComboBoxColumn { HeaderText = "Speaker", FlatStyle = FlatStyle.Flat, DisplayMember = "Name", ValueMember = "Id" };
            Action refreshNames = () => assignments.DataSource = choices.Select(c => new Option { Id = c.Id, Name = c.Name + " · " + c.Recording }).ToList();
            refreshNames(); captions.Columns.Add(assignments);
            foreach (var group in groups) {
                int i = captions.Rows.Add(TimeSpan.FromSeconds(group.Start).ToString(@"hh\:mm\:ss\.fff"), group.Text.Replace("\n", " "), group.SpeakerId);
                if (group.Words.Any(w => w.SpeakerReview)) captions.Rows[i].DefaultCellStyle.ForeColor = PanelTheme.Warning;
            }
            // Reopen the assignment menu after renaming to see the new name.
            speakers.CellValueChanged += (s, e) => {
                if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
                choices[e.RowIndex].Name = Convert.ToString(speakers.Rows[e.RowIndex].Cells[1].Value);
                refreshNames();
            };
            layout.Controls.Add(captions);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var build = new StudioButton { Text = "Build subtitles", Width = 132, Height = 32 };
            var cancel = new StudioButton { Text = "Cancel", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
            build.Click += (s, e) => {
                speakers.EndEdit(); captions.EndEdit();
                for (int i = 0; i < choices.Count; i++) {
                    choices[i].Name = Convert.ToString(speakers.Rows[i].Cells[1].Value).Trim();
                    if (choices[i].Name.Length == 0) { speakers.CurrentCell = speakers.Rows[i].Cells[1]; return; }
                    choices[i].PresetId = Convert.ToString(speakers.Rows[i].Cells[2].Value);
                }
                for (int i = 0; i < groups.Count; i++) {
                    string id = Convert.ToString(captions.Rows[i].Cells[2].Value);
                    foreach (var word in groups[i].Words) {
                        if (word.SpeakerId != id) { word.SpeakerEdited = true; word.SpeakerReview = false; }
                        word.SpeakerId = id;
                    }
                }
                DialogResult = DialogResult.OK; Close();
            };
            buttons.Controls.Add(build); buttons.Controls.Add(cancel); layout.Controls.Add(buttons);
            CancelButton = cancel; Controls.Add(layout);
        }
        private static DataGridView Grid()
        {
            return new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, BackgroundColor = PanelTheme.Input,
                BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = PanelTheme.Surface, ForeColor = PanelTheme.Ink },
                DefaultCellStyle = new DataGridViewCellStyle { BackColor = PanelTheme.Input, ForeColor = PanelTheme.Ink, SelectionBackColor = PanelTheme.Accent,
                    SelectionForeColor = PanelTheme.OnAccent }, GridColor = PanelTheme.Thumb };
        }
    }
}
