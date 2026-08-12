using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace Foot_Tracker
{
    public partial class ProTrackerandDatabaseCompactMode : Form
    {
        private readonly HuntSession huntSession;
        private readonly EncounterTracker encounterTracker;
        private readonly System.Windows.Forms.Timer compactTimer =
    new System.Windows.Forms.Timer();
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(
            IntPtr hWnd,
            int Msg,
            IntPtr wParam,
            IntPtr lParam
        );

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;
        public ProTrackerandDatabaseCompactMode(
            HuntSession huntSession,
            EncounterTracker encounterTracker)
        {
            InitializeComponent();
            ThemeManager.ApplyToForm(this);

            encounterTracker.EncounterDetected +=
            EncounterTracker_EncounterDetected;

            this.huntSession = huntSession;
            this.encounterTracker = encounterTracker;

            compactTimer.Interval = 1000;
            compactTimer.Tick += CompactTimer_Tick;
            compactTimer.Start();

            UpdateCompactDisplay();

        }
        private void CompactTimer_Tick(
    object? sender,
    EventArgs e)
        {
            UpdateCompactTimer();
        }
        private void UpdateCompactTimer()
        {
            CompactTimerLabel.Text =
                huntSession
                    .GetCurrentElapsedTime()
                    .ToString(@"hh\:mm\:ss");
        }
        private void PlayButtonCompact_Click(
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

            huntSession.Start();

            encounterTracker.Start();

            UpdateCompactDisplay();
        }

        private async void PauseButtonCompact_Click(
            object sender,
            EventArgs e)
        {
            huntSession.Pause();

            await encounterTracker.StopAsync();

            UpdateCompactDisplay();
        }

        private async void ResetButtonCompact_Click(
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

            await encounterTracker.StopAsync();

            huntSession.Reset();

            UpdateCompactDisplay();
        }

        private void SelectPokemonCompact_Click(
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

            UpdateCompactDisplay();
        }

        private void UpdateCompactDisplay()
        {
            // TARGET POKEMON
            // Species-only lookup because alternate forms aren't directly huntable.
            SetCompactPictureBoxPokemon(
                CurrentlyHuntingCompact,
                huntSession.TargetPokemon,
                true
            );

            // CURRENT ENCOUNTER
            // Form-aware lookup for Alolan/Galarian/Hisuian/etc.
            SetCompactPictureBoxPokemon(
                CurrentEncounterCompact,
                huntSession.CurrentEncounter,
                true
            );

            // PREVIOUS ENCOUNTER
            // Form-aware lookup for Alolan/Galarian/Hisuian/etc.
            SetCompactPictureBoxPokemon(
                PreviousEncounterCompact,
                huntSession.PreviousEncounter,
                true
            );

            // Labels
            CurrentlyHuntedCompactLabel.Text =
                string.IsNullOrWhiteSpace(huntSession.TargetPokemon)
                    ? "None"
                    : huntSession.TargetPokemon;

            CurrentEncounteredCompactLabel.Text =
                string.IsNullOrWhiteSpace(huntSession.CurrentEncounter)
                    ? "None"
                    : huntSession.CurrentEncounter;

            PreviouslyEncounteredCompactLabel.Text =
                string.IsNullOrWhiteSpace(huntSession.PreviousEncounter)
                    ? "None"
                    : huntSession.PreviousEncounter;

            UpdateCompactTimer();
        }

        private static void SetCompactPictureBoxPokemon(
            PictureBox pictureBox,
            string pokemonName,
            bool encounterSprite)
        {
            pictureBox.Image?.Dispose();
            pictureBox.Image = null;

            if (string.IsNullOrWhiteSpace(pokemonName))
                return;

            pictureBox.Image =
                encounterSprite
                    ? PokemonSpriteService.GetEncounterSprite(pokemonName)
                    : PokemonSpriteService.GetSprite(pokemonName);

            pictureBox.SizeMode =
                PictureBoxSizeMode.Zoom;
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

            // DON'T RegisterEncounter here.
            UpdateCompactDisplay();
        }

        protected override void OnFormClosed(
            FormClosedEventArgs e)
        {
            compactTimer.Stop();
            compactTimer.Dispose();

            encounterTracker.EncounterDetected -=
                EncounterTracker_EncounterDetected;

            base.OnFormClosed(e);
        }

        private void CurrentlyHunting_Click(object sender, EventArgs e)
        {

        }

        private void StickyModeCompact_Click(
            object sender,
            EventArgs e)
        {
            TopMost = !TopMost;

            StickyModeCompact.Checked = TopMost;
        }

        private void StickyModeCompact_CheckedChanged(
    object sender,
    EventArgs e)
        {
            TopMost = StickyModeCompact.Checked;
        }

        private void CloseCompactButton_Click(
            object sender,
            EventArgs e)
        {
            if (Owner != null)
            {
                Owner.Show();
                Owner.Activate();
            }

            Close();
        }

        private void DragHandleCompact_MouseDown(
    object sender,
    MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            ReleaseCapture();

            SendMessage(
                Handle,
                WM_NCLBUTTONDOWN,
                (IntPtr)HTCAPTION,
                IntPtr.Zero
            );
        }

        private void ProTrackerandDatabaseCompactMode_Load(object sender, EventArgs e)
        {

        }
    }
}
