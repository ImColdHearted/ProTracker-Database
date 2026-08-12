using Foot_Tracker.Forms.BossTemplate;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Foot_Tracker
{
    public partial class BossTemplate : Form
    {
        private readonly string _bossId;
        private readonly BossDifficulty _difficulty;


        public BossTemplate(
            string bossId,
            BossDifficulty difficulty)
        {
            InitializeComponent();

            _bossId = bossId;
            _difficulty = difficulty;

            LoadBoss(_bossId, _difficulty);

            ThemeManager.ApplyToForm(this);
        }

        private void BossTemplate_Load(
            object sender,
            EventArgs e)
        {
            LoadBoss(_bossId, _difficulty);
        }

        private void LoadBoss(
            string bossId,
            BossDifficulty difficulty)
        {
            try
            {
                BossData boss = BossRepository.Load(bossId);

                string difficultyKey =
                    difficulty.ToString().ToLowerInvariant();

                if (!boss.Difficulties.TryGetValue(
                    difficultyKey,
                    out BossDifficultyData? selectedDifficulty))
                {
                    MessageBox.Show(
                        $"Difficulty '{difficulty}' was not found for {boss.Name}.");

                    return;
                }

                BossNameValueLabel.Text =
                    $"{boss.Name} ({difficulty})";

                BossLocation.Text =
                    boss.Location;

                string locationPicturePath =
    !string.IsNullOrWhiteSpace(boss.LocationPicture)
        ? boss.LocationPicture
        : boss.LocationImage;

                SetPictureFromPath(
                    "BossLocationPicture",
                    locationPicturePath);

                BossRequirement.Text =
                    !string.IsNullOrWhiteSpace(boss.Requirement)
                        ? boss.Requirement
                        : boss.Requirements;

                BossPokemonPokedollars.Text =
                    $"{selectedDifficulty.Rewards.Pokedollars.Minimum:N0} - " +
                    $"{selectedDifficulty.Rewards.Pokedollars.Maximum:N0}";

                LoadItemRewards(selectedDifficulty);
                LoadPokemonRewards(selectedDifficulty);
                LoadBossTeam(selectedDifficulty);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "Boss Loading Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void LoadItemRewards(
    BossDifficultyData selectedDifficulty)
        {
            const int maximumItemRewards = 12;

            for (int rewardNumber = 1;
                 rewardNumber <= maximumItemRewards;
                 rewardNumber++)
            {
                int rewardIndex = rewardNumber - 1;

                string controlName =
                    $"BossItemReward{rewardNumber}";

                if (rewardIndex >=
                    selectedDifficulty.Rewards.Items.Count)
                {
                    ClearPicture(controlName);
                    continue;
                }

                BossItemReward reward =
                    selectedDifficulty.Rewards.Items[rewardIndex];

                SetPictureFromPath(
                    controlName,
                    reward.Picture);
            }
        }
        private void ClearPicture(string controlName)
        {
            PictureBox? pictureBox = FindPictureBox(controlName);

            if (pictureBox == null)
                return;

            pictureBox.Image?.Dispose();
            pictureBox.Image = null;
            pictureBox.Visible = false;
        }
        private void LoadBossTeam(
BossDifficultyData selectedDifficulty)
        {
            for (int pokemonNumber = 1; pokemonNumber <= 6; pokemonNumber++)
            {
                int pokemonIndex = pokemonNumber - 1;

                if (pokemonIndex >= selectedDifficulty.Team.Count)
                {
                    ClearPokemonSlot(pokemonNumber);
                    continue;
                }

                BossPokemonData pokemon =
                    selectedDifficulty.Team[pokemonIndex];

                SetControlText(
                    $"BossPokemonName{pokemonNumber}",
                    pokemon.Name);

                SetPokemonPicture(
                    $"BossPokemonPicture{pokemonNumber}",
                    pokemon.DexNumber);

                SetControlText(
                    $"BossPokemonNature{pokemonNumber}",
                    pokemon.Nature);

                SetControlText(
                    $"BossPokemonAbility{pokemonNumber}",
                    pokemon.Ability);

                SetControlText(
                    $"BossPokemonItem{pokemonNumber}",
                    pokemon.Item);

                for (int moveNumber = 1; moveNumber <= 7; moveNumber++)
                {
                    int moveIndex = moveNumber - 1;

                    string move =
                        moveIndex < pokemon.Moves.Count
                            ? pokemon.Moves[moveIndex]
                            : string.Empty;

                    SetControlText(
                        $"BossPokemon{pokemonNumber}Move{moveNumber}",
                        move);
                }
            }
        }
        private string GetPokemonSpritePath(
    int dexNumber,
    string pictureOverride)
        {
            if (!string.IsNullOrWhiteSpace(pictureOverride))
                return pictureOverride;

            if (dexNumber < 0)
                return string.Empty;

            return Path.Combine(
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                $"{dexNumber}.png");
        }
        private void LoadPokemonRewards(
    BossDifficultyData selectedDifficulty)
        {
            const int maximumPokemonRewards = 14;

            for (int rewardNumber = 1;
                 rewardNumber <= maximumPokemonRewards;
                 rewardNumber++)
            {
                int rewardIndex = rewardNumber - 1;

                string controlName =
                    $"BossPokemonReward{rewardNumber}";

                if (rewardIndex >=
                    selectedDifficulty.Rewards.Pokemon.Count)
                {
                    ClearPicture(controlName);
                    continue;
                }

                BossPokemonReward reward =
                    selectedDifficulty.Rewards.Pokemon[rewardIndex];

                string spritePath = GetPokemonSpritePath(
                    reward.DexNumber,
                    reward.Picture);

                SetPictureFromPath(
                    controlName,
                    spritePath);
            }
        }
        private void SetPokemonPicture(
            string controlName,
            int dexNumber)
        {
            PictureBox? pictureBox = FindPictureBox(controlName);

            if (pictureBox == null)
                return;

            pictureBox.Image?.Dispose();
            pictureBox.Image = null;

            if (dexNumber <= 0)
                return;

            string fullPath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                $"{dexNumber}.png");

            if (!File.Exists(fullPath))
                return;

            using Image sourceImage = Image.FromFile(fullPath);

            pictureBox.Image = new Bitmap(sourceImage);
            pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        }
        private PictureBox? FindPictureBox(string controlName)
        {
            Control[] matches = Controls.Find(controlName, true);

            if (matches.Length == 0)
                return null;

            return matches[0] as PictureBox;
        }
        private bool SetPictureFromPath(
    string controlName,
    string relativePath)
        {
            PictureBox? pictureBox = FindPictureBox(controlName);

            if (pictureBox == null)
            {
                MessageBox.Show(
                    $"PictureBox not found: {controlName}",
                    "Control Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return false;
            }

            pictureBox.Image?.Dispose();
            pictureBox.Image = null;

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                pictureBox.Visible = false;
                return false;
            }

            string normalizedPath = relativePath.Replace(
                '/',
                Path.DirectorySeparatorChar);

            string fullPath = Path.Combine(
                AppContext.BaseDirectory,
                normalizedPath);

            if (!File.Exists(fullPath))
            {
                Debug.WriteLine(
                    $"Image not found: {fullPath}");

                pictureBox.Visible = false;
                return false;
            }

            using Image sourceImage = Image.FromFile(fullPath);

            pictureBox.Image = new Bitmap(sourceImage);
            pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBox.Visible = true;

            return true;
        }
        private Control? FindControlByName(string controlName)
        {
            Control[] matches = Controls.Find(controlName, true);

            return matches.Length > 0
                ? matches[0]
                : null;
        }

        private void SetControlText(
            string controlName,
            string value)
        {
            Control? control = FindControlByName(controlName);

            if (control == null)
                return;

            control.Text = value;
        }
        private void ClearPokemonSlot(int pokemonNumber)
        {
            SetControlText(
                $"BossPokemonName{pokemonNumber}",
                string.Empty);

            SetControlText(
                $"BossPokemonNature{pokemonNumber}",
                string.Empty);

            SetPokemonPicture(
                $"BossPokemonPicture{pokemonNumber}",
                0);

            SetControlText(
                $"BossPokemonAbility{pokemonNumber}",
                string.Empty);

            SetControlText(
                $"BossPokemonItem{pokemonNumber}",
                string.Empty);

            for (int moveNumber = 1; moveNumber <= 7; moveNumber++)
            {
                SetControlText(
                    $"BossPokemon{pokemonNumber}Move{moveNumber}",
                    string.Empty);
            }
        }
        public class GradientPanel : Panel
        {
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using LinearGradientBrush brush =
                    new LinearGradientBrush(
                        ClientRectangle,
                        Color.FromArgb(28, 55, 105),
                        Color.Black,
                        LinearGradientMode.Vertical);

                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        private void label7_Click(object sender, EventArgs e)
        {

        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {

        }

        private void panel1_Paint(object sender, PaintEventArgs e)
        {

        }

        private void panel1_Paint_1(object sender, PaintEventArgs e)
        {

        }

        private void tableLayoutPanel2_Paint(object sender, PaintEventArgs e)
        {

        }

        private void label72_Click(object sender, EventArgs e)
        {

        }

        private void BossName_Click(object sender, EventArgs e)
        {

        }

        private void BossLocation_Click(object sender, EventArgs e)
        {

        }

        private void BossTemplate_Load_1(object sender, EventArgs e)
        {

        }

        private void tableLayoutPanel4_Paint(object sender, PaintEventArgs e)
        {

        }

        private void BossPokemonName1_Click(object sender, EventArgs e)
        {

        }

        private void toolStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void label10_Click(object sender, EventArgs e)
        {

        }

        private void BossLocationPicture_Click(object sender, EventArgs e)
        {

        }

        private void BossNameLabel_Click(object sender, EventArgs e)
        {

        }

        private void label16_Click(object sender, EventArgs e)
        {

        }

        private void BossPokemonPicture2_Click(object sender, EventArgs e)
        {

        }

        private void panel8_Paint(object sender, PaintEventArgs e)
        {

        }
    }
}
