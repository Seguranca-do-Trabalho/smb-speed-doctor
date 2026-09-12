using System;
using System.Windows.Forms;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;

namespace SmbSpeedDoctor.Gui;

public partial class MainForm : Form
{
    private Button _scanButton;
    private Button _browseButton;
    private TextBox _pathBox;
    private FlowLayoutPanel _resultsPanel;
    private Label _statusLabel;
    private Label _speedLabel;
    private Label _detailLabel;
    private bool _scanning;

    public MainForm()
    {
        InitializeComponent();
        SetupUi();
    }

    private void InitializeComponent()
    {
        this.Text = "SMB Speed Doctor";
        this.Size = new System.Drawing.Size(620, 600);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
    }

    private void SetupUi()
    {
        // Path input: without target there is no test copy and therefore no
        // throughput measurement — diagnosis is partial.
        var pathLabel = new Label
        {
            Text = @"Share to measure (\\server\share or local folder):",
            Font = new System.Drawing.Font("Segoe UI", 9f),
            Location = new System.Drawing.Point(20, 15),
            Size = new System.Drawing.Size(568, 18)
        };
        this.Controls.Add(pathLabel);

        _pathBox = new TextBox
        {
            Font = new System.Drawing.Font("Segoe UI", 10f),
            Location = new System.Drawing.Point(20, 36),
            Size = new System.Drawing.Size(480, 25),
            PlaceholderText = @"\\server\share   (empty = partial scan, without measuring throughput)"
        };
        this.Controls.Add(_pathBox);

        _browseButton = new Button
        {
            Text = "Browse…",
            Font = new System.Drawing.Font("Segoe UI", 9f),
            Location = new System.Drawing.Point(508, 35),
            Size = new System.Drawing.Size(80, 27)
        };
        _browseButton.Click += BrowseButton_Click;
        this.Controls.Add(_browseButton);

        _scanButton = new Button
        {
            Text = "MEASURE NOW",
            Font = new System.Drawing.Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold),
            Size = new System.Drawing.Size(568, 50),
            Location = new System.Drawing.Point(20, 72)
        };
        _scanButton.Click += ScanButton_Click;
        this.Controls.Add(_scanButton);

        _statusLabel = new Label
        {
            Text = "Ready to diagnose",
            Font = new System.Drawing.Font("Segoe UI", 10f),
            Location = new System.Drawing.Point(20, 132),
            Size = new System.Drawing.Size(568, 42)
        };
        this.Controls.Add(_statusLabel);

        // Highlighted MEASUREMENT
        _speedLabel = new Label
        {
            Text = "—",
            Font = new System.Drawing.Font("Segoe UI", 15f, System.Drawing.FontStyle.Bold),
            Location = new System.Drawing.Point(20, 178),
            Size = new System.Drawing.Size(568, 30),
            ForeColor = System.Drawing.Color.DimGray
        };
        this.Controls.Add(_speedLabel);

        _detailLabel = new Label
        {
            Text = "",
            Font = new System.Drawing.Font("Segoe UI", 8.5f),
            Location = new System.Drawing.Point(20, 208),
            Size = new System.Drawing.Size(568, 20),
            ForeColor = System.Drawing.Color.DimGray
        };
        this.Controls.Add(_detailLabel);

        _resultsPanel = new FlowLayoutPanel
        {
            Location = new System.Drawing.Point(20, 234),
            Size = new System.Drawing.Size(568, 306),
            AutoScroll = true,
            BackColor = Color.White
        };
        this.Controls.Add(_resultsPanel);
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024 ? string.Format("{0:N0} MB", bytes / (1024 * 1024))
         : bytes >= 1024        ? string.Format("{0:N0} KB", bytes / 1024)
         : string.Format("{0:N0} B", bytes);

    private void BrowseButton_Click(object sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose the share or folder to measure",
            ShowNewFolderButton = false
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _pathBox.Text = dlg.SelectedPath;
    }

    private async void ScanButton_Click(object sender, EventArgs e)
    {
        if (_scanning) return;
        _scanning = true;

        _scanButton.Enabled = false;
        _browseButton.Enabled = false;
        _resultsPanel.Controls.Clear();
        _speedLabel.Text = "—";
        _speedLabel.ForeColor = System.Drawing.Color.DimGray;
        _detailLabel.Text = "";

        string sharePath = (_pathBox.Text ?? string.Empty).Trim();
        _statusLabel.Text = sharePath.Length > 0
            ? string.Format("Measuring with test copy at {0}… (may take a few seconds)", sharePath)
            : "Collecting data… without a path specified, throughput will NOT be measured.";
        _statusLabel.ForeColor = System.Drawing.Color.Blue;

        try
        {
            var scanner = new WindowsScanner(sharePath: sharePath);
            var data = await System.Threading.Tasks.Task.Run(() => scanner.Collect());
            var result = new DiagnosisEngine().Diagnose(data);

            // --- The measurement, highlighted ---
            if (data.ThroughputQuality == MeasurementQuality.Unavailable)
            {
                _speedLabel.Text = "Speed not measured";
                _speedLabel.ForeColor = System.Drawing.Color.DarkOrange;
                _detailLabel.Text = "Specify a share to run the test copy.";
            }
            else
            {
                double mbps = data.ObservedCopyThroughputBps / 1_000_000.0;
                string origin = data.ThroughputQuality == MeasurementQuality.Measured
                    ? "real test copy"
                    : "network traffic estimation";
                _speedLabel.Text = string.Format("{0:N2} MB/s", mbps);
                _speedLabel.ForeColor = System.Drawing.Color.FromArgb(0, 90, 156);
                _detailLabel.Text = string.Format(
                    "{0}  ·  latency {1:N1} ms  ·  link {2:N0} Mb/s  ·  {3:N0} files (avg {4})",
                    origin, data.LatencyMs, data.LinkSpeedBps / 1_000_000.0,
                    data.FileCount, FormatBytes((long)data.AverageFileBytes));
            }

            _statusLabel.Text = result.OneLineSummary;
            _statusLabel.ForeColor = result.Severity switch
            {
                Severity.Ok => System.Drawing.Color.Green,
                Severity.Warning => System.Drawing.Color.Orange,
                Severity.Critical => System.Drawing.Color.Red,
                _ => System.Drawing.Color.Black
            };

            foreach (var finding in result.Findings)
            {
                var item = new Panel
                {
                    Size = new System.Drawing.Size(540, 40),
                    BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                    Margin = new Padding(0, 0, 0, 5)
                };

                var icon = new Label
                {
                    Text = finding.Severity switch
                    {
                        Severity.Ok => "\u2713",
                        Severity.Warning => "\u26a0",
                        Severity.Critical => "\u2717",
                        _ => "?"
                    },
                    ForeColor = finding.Severity switch
                    {
                        Severity.Ok => System.Drawing.Color.Green,
                        Severity.Warning => System.Drawing.Color.Orange,
                        Severity.Critical => System.Drawing.Color.Red,
                        _ => System.Drawing.Color.Gray
                    },
                    Font = new System.Drawing.Font("Segoe UI", 12f, System.Drawing.FontStyle.Bold),
                    Location = new System.Drawing.Point(5, 10),
                    Size = new System.Drawing.Size(30, 20)
                };
                item.Controls.Add(icon);

                var text = new Label
                {
                    Text = string.Format("{0}: {1} = {2}", finding.Layer, finding.Metric, finding.Value),
                    Font = new System.Drawing.Font("Segoe UI", 9f),
                    Location = new System.Drawing.Point(40, 10),
                    Size = new System.Drawing.Size(490, 20)
                };
                item.Controls.Add(text);

                _resultsPanel.Controls.Add(item);
            }

            if (result.RecommendedRemediation != null)
            {
                var rem = result.RecommendedRemediation;
                var remPanel = new Panel
                {
                    Size = new System.Drawing.Size(540, 80),
                    BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                    BackColor = System.Drawing.Color.LightYellow,
                    Margin = new Padding(0, 0, 0, 5)
                };

                var remTitle = new Label
                {
                    Text = string.Format("Recommendation: {0}", rem.Title),
                    Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold),
                    Location = new System.Drawing.Point(10, 5),
                    Size = new System.Drawing.Size(520, 20)
                };
                remPanel.Controls.Add(remTitle);

                var remDesc = new Label
                {
                    Text = rem.Description,
                    Font = new System.Drawing.Font("Segoe UI", 9f),
                    Location = new System.Drawing.Point(10, 28),
                    Size = new System.Drawing.Size(520, 50)
                };
                remPanel.Controls.Add(remDesc);

                _resultsPanel.Controls.Add(remPanel);
            }

            // Collection notes
            foreach (var note in scanner.CollectionErrors)
            {
                var notePanel = new Panel
                {
                    Size = new System.Drawing.Size(540, 54),
                    BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                    BackColor = System.Drawing.Color.FromArgb(245, 245, 245),
                    Margin = new Padding(0, 0, 0, 5)
                };
                notePanel.Controls.Add(new Label
                {
                    Text = "note: " + note,
                    Font = new System.Drawing.Font("Segoe UI", 8f),
                    ForeColor = System.Drawing.Color.DimGray,
                    Location = new System.Drawing.Point(8, 5),
                    Size = new System.Drawing.Size(524, 44)
                });
                _resultsPanel.Controls.Add(notePanel);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = string.Format("Error: {0}", ex.Message);
            _statusLabel.ForeColor = System.Drawing.Color.Red;
        }
        finally
        {
            _scanning = false;
            _scanButton.Enabled = true;
            _browseButton.Enabled = true;
        }
    }
}

public static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
