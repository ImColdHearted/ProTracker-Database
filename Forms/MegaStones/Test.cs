using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Foot_Tracker.Forms.Guides.MegaStones
{
    public partial class Test : Form
    {
        public Test()
        {
            InitializeComponent();
        }

        private async void MegaStones_Load(
            object sender,
            EventArgs e)
        {
            await InitializeGuideBrowserAsync();

            webGuide.Source =
                new Uri(
                    "https://guides.local/Test/index.html"
                );
        }

        private async Task InitializeGuideBrowserAsync()
        {
            await webGuide.EnsureCoreWebView2Async();

            string guideRoot = Path.Combine(
                AppContext.BaseDirectory,
                "DataFiles",
                "Guides"
            );

            string sharedRoot = Path.Combine(
    AppContext.BaseDirectory,
    "SharedPokemonLibrary"
);

            if (!Directory.Exists(guideRoot))
            {
                MessageBox.Show(
                    $"Guide directory was not found:\n{guideRoot}",
                    "Missing Guide Folder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );

                return;
            }

            webGuide.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "guides.local",
                guideRoot,
                CoreWebView2HostResourceAccessKind.Allow
            );

            webGuide.CoreWebView2.SetVirtualHostNameToFolderMapping(
    "assets.local",
    sharedRoot,
    CoreWebView2HostResourceAccessKind.Allow
);

            ConfigureGuideNavigation();
        }

        private void ConfigureGuideNavigation()
        {
            webGuide.CoreWebView2.NavigationStarting +=
                (_, e) =>
                {
                    bool isGuide =
                        e.Uri.StartsWith(
                            "https://guides.local/",
                            StringComparison.OrdinalIgnoreCase);

                    bool isAsset =
                        e.Uri.StartsWith(
                            "https://assets.local/",
                            StringComparison.OrdinalIgnoreCase);

                    if (!isGuide && !isAsset)
                    {
                        e.Cancel = true;
                    }
                };
        }
    }
    }

