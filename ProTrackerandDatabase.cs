using Foot_Tracker.Forms.BossTemplate;
using Foot_Tracker.Forms.Cooldowns.Bosses;
using Foot_Tracker.Forms.Counterparts;
using Foot_Tracker.Forms.Excavations;
using Foot_Tracker.Forms.Guides.MegaStones;
using Foot_Tracker.Forms.Interactive_Maps;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Foot_Tracker
{
    public partial class ProTrackerandDatabase : Form
    {
        private readonly HuntSession huntSession = new();

        private readonly System.Windows.Forms.Timer huntTimer =
            new System.Windows.Forms.Timer();

        private readonly EncounterTracker encounterTracker =
    new EncounterTracker();

        internal sealed class BlackDropDownTextRenderer
: ToolStripProfessionalRenderer
        {
            protected override void OnRenderItemText(
                ToolStripItemTextRenderEventArgs e)
            {
                // Anything inside an actual dropdown menu
                // must always use black text.
                if (e.Item.Owner is ToolStripDropDown)
                {
                    e.TextColor = Color.Black;
                }

                base.OnRenderItemText(e);
            }
        }

        private void ApplyMenuRenderer()
        {
            menuStrip1.Renderer =
                new BlackDropDownTextRenderer();
        }

        private void TestRandomEncounter()
        {
            if (!huntSession.IsRunning)
                return;

            var pokemon = PokemonSpriteService.AllPokemon;

            if (pokemon.Count == 0)
                return;

            var randomPokemon =
                pokemon[Random.Shared.Next(pokemon.Count)];

            RegisterEncounter(randomPokemon.Name);
        }

        public ProTrackerandDatabase()
        {
            InitializeComponent();

            ThemeManager.ApplyToForm(this);

            // Must happen AFTER the theme,
            // in case the theme changed the renderer.
            ApplyMenuRenderer();

            PokemonSpriteService.Load();
            CounterpartSpriteService.Load();
            BossCooldownService.Load();
            LoadPreviousSession();

            huntTimer.Interval = 1000;
            huntTimer.Tick += HuntTimer_Tick;

            encounterTracker.EncounterDetected +=
                EncounterTracker_EncounterDetected;

            encounterTracker.StatusChanged +=
                EncounterTracker_StatusChanged;

            encounterTracker.CatchResultDetected +=
                EncounterTracker_CatchResultDetected;

            encounterTracker.RareEncounterDetected +=
            EncounterTracker_RareEncounterDetected;

            UpdateTrackerDisplay();
        }

        private void EncounterTracker_RareEncounterDetected(
    string pokemonName,
    RareEncounterType rareType)
        {
            if (InvokeRequired)
            {
                BeginInvoke(
                    new Action<string, RareEncounterType>(
                        EncounterTracker_RareEncounterDetected
                    ),
                    pokemonName,
                    rareType
                );

                return;
            }

            if (!huntSession.IsRunning)
                return;

            switch (rareType)
            {
                case RareEncounterType.Shiny:
                    huntSession.EncountersSinceShiny = 0;
                    break;

                case RareEncounterType.Form:
                    huntSession.EncountersSinceForm = 0;
                    break;

                default:
                    return;
            }

            UpdateTrackerDisplay();

            SessionPersistenceService.Save(
                huntSession
            );
        }

        private void EncounterTracker_EncounterDetected(
    string pokemonName)
        {
            if (InvokeRequired)
            {
                BeginInvoke(
                    new Action<string>(
                        EncounterTracker_EncounterDetected
                    ),
                    pokemonName
                );

                return;
            }

            RegisterEncounter(pokemonName);
        }
        private void EncounterTracker_StatusChanged(
    string status)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EncounterTracker] {status}"
            );
        }

        private static void ResetEncounterTable(
    TableLayoutPanel table)
        {
            // Remove all runtime-created controls.
            // Keep row 0 because it contains:
            // Pokemon | Encounters | Rate
            for (int i = table.Controls.Count - 1;
                 i >= 0;
                 i--)
            {
                Control control =
                    table.Controls[i];

                int row =
                    table.GetRow(control);

                if (row == 0)
                    continue;

                DisposeControlImages(control);

                table.Controls.Remove(control);
                control.Dispose();
            }

            // Remove runtime-created row styles.
            while (table.RowStyles.Count > 1)
            {
                table.RowStyles.RemoveAt(
                    table.RowStyles.Count - 1
                );
            }

            table.RowCount = 1;

            // Preserve/fix header row.
            if (table.RowStyles.Count == 0)
            {
                table.RowStyles.Add(
                    new RowStyle(
                        SizeType.Absolute,
                        30F
                    )
                );
            }
            else
            {
                table.RowStyles[0].SizeType =
                    SizeType.Absolute;

                table.RowStyles[0].Height =
                    30F;
            }
        }

        private void UpdateSessionEncounters()
        {
            const int rowsPerSide = 8;

            SessionEncountersTableLeft.SuspendLayout();
            SessionEncountersTableRight.SuspendLayout();

            try
            {
                ResetEncounterTable(
                    SessionEncountersTableLeft
                );

                ResetEncounterTable(
                    SessionEncountersTableRight
                );

                int total =
                    huntSession.TotalEncounters;

                var encounters =
                    huntSession.EncounterCounts
                        .OrderByDescending(x => x.Value)
                        .ThenBy(x => x.Key)
                        .ToList();

                for (int i = 0;
                     i < encounters.Count;
                     i++)
                {
                    var encounter =
                        encounters[i];

                    string pokemonName =
                        encounter.Key;

                    int count =
                        encounter.Value;

                    double percentage =
                        total > 0
                            ? (double)count /
                              total * 100.0
                            : 0;

                    TableLayoutPanel targetTable =
                        i < rowsPerSide
                            ? SessionEncountersTableLeft
                            : SessionEncountersTableRight;

                    AddSessionEncounterTableRow(
                        targetTable,
                        pokemonName,
                        count,
                        percentage
                    );
                }
            }
            finally
            {
                SessionEncountersTableLeft.ResumeLayout(
                    true
                );

                SessionEncountersTableRight.ResumeLayout(
                    true
                );
            }
        }
        private static void DisposeControlImages(
    Control control)
        {
            if (control is PictureBox pictureBox)
            {
                pictureBox.Image?.Dispose();
                pictureBox.Image = null;
            }

            foreach (Control child in control.Controls)
            {
                DisposeControlImages(child);
            }
        }

        private void AddSessionEncounterTableRow(
            TableLayoutPanel table,
            string pokemonName,
            int count,
            double percentage)
        {
            const int rowHeight = 32;
            int row =
                table.RowCount;

            table.RowCount =
                row + 1;

            // Make THIS exact row 42px tall.
            if (table.RowStyles.Count <= row)
            {
                table.RowStyles.Add(
                    new RowStyle(
                        SizeType.Absolute,
                        rowHeight
                    )
                );
            }
            else
            {
                table.RowStyles[row].SizeType =
                    SizeType.Absolute;

                table.RowStyles[row].Height =
                    rowHeight;
            }

            // ==========================================
            // POKEMON CELL
            // ==========================================

            var pokemonPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = rowHeight,
                Margin = Padding.Empty,
                BackColor = Color.Transparent
            };

            var sprite = new PictureBox
            {
                Width = 28,
                Height = 28,
                Left = 3,
                Top = 2,

                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,

                Image =
                    PokemonSpriteService
                        .GetEncounterSprite(
                            pokemonName
                        )
            };

            var nameLabel = new Label
            {
                Text = pokemonName,

                Left = 35,
                Top = 0,

                Width = 130,
                Height = rowHeight,

                AutoSize = false,

                TextAlign =
                    ContentAlignment.MiddleLeft,

                BackColor =
                    Color.Transparent,

                ForeColor =
                    TotalEncounters.ForeColor
            };

            pokemonPanel.Controls.Add(sprite);
            pokemonPanel.Controls.Add(nameLabel);

            var countLabel = new Label
            {
                Text = count.ToString(),

                Dock = DockStyle.Top,
                Height = rowHeight,
                Margin = Padding.Empty,

                TextAlign = ContentAlignment.MiddleCenter,

                BackColor = Color.Transparent,
                ForeColor = TotalEncounters.ForeColor
            };

            var rateLabel = new Label
            {
                Text = $"{percentage:F2}%",

                Dock = DockStyle.Top,
                Height = rowHeight,
                Margin = Padding.Empty,

                TextAlign = ContentAlignment.MiddleCenter,

                BackColor = Color.Transparent,
                ForeColor = TotalEncounters.ForeColor
            };

            // ==========================================
            // SAME ROW FOR ALL 3
            // ==========================================

            table.Controls.Add(
                pokemonPanel,
                0,
                row
            );

            table.Controls.Add(
                countLabel,
                1,
                row
            );

            table.Controls.Add(
                rateLabel,
                2,
                row
            );

            table.RowStyles[row].SizeType =
    SizeType.Absolute;

            table.RowStyles[row].Height =
                rowHeight;
        }

        private void ProTrackerandDatabase_KeyDown(
object? sender,
KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F8)
            {
                TestRandomEncounter();
            }

            if (e.KeyCode == Keys.F9)
            {
                TestBossCooldown();
            }
        }
        private void TestBossCooldown()
        {
            BossCooldownService.RegisterBossDefeat(
                "Ash Westbrook"
            );

            MessageBox.Show(
                "Ash Westbrook cooldown started."
            );

        }

        private void TestProCapture()
        {
            using Bitmap? screenshot =
                ScreenCapture.CaptureProWindow();

            if (screenshot == null)
            {
                MessageBox.Show(
                    "Could not capture the PRO window.",
                    "Capture Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );

                return;
            }

            if (!BattleWindowLocator.TryLocate(
                    screenshot,
                    out Rectangle battleBounds))
            {
                MessageBox.Show(
                    "PRO was captured, but the battle window could not be located.",
                    "Battle Window Not Found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );

                return;
            }

            Rectangle titleRegion =
                ScreenCapture.GetBattleTitleRegion(
                    battleBounds
                );

            string outputFolder =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "DebugCaptures"
                );

            Directory.CreateDirectory(outputFolder);

            using Bitmap battleOverlay =
                new Bitmap(screenshot);

            using (Graphics graphics =
                   Graphics.FromImage(battleOverlay))
            {
                // Entire battle window
                using Pen battlePen =
                    new Pen(Color.Lime, 4);

                graphics.DrawRectangle(
                    battlePen,
                    battleBounds
                );

                // Battle title region
                using Pen titlePen =
                    new Pen(Color.Red, 3);

                graphics.DrawRectangle(
                    titlePen,
                    titleRegion
                );
            }

            using Bitmap titleCrop =
                ScreenCapture.CropImage(
                    screenshot,
                    titleRegion
                );

            string overlayPath =
                Path.Combine(
                    outputFolder,
                    "debug_battle_locator.png"
                );

            string titlePath =
                Path.Combine(
                    outputFolder,
                    "debug_battle_title.png"
                );

            battleOverlay.Save(
                overlayPath,
                System.Drawing.Imaging.ImageFormat.Png
            );

            titleCrop.Save(
                titlePath,
                System.Drawing.Imaging.ImageFormat.Png
            );

            MessageBox.Show(
                $"Battle window located!\n\n" +
                $"X: {battleBounds.X}\n" +
                $"Y: {battleBounds.Y}\n" +
                $"Width: {battleBounds.Width}\n" +
                $"Height: {battleBounds.Height}\n\n" +
                $"Debug captures saved to:\n" +
                $"{outputFolder}",
                "Battle Locator Test",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }

        public static Rectangle GetWildPokemonTitleRegion(
    Rectangle battleBounds)
        {
            // Start around the middle of the title bar,
            // after the player's name and VS.
            int x = battleBounds.X +
                    (int)(battleBounds.Width * 0.48);

            int y = battleBounds.Y;

            int width =
                battleBounds.Right - x;

            int height = 45;

            return new Rectangle(
                x,
                y,
                width,
                height
            );
        }

        private void EncounterTracker_CatchResultDetected(
    CatchResult result)
        {
            if (InvokeRequired)
            {
                BeginInvoke(
                    new Action<CatchResult>(
                        EncounterTracker_CatchResultDetected
                    ),
                    result
                );

                return;
            }

            if (!huntSession.IsRunning)
                return;

            switch (result)
            {
                case CatchResult.Success:
                    huntSession.SuccessfulCatches++;
                    break;

                case CatchResult.Failed:
                    huntSession.FailedCatches++;
                    break;

                case CatchResult.RunAway:
                case CatchResult.None:
                default:
                    return;
            }

            UpdateTrackerDisplay();

            SessionPersistenceService.Save(
                huntSession
            );
        }

        private void HuntTimer_Tick(
            object? sender,
            EventArgs e)
        {
            UpdateTrackerDisplay();
        }

        private void LoadPreviousSession()
        {
            HuntSessionSaveData? saved =
                SessionPersistenceService.Load();

            if (saved == null)
                return;

            huntSession.Restore(saved);

            UpdateTrackerDisplay();
            UpdateSessionEncounters();
        }

        private void SelectPokemon_Click(
            object sender,
            EventArgs e)
        {
            using var selector =
                new PokemonSelectorForm();

            if (selector.ShowDialog(this) != DialogResult.OK)
                return;

            if (string.IsNullOrWhiteSpace(
                    selector.SelectedPokemon))
            {
                return;
            }

            huntSession.TargetPokemon =
                selector.SelectedPokemon;

            UpdateTrackerDisplay();

            SessionPersistenceService.Save(
            huntSession);
        }

        private void PlayButton_Click(
            object sender,
            EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(
                    huntSession.TargetPokemon))
            {
                MessageBox.Show(
                    "Select a Pokémon before starting the tracker.",
                    "No Pokémon Selected",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                return;
            }

            if (huntSession.IsRunning)
                return;

            huntSession.Start();

            huntTimer.Start();

            encounterTracker.Start();

            UpdateTrackerDisplay();
        }

        private async void StopButton_Click(
            object sender,
            EventArgs e)
        {
            if (!huntSession.IsRunning)
                return;

            huntSession.Pause();

            huntTimer.Stop();

            await encounterTracker.StopAsync();

            SessionPersistenceService.Save(
            huntSession
                );

            UpdateTrackerDisplay();
        }

        private async void ResetButton_Click(
            object sender,
            EventArgs e)
        {
            DialogResult result =
                MessageBox.Show(
                    "Reset the current hunt?",
                    "Reset Hunt",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );

            if (result != DialogResult.Yes)
                return;

            huntTimer.Stop();

            await encounterTracker.StopAsync();

            huntTimer.Stop();

            huntSession.Reset();

            SessionPersistenceService.Delete();

            UpdateTrackerDisplay();
            UpdateSessionEncounters();
        }

        private void RegisterEncounter(string pokemonName)
        {
            if (!huntSession.IsRunning)
                return;

            string resolvedName =
                PokemonSpriteService.ResolveEncounterName(
                    pokemonName
                );

            huntSession.PreviousEncounter =
                huntSession.CurrentEncounter;

            huntSession.CurrentEncounter =
                resolvedName;

            huntSession.TotalEncounters++;
            huntSession.EncountersSinceShiny++;
            huntSession.EncountersSinceForm++;

            huntSession.RegisterPokemonEncounter(
    resolvedName
);

            UpdateTrackerDisplay();
            UpdateSessionEncounters();

            SessionPersistenceService.Save(
                huntSession
            );
        }

        private void UpdateTrackerDisplay()
        {
            TotalEncounters.Text =
                huntSession.TotalEncounters.ToString();

            TimeHunting.Text =
                huntSession
                    .GetCurrentElapsedTime()
                    .ToString(@"hh\:mm\:ss");

            SinceShiny.Text =
                huntSession.EncountersSinceShiny.ToString();

            SinceForm.Text =
                huntSession.EncountersSinceForm.ToString();

            int totalCatchAttempts =
                huntSession.SuccessfulCatches +
                huntSession.FailedCatches;

            double catchRate =
                totalCatchAttempts > 0
                    ? huntSession.SuccessfulCatches /
                      (double)totalCatchAttempts * 100.0
                    : 0;

            CatchRate.Text =
                $"{catchRate:F2}%";

            SuccessfulCatches.Text =
                huntSession.SuccessfulCatches.ToString();

            FailedCatches.Text =
                huntSession.FailedCatches.ToString();


            // TARGET POKEMON
            // Keep using normal pokemon-species.json lookup
            CurrentlyHunting.Image =
                PokemonSpriteService.GetEncounterSprite(
                    huntSession.TargetPokemon
                );

            // ENCOUNTERS
            // These should now use pokemon-forms.json-aware lookup
            CurrentEncounter.Image =
                PokemonSpriteService.GetEncounterSprite(
                    huntSession.CurrentEncounter
                );

            PreviousEncounter.Image =
                PokemonSpriteService.GetEncounterSprite(
                    huntSession.PreviousEncounter
                );

            // Pokémon names underneath the sprites
            CurrentlyHuntedLabel.Text =
                string.IsNullOrWhiteSpace(huntSession.TargetPokemon)
                    ? "None"
                    : huntSession.TargetPokemon;

            CurrentEncounteredLabel.Text =
                string.IsNullOrWhiteSpace(huntSession.CurrentEncounter)
                    ? "None"
                    : huntSession.CurrentEncounter;

            PreviouslyEncounteredLabel.Text =
                string.IsNullOrWhiteSpace(huntSession.PreviousEncounter)
                    ? "None"
                    : huntSession.PreviousEncounter;

            Text =
                huntSession.IsRunning &&
                !string.IsNullOrWhiteSpace(huntSession.TargetPokemon)
                    ? $"Pro Tracker & Database - Hunting {huntSession.TargetPokemon}"
                    : "Pro Tracker & Database";
        }

        protected override void OnFormClosing(
            FormClosingEventArgs e)
        {
            if (huntSession.IsRunning)
            {
                huntSession.Pause();
            }

            SessionPersistenceService.Save(
                huntSession
            );

            base.OnFormClosing(e);
        }

        private static void SetPictureBoxPokemon(
    PictureBox pictureBox,
    string pokemonName)
        {
            pictureBox.Image?.Dispose();
            pictureBox.Image = null;

            if (string.IsNullOrWhiteSpace(pokemonName))
                return;

            pictureBox.Image =
                PokemonSpriteService.GetSprite(pokemonName);

            pictureBox.SizeMode =
                PictureBoxSizeMode.Zoom;
        }

        private void TestEncounterDetection()
        {
            using Bitmap? screenshot =
                ScreenCapture.CaptureProWindow();

            if (screenshot == null)
            {
                MessageBox.Show(
                    "PRO could not be captured."
                );

                return;
            }

            System.Diagnostics.Debug.WriteLine(
    "[MAIN] About to call EncounterDetector.TryDetectEncounter"
);

            if (EncounterDetector.TryDetectEncounter(
                    screenshot,
                    out string pokemon))
            {
                MessageBox.Show(
                    $"Detected Pokémon: {pokemon}",
                    "Encounter Test",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            else
            {
                MessageBox.Show(
                    "Battle found, but no Pokémon name was detected.",
                    "Encounter Test",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }
        private void panel4_Paint(object sender, PaintEventArgs e)
        {

        }

        private void pictureBox13_Click(object sender, EventArgs e)
        {

        }

        private void label2_Click(object sender, EventArgs e)
        {

        }

        private void Boss2Label_Click(object sender, EventArgs e)
        {

        }
        private void OpenBoss(
            string bossId,
            BossDifficulty difficulty)
        {
            BossTemplate bossForm =
                new BossTemplate(bossId, difficulty);

            bossForm.Show();
        }

        private void BossDifficultyMenuItem_Click(
            object sender,
            EventArgs e)
        {
            if (sender is not ToolStripMenuItem menuItem)
                return;

            string tagValue =
                menuItem.Tag?.ToString() ?? string.Empty;

            string[] parts = tagValue.Split('|');

            if (parts.Length != 2)
            {
                MessageBox.Show(
                    $"Invalid boss menu tag: {tagValue}",
                    "Boss Menu Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return;
            }

            string bossId = parts[0];

            if (!Enum.TryParse(
                parts[1],
                true,
                out BossDifficulty difficulty))
            {
                MessageBox.Show(
                    $"Invalid difficulty: {parts[1]}",
                    "Boss Menu Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return;
            }

            OpenBoss(bossId, difficulty);
        }

        private void ProTrackerandDatabase_Load(object sender, EventArgs e)
        {

        }

        private void brockToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void hArdToolStripMenuItem4_Click(object sender, EventArgs e)
        {

        }

        private void kantoToolStripMenuItem_Click(
            object sender,
            EventArgs e)
        {

            // KantoMapForm mapForm = new KantoMapForm();
            // mapForm.Show();

        }

        private void appearanceToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using AppearanceForm appearanceForm =
                new AppearanceForm();

            if (appearanceForm.ShowDialog(this) !=
                DialogResult.OK)
            {
                return;
            }

            ThemeManager.Reload();

            // Apply user's new theme.
            ThemeManager.ApplyToForm(this);

            // Then override ONLY dropdown text rendering.
            ApplyMenuRenderer();

            // Rebuild dynamic rows using the new font color.
            UpdateSessionEncounters();

            Refresh();
        }

        private void pinkanIslandToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Pinkan");
            form.Show();
        }

        private void bidoofToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Bidoof Day");
            form.Show();
        }

        private void pikachuToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Pikachu World Quest");
            form.Show();
        }

        private void shadowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Shadow");
            form.Show();
        }

        private void summerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Summer");
            form.Show();
        }

        private void halloweenToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Halloween");
            form.Show();
        }

        private void may4thToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("May4th");
            form.Show();
        }

        private void christmasToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Christmas");
            form.Show();
        }

        private void easterToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Easter");
            form.Show();
        }

        private void valentinesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("Valentines");
            form.Show();
        }

        private void aprilFoolsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var form = new Counterparts("April Fools");
            form.Show();
        }

        private void excavationsToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            var form = new ExcavationsForm();
            form.Show();
        }



        private void testToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Test Test = new Test();
            Test.Show(this);
        }

        private void saveDataToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void toolStripLabel2_Click(object sender, EventArgs e)
        {

        }

        private void TotalEncountersLabel_Click(object sender, EventArgs e)
        {

        }

        private void CompactModeButton_Click(
            object sender,
            EventArgs e)
        {
            var compactForm =
                new ProTrackerandDatabaseCompactMode(
                    huntSession,
                    encounterTracker
                );

            // Make the main form the owner.
            compactForm.Show(this);

            // Hide it, DON'T close it.
            Hide();
        }

        protected override void OnFormClosed(
    FormClosedEventArgs e)
        {
            // Remove our display-only event subscription.
            encounterTracker.EncounterDetected -=
                EncounterTracker_EncounterDetected;

            // Restore the main tracker if it still exists.
            if (Owner != null &&
                !Owner.IsDisposed)
            {
                Owner.Show();
                Owner.Activate();
            }

            base.OnFormClosed(e);
        }

        private void tableLayoutPanel2_Paint(object sender, PaintEventArgs e)
        {

        }

        private void bossesToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            BossCooldownForm BossCooldownForm = new BossCooldownForm();
            BossCooldownForm.Show(this);
        }

        private void interactiveMapsToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }
    }
}
