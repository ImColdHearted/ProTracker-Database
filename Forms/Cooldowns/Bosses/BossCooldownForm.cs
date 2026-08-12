using Foot_Tracker.Models;
using Foot_Tracker.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.IO;

namespace Foot_Tracker.Forms.Cooldowns.Bosses
{

    public partial class BossCooldownForm : Form
    {
        public BossCooldownForm()
        {
            InitializeComponent();

            ThemeManager.ApplyToForm(this);

            LoadBossesIntoGrid();

            // Ash Westbrook
            BossPicture.Tag = "ashwestbrook";
            BossLabel.Tag = "ashwestbrook";
            BossPanel.Tag = "ashwestbrook";
            BossPanel.Click += BossCooldownCard_Click;
            BossPicture.Click += BossCooldownCard_Click;
            BossLabel.Click += BossCooldownCard_Click;
        }

        private void LoadBossesIntoGrid()
        {
            string bossesFolder =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "DataFiles",
                    "Bosses"
                );

            if (!Directory.Exists(bossesFolder))
                return;

            string[] files =
                Directory.GetFiles(
                    bossesFolder,
                    "*.json"
                )
                .OrderBy(x => x)
                .ToArray();

            BossGrid.SuspendLayout();

            try
            {
                BossGrid.Controls.Clear();

                BossGrid.RowStyles.Clear();

                // Very important:
                // reset the old Designer-created rows.
                BossGrid.RowCount = 0;

                int columns =
                    BossGrid.ColumnCount;

                int index = 0;

                foreach (string file in files)
                {
                    try
                    {
                        string json =
                            File.ReadAllText(file);

                        BossCooldownDefinition? boss =
                            JsonSerializer.Deserialize<
                                BossCooldownDefinition
                            >(
                                json,
                                new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                }
                            );

                        if (boss == null ||
                            string.IsNullOrWhiteSpace(boss.Name))
                        {
                            continue;
                        }

                        int column =
                            index % columns;

                        int row =
                            index / columns;

                        if (BossGrid.RowCount <= row)
                        {
                            BossGrid.RowCount =
                                row + 1;

                            BossGrid.RowStyles.Add(
                                new RowStyle(
                                    SizeType.Absolute,
                                    60F
                                )
                            );
                        }

                        Panel bossCard =
                            CreateBossCard(boss);

                        BossGrid.Controls.Add(
                            bossCard,
                            column,
                            row
                        );

                        index++;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Failed to load boss file " +
                            $"{Path.GetFileName(file)}: " +
                            $"{ex.Message}"
                        );
                    }
                }
            }
            finally
            {
                BossGrid.ResumeLayout(true);
            }
        }

        private Panel CreateBossCard(
    BossCooldownDefinition boss)
        {
            Panel panel =
                new Panel
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(3),
                    BackColor = Color.Transparent,
                    Tag = boss.BossId,
                    Cursor = Cursors.Hand
                };

            PictureBox picture =
                new PictureBox
                {
                    Width = 28,
                    Height = 42,
                    Left = 7,
                    Top = 12,

                    SizeMode =
                        PictureBoxSizeMode.Zoom,

                    BackColor =
                        Color.Transparent,

                    Tag = boss.BossId,
                    Cursor = Cursors.Hand
                };

            picture.Image =
                LoadBossImage(
                    boss.NPCPicture
                );

            Label nameLabel =
                new Label
                {
                    Text = boss.Name,

                    Left = 45,
                    Top = 12,
                    Font = new Font(Font.FontFamily, 7, FontStyle.Bold),
                    Width = 90,
                    Height = 25,

                    AutoSize = false,

                    TextAlign =
                        ContentAlignment.MiddleLeft,

                    BackColor =
                        Color.Transparent,

                    Tag = boss.BossId,
                    Cursor = Cursors.Hand
                };

            Label timerLabel =
                new Label
                {
                    Text =
                        GetBossCooldownText(
                            boss.Name
                        ),

                    Left = 45,
                    Top = 38,
                    Font = new Font(Font.FontFamily, 8, FontStyle.Bold),
                    Width = 90,
                    Height = 22,

                    AutoSize = false,

                    TextAlign =
                        ContentAlignment.MiddleLeft,

                    BackColor =
                        Color.Transparent,

                    Tag = boss.BossId,
                    Cursor = Cursors.Hand
                };

            panel.Click +=
                BossCooldownCard_Click;

            picture.Click +=
                BossCooldownCard_Click;

            nameLabel.Click +=
                BossCooldownCard_Click;

            timerLabel.Click +=
                BossCooldownCard_Click;

            panel.Controls.Add(
                picture
            );

            panel.Controls.Add(
                nameLabel
            );

            panel.Controls.Add(
                timerLabel
            );

            return panel;
        }

        private static Image? LoadBossImage(
    string relativePath)
        {
            if (string.IsNullOrWhiteSpace(
                    relativePath))
            {
                return null;
            }

            string cleanPath =
                relativePath
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);

            string fullPath =
                Path.Combine(
                    AppContext.BaseDirectory,
                    cleanPath
                );

            if (!File.Exists(fullPath))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Boss image not found: {fullPath}"
                );

                return null;
            }

            using FileStream stream =
                new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read
                );

            using Image source =
                Image.FromStream(stream);

            return new Bitmap(source);
        }

        private static string GetBossCooldownText(
    string bossName)
        {
            BossCooldownEntry? cooldown =
                BossCooldownService.GetCooldown(
                    bossName
                );

            if (cooldown == null ||
                cooldown.TimeRemaining <= TimeSpan.Zero)
            {
                return "READY";
            }

            TimeSpan remaining =
                cooldown.TimeRemaining;

            return
                $"{remaining.Days:00}:" +
                $"{remaining.Hours:00}:" +
                $"{remaining.Minutes:00}";
        }

        private void BossCooldownCard_Click(
    object? sender,
    EventArgs e)
        {
            if (sender is not Control control)
                return;

            string bossId =
                control.Tag?.ToString() ?? string.Empty;

            DialogResult result =
                MessageBox.Show(
                    $"Start the cooldown for {bossId}?",
                    "Start Boss Cooldown",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );

            if (result != DialogResult.Yes)
                return;

            BossCooldownService.RegisterBossDefeat(
                bossId
            );

            UpdateBossCooldownDisplay();
        }

        private void UpdateBossCooldownDisplay()
        {
            BossCooldownEntry? ash =
                BossCooldownService.GetCooldown(
                    "Ash Westbrook"
                );

            if (ash == null ||
                ash.TimeRemaining <= TimeSpan.Zero)
            {
                BossLabel.Text =
                    "READY";
            }
            else
            {
                TimeSpan remaining =
                    ash.TimeRemaining;

                BossTimer.Text =
                    $"{remaining.Days:00}:" +
                    $"{remaining.Hours:00}:" +
                    $"{remaining.Minutes:00}";
            }
        }

        private void label3_Click(object sender, EventArgs e)
        {

        }

        private void panel1_Paint(object sender, PaintEventArgs e)
        {

        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {

        }
    }
}
