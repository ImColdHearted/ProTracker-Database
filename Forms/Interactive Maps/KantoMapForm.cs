using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Linq;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.Forms.Interactive_Maps
{
    public partial class KantoMapForm : Form
    {
        private RegionMapData? _kantoMap;
        private Button? _selectedRouteButton;

        public KantoMapForm()
        {
            InitializeComponent();

            LoadRouteList();

            ThemeManager.ApplyToForm(this);

            try
            {
                _kantoMap = RegionMapRepository.Load("kanto");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "Kanto Map Loading Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void MapMarker_Click(
            object sender,
            EventArgs e)
        {
            if (sender is not Control marker)
                return;

            LoadLocationFromTag(
                marker.Tag?.ToString());

            HighlightRouteFromTag(
                marker.Tag?.ToString());
        }

        private void LoadLocationPokemon(
            IReadOnlyList<RouteEncounter>? pokemonList = null)
        {
            const int maximumSlots = 15;

            pokemonList ??= Array.Empty<RouteEncounter>();

            for (int slotNumber = 1;
                 slotNumber <= maximumSlots;
                 slotNumber++)
            {
                int index = slotNumber - 1;

                string pictureBoxName =
                    $"LocationPokemon{slotNumber}";

                PictureBox? pictureBox =
                    FindPictureBox(pictureBoxName);

                if (pictureBox == null)
                    continue;

                if (index >= pokemonList.Count)
                {
                    ClearLocationPokemonSlot(pictureBox);
                    continue;
                }

                RouteEncounter encounter =
                    pokemonList[index];

                SpriteMapEntry? spriteEntry =
                    SpriteMapRepository.FindByName(
                        encounter.Pokemon);

                if (spriteEntry == null)
                {
                    SetPokemonPicture(
                        pictureBoxName,
                        0);

                    pictureBox.Tag = encounter;
                    continue;
                }

                SetPokemonPicture(
                    pictureBoxName,
                    spriteEntry.DexNumber);

                pictureBox.Tag = encounter;
                pictureBox.Visible = true;
            }
        }

        private static void ClearLocationPokemonSlot(
    PictureBox pictureBox)
        {
            pictureBox.Image?.Dispose();
            pictureBox.Image = null;
            pictureBox.Tag = null;
            pictureBox.Visible = false;
            pictureBox.BackColor = Color.Transparent;
        }

        private void DisplayRoute(
            List<RouteEncounterGroup> routeGroups)
        {
            if (routeGroups.Count == 0)
            {
                ClearLocation();
                return;
            }

            RouteEncounterGroup firstGroup =
                routeGroups[0];

            LocationNameLabel.Text =
                firstGroup.DisplayName;

            LocationTypeLabel.Text = "Route";

            LocationDescriptionLabel.Text =
                string.Join(
                    ", ",
                    routeGroups.Select(group =>
                        group.RequiresMembership
                            ? $"{group.Method} (Membership)"
                            : group.Method));

            List<RouteEncounter> allEncounters =
                routeGroups
                    .SelectMany(group => group.Encounters)
                    .ToList();

            LoadLocationPokemon(allEncounters);
        }
        private void ClearLocation()
        {
            LocationNameLabel.Text = "Unknown Location";
            LocationTypeLabel.Text = string.Empty;
            LocationDescriptionLabel.Text = string.Empty;

            LoadLocationPokemon(
                new List<RouteEncounter>());

            LoadNotables(
                new List<RouteNotableData>());
        }
        private void LoadNotables(
    IReadOnlyList<RouteNotableData> notables)
        {
            // Temporary placeholder.
            // We will populate the lower panel later.
        }

        private PictureBox? FindPictureBox(string controlName)
        {
            Control[] matches = Controls.Find(controlName, true);

            if (matches.Length == 0)
                return null;

            return matches[0] as PictureBox;
        }
        private void SetPokemonPicture(
            string controlName,
            int dexNumber)
        {
            PictureBox? pictureBox =
                FindPictureBox(controlName);

            if (pictureBox == null)
                return;

            pictureBox.Image?.Dispose();
            pictureBox.Image = null;

            string fullPath = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Sprites",
                $"{dexNumber}.png");

            if (!File.Exists(fullPath))
            {
                pictureBox.Visible = false;
                return;
            }

            using Image sourceImage =
                Image.FromFile(fullPath);

            pictureBox.Image =
                new Bitmap(sourceImage);

            pictureBox.SizeMode =
                PictureBoxSizeMode.Zoom;

            pictureBox.Visible = true;
        }
        private void LoadRouteList()
        {
            ListedRoutesFlowPanel.SuspendLayout();
            ListedRoutesFlowPanel.Controls.Clear();

            string regionFolder = Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Data",
                "Regions",
                "Kanto");

            if (!Directory.Exists(regionFolder))
            {
                MessageBox.Show(
                    $"The Kanto route folder could not be found:\n{regionFolder}",
                    "Route List Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            string[] routeFiles = Directory.GetFiles(
                regionFolder,
                "Route*.json");

            foreach (string filePath in routeFiles
                         .OrderBy(GetRouteNumber))
            {
                string fileName = Path.GetFileName(filePath);
                string displayName =
                    Path.GetFileNameWithoutExtension(filePath);

                Button routeButton = new()
                {
                    Name = $"RouteButton_{displayName}",
                    Text = FormatRouteName(displayName),
                    Tag = $"Kanto|{fileName}",
                    Width = ListedRoutesFlowPanel.ClientSize.Width - 50,
                    Height = 36,
                    BackColor = ThemeManager.Current.BorderColor,
                    ForeColor = Color.Black,
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(3)
                };

                routeButton.FlatAppearance.BorderSize = 1;
                routeButton.Click += RouteListButton_Click;

                ListedRoutesFlowPanel.Controls.Add(routeButton);
            }

            ListedRoutesFlowPanel.ResumeLayout();
        }

        private static int GetRouteNumber(string filePath)
        {
            string name =
                Path.GetFileNameWithoutExtension(filePath);

            string numberText =
                new string(name
                    .Where(char.IsDigit)
                    .ToArray());

            return int.TryParse(numberText, out int number)
                ? number
                : int.MaxValue;
        }

        private static string FormatRouteName(
    string fileNameWithoutExtension)
        {
            string numberText =
                new string(fileNameWithoutExtension
                    .Where(char.IsDigit)
                    .ToArray());

            return string.IsNullOrWhiteSpace(numberText)
                ? fileNameWithoutExtension
                : $"Route {numberText}";
        }
        private void RouteListButton_Click(
    object? sender,
    EventArgs e)
        {
            if (sender is not Button routeButton)
                return;

            LoadLocationFromTag(
                routeButton.Tag?.ToString());

            HighlightSelectedRouteButton(routeButton);
        }

        private void LoadLocationFromTag(string? tagValue)
        {
            if (string.IsNullOrWhiteSpace(tagValue))
                return;

            string[] parts = tagValue.Split('|');

            if (parts.Length != 2)
            {
                MessageBox.Show(
                    $"Invalid location tag: {tagValue}",
                    "Map Configuration Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            string region = parts[0];
            string fileName = parts[1];

            try
            {
                List<RouteEncounterGroup> routeGroups =
                    RouteRepository.Load(
                        region,
                        fileName);

                DisplayRoute(routeGroups);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "Route Loading Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void HighlightSelectedRouteButton(
    Button selectedButton)
        {
            if (_selectedRouteButton != null)
            {
                _selectedRouteButton.BackColor =
                    Color.White;

                _selectedRouteButton.ForeColor =
                    Color.Black;
            }

            _selectedRouteButton = selectedButton;

            selectedButton.BackColor =
                ThemeManager.Current.BorderColor;

            selectedButton.ForeColor =
                GetReadableTextColor(
                    ThemeManager.Current.BorderColor);
        }

        private static Color GetReadableTextColor(
    Color background)
        {
            double brightness =
                (background.R * 0.299) +
                (background.G * 0.587) +
                (background.B * 0.114);

            return brightness > 140
                ? Color.Black
                : Color.White;
        }

        private void HighlightRouteFromTag(
    string? tagValue)
        {
            if (string.IsNullOrWhiteSpace(tagValue))
                return;

            Button? matchingButton =
                ListedRoutesFlowPanel.Controls
                    .OfType<Button>()
                    .FirstOrDefault(button =>
                        string.Equals(
                            button.Tag?.ToString(),
                            tagValue,
                            StringComparison.OrdinalIgnoreCase));

            if (matchingButton != null)
                HighlightSelectedRouteButton(matchingButton);
        }
    }
}
