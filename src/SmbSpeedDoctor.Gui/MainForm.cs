using System;
using System.Windows.Forms;
using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;

namespace SmbSpeedDoctor.Gui;

public partial class MainForm : Form
{
    private Button _scanButton;
    private FlowLayoutPanel _resultsPanel;
    private Label _statusLabel;
    private bool _scanning;

    public MainForm()
    {
        InitializeComponent();
        SetupUi();
    }

    private void InitializeComponent()
    {
        this.Text = "SMB Speed Doctor";
        this.Size = new System.Drawing.Size(600, 400);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedSingle;
        this.MaximizeBox = false;
    }

    private void SetupUi()
    {
        _scanButton = new Button
        {
            Text = "MEDIR AGORA",
            Font = new System.Drawing.Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold),
            Size = new System.Drawing.Size(200, 60),
            Location = new System.Drawing.Point(200, 20)
        };
        _scanButton.Click += ScanButton_Click;
        this.Controls.Add(_scanButton);

        _statusLabel = new Label
        {
            Text = "Pronto para diagnosticar",
            Font = new System.Drawing.Font("Segoe UI", 10f),
            Location = new System.Drawing.Point(20, 90),
            Size = new System.Drawing.Size(560, 30)
        };
        this.Controls.Add(_statusLabel);

        _resultsPanel = new FlowLayoutPanel
        {
            Location = new System.Drawing.Point(20, 130),
            Size = new System.Drawing.Size(560, 240),
            AutoScroll = true,
            BackColor = Color.White
        };
        this.Controls.Add(_resultsPanel);
    }

    private async void ScanButton_Click(object sender, EventArgs e)
    {
        if (_scanning) return;
        _scanning = true;

        _scanButton.Enabled = false;
        _resultsPanel.Controls.Clear();
        _statusLabel.Text = "Coletando dados...";
        _statusLabel.ForeColor = System.Drawing.Color.Blue;

        try
        {
            var data = await System.Threading.Tasks.Task.Run(() => new WindowsScanner().Collect());
            var result = new DiagnosisEngine().Diagnose(data);

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
                    Text = string.Format("Recomenda\u00e7\u00e3o: {0}", rem.Title),
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
        }
        catch (Exception ex)
        {
            _statusLabel.Text = string.Format("Erro: {0}", ex.Message);
            _statusLabel.ForeColor = System.Drawing.Color.Red;
        }
        finally
        {
            _scanning = false;
            _scanButton.Enabled = true;
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
