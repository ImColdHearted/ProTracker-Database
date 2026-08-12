using Foot_Tracker.Models;
using Foot_Tracker.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Foot_Tracker
{
    public partial class AppearanceForm : Form
    {
        private bool _backgroundSelectedThisSession;

        private string? _pendingCustomImagePath;
        public AppearanceForm()
        {
            InitializeComponent();

            PreviewPictureBox1.Paint += PreviewBorder_Paint;
            PreviewPictureBox2.Paint += PreviewBorder_Paint;

            LoadBackgroundThumbnails();
            LoadCurrentSettings();

            ThemeManager.ApplyToForm(this);
        }

        private AppearanceSettings _workingSettings =
    AppearanceSettingsRepository.Load();

        private PictureBox? _selectedBackgroundBox;
        private void LoadBackgroundThumbnails()
        {
            SetThumbnail(
                BackgroundChoice1,
                "Midnight");

            SetThumbnail(
                BackgroundChoice2,
                "Blood");

            SetThumbnail(
                BackgroundChoice3,
                "Slate");

            SetThumbnail(
                BackgroundChoice4,
                "Pride");

            SetThumbnail(
                BackgroundChoice5,
                "Pink");

            SetThumbnail(
                BackgroundChoice6,
                "Violet");
        }

        private void SetThumbnail(
    PictureBox pictureBox,
    string backgroundId)
        {
            string fullPath = GetBackgroundPath(backgroundId);

            if (!File.Exists(fullPath))
                return;

            using Image source = Image.FromFile(fullPath);

            pictureBox.Image = new Bitmap(source);
            pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        }

        private static string GetBackgroundPath(
    string backgroundId)
        {
            return Path.Combine(
                AppContext.BaseDirectory,
                "SharedPokemonLibrary",
                "Assets",
                "Custom",
                $"{backgroundId}.png");
        }

        private void BackgroundChoice_Click(
    object sender,
    EventArgs e)
        {
            if (sender is not PictureBox pictureBox)
                return;

            string backgroundId =
                pictureBox.Tag?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(backgroundId))
                return;

            // Select the preset.
            _workingSettings.BackgroundId = "Pink";

            _workingSettings.BackgroundId = "Blood";

            _workingSettings.BackgroundId = "Midnight";

            _workingSettings.BackgroundId = "Violet";

            _workingSettings.BackgroundId = "Pride";

            _workingSettings.BackgroundId = "Slate";


            // Turn off custom-image mode.
            _workingSettings.BackgroundId = backgroundId;

            _workingSettings.UseCustomBackground = false;
            _workingSettings.CustomBackgroundPath = string.Empty;

            _pendingCustomImagePath = null;
            _backgroundSelectedThisSession = true;

            SelectBackgroundBox(pictureBox);
            UpdateBackgroundPreview();
        }
        private void SelectBackgroundBox(
    PictureBox selectedBox)
        {
            PictureBox[] choices =
            [
                BackgroundChoice1,
                BackgroundChoice2,
                BackgroundChoice3,
                BackgroundChoice4,
                BackgroundChoice5,
                BackgroundChoice6
            ];

            foreach (PictureBox choice in choices)
            {
                choice.BorderStyle =
                    choice == selectedBox
                        ? BorderStyle.Fixed3D
                        : BorderStyle.FixedSingle;
            }

            _selectedBackgroundBox = selectedBox;
        }

        private void LoadCurrentSettings()
        {
            _backgroundSelectedThisSession = false;

            ClearBackgroundSelection();

            PreviewPanel.BackgroundImage?.Dispose();
            PreviewPanel.BackgroundImage = null;

            _pendingCustomImagePath = null;
            _backgroundSelectedThisSession = false;

            UpdateTextPreview();
            RefreshBorderPreview();
        }

        private void ClearBackgroundSelection()
        {
            PictureBox[] choices =
            [
                BackgroundChoice1,
        BackgroundChoice2,
        BackgroundChoice3,
        BackgroundChoice4,
        BackgroundChoice5,
        BackgroundChoice6
            ];

            foreach (PictureBox choice in choices)
            {
                choice.BorderStyle = BorderStyle.FixedSingle;
            }

            _selectedBackgroundBox = null;
        }

        private void panel2_Paint(object sender, PaintEventArgs e)
        {

        }

        private void BorderColorButton_Click(object sender, EventArgs e)
        {
            using ColorDialog dialog = new();

            dialog.Color =
                Color.FromArgb(
                    _workingSettings.BorderColorArgb);

            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            _workingSettings.BorderColorArgb =
                dialog.Color.ToArgb();

            RefreshBorderPreview();
        }
        private void RefreshBorderPreview()
        {
            PreviewPictureBox1.Invalidate();
            PreviewPictureBox2.Invalidate();

            PreviewPictureBox1.Refresh();
            PreviewPictureBox2.Refresh();
        }
        private void PreviewBorder_Paint(
    object? sender,
    PaintEventArgs e)
        {
            if (sender is not Control control)
                return;

            Color borderColor =
                Color.FromArgb(_workingSettings.BorderColorArgb);

            using Pen pen = new(borderColor, 1);

            Rectangle borderRectangle = control.ClientRectangle;

            borderRectangle.Width -= 1;
            borderRectangle.Height -= 1;

            e.Graphics.DrawRectangle(
                pen,
                borderRectangle);
        }
        private void TextColorButton_Click(
            object sender,
            EventArgs e)
        {
            using ColorDialog dialog = new();

            dialog.Color =
                Color.FromArgb(
                    _workingSettings.TextColorArgb);

            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            _workingSettings.TextColorArgb =
                dialog.Color.ToArgb();

            UpdateTextPreview();
        }
        private void UpdateBackgroundPreview()
        {
            if (!_backgroundSelectedThisSession)
                return;

            string fullPath =
                ThemeManager.GetBackgroundPath(
                    _workingSettings.BackgroundId);

            PreviewPanel.BackgroundImage?.Dispose();
            PreviewPanel.BackgroundImage = null;

            if (!File.Exists(fullPath))
                return;

            using Image source = Image.FromFile(fullPath);

            PreviewPanel.BackgroundImage =
                new Bitmap(source);

            PreviewPanel.BackgroundImageLayout =
                ImageLayout.Stretch;
        }
        private void UpdateTextPreview()
        {
            Color textColor =
                Color.FromArgb(
                    _workingSettings.TextColorArgb);

            PreviewTitleLabel.ForeColor = textColor;
            PreviewTitleLabel.ForeColor = textColor;
        }
        private void SaveButton_Click(
            object sender,
            EventArgs e)
        {
            try
            {
                if (_workingSettings.UseCustomBackground &&
                    !string.IsNullOrWhiteSpace(_pendingCustomImagePath))
                {
                    _workingSettings.CustomBackgroundPath =
                        AppearanceSettingsRepository.SaveCustomBackground(
                            _pendingCustomImagePath);
                }

                AppearanceSettingsRepository.Save(_workingSettings);

                ThemeManager.Reload();

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"The appearance settings could not be saved.\n\n{ex.Message}",
                    "Appearance Save Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ClearBackgroundButton_Click(
            object sender,
            EventArgs e)
        {
            _workingSettings.UseCustomBackground = false;
            _workingSettings.CustomBackgroundPath = string.Empty;
            _workingSettings.BackgroundId = "Pride";

            _pendingCustomImagePath = null;
            _backgroundSelectedThisSession = false;

            ClearBackgroundSelection();

            PreviewPanel.BackgroundImage?.Dispose();
            PreviewPanel.BackgroundImage = null;
        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {

        }

        private void SelectCustomImageButton_Click(object sender, EventArgs e)
        {
            using OpenFileDialog dialog = new()
            {
                Title = "Select a custom background",
                Filter =
                    "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|" +
                    "PNG Files|*.png|" +
                    "JPEG Files|*.jpg;*.jpeg|" +
                    "Bitmap Files|*.bmp|" +
                    "All Files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            try
            {
                using Image testImage = Image.FromFile(dialog.FileName);

                _pendingCustomImagePath = dialog.FileName;

                _workingSettings.UseCustomBackground = true;
                _backgroundSelectedThisSession = true;

                ClearBackgroundSelection();
                UpdateCustomBackgroundPreview(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"The selected file could not be loaded as an image.\n\n{ex.Message}",
                    "Invalid Background Image",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        private void UpdateCustomBackgroundPreview(
    string imagePath)
        {
            PreviewPanel.BackgroundImage?.Dispose();
            PreviewPanel.BackgroundImage = null;

            if (!File.Exists(imagePath))
                return;

            using Image source = Image.FromFile(imagePath);

            PreviewPanel.BackgroundImage =
                new Bitmap(source);

            PreviewPanel.BackgroundImageLayout =
                ImageLayout.Stretch;

            if (Controls.Find(
                    "CustomBackgroundPictureBox",
                    true)
                .FirstOrDefault() is PictureBox customPreview)
            {
                customPreview.Image?.Dispose();
                customPreview.Image = new Bitmap(source);
                customPreview.SizeMode = PictureBoxSizeMode.Zoom;
                customPreview.Visible = true;
            }
        }

        private void AppearanceForm_Load(object sender, EventArgs e)
        {

        }
    }
}
