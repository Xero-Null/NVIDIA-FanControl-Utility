using System.Runtime.InteropServices;
using NVIDIAFanControlUtility.Controls;
using NVIDIAFanControlUtility.Models;
using NVIDIAFanControlUtility.Services;
using Timer = System.Windows.Forms.Timer; // disambiguate from System.Threading.Timer

namespace NVIDIAFanControlUtility;

/// <summary>
/// The main window: header, a FlowLayoutPanel of GPU cards, a status bar,
/// and a timer that re-runs nvidia-smi on an interval.
/// </summary>
public sealed class MainForm : Form
{
    private const int DwmAttributeUseDarkMode = 20; // DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 1809+)

    private readonly NvSmiService _service = new();
    private readonly List<GpuPanel> _panels = new();

    private readonly FlowLayoutPanel _contentPanel;
    private readonly TabStrip _tabStrip;
    private readonly Panel _tabContent;
    private readonly FlowLayoutPanel _optionsPage;
    private readonly Label _statusLabel;
    private readonly Button _refreshButton;
    private readonly Timer _timer;
    private readonly ToolTip _toolTip = new();
    private readonly TextBox _pollInput;
    private readonly Label _pollHint;

    // ── Fan Control integration (Options tab) ─────────────────────────────
    // Assigned in BuildFanControlSection() (called from the constructor, before
    // the form is shown), so these are not readonly — C# only allows readonly
    // field writes from the constructor itself.
    private readonly FanControlService _fanControl = new();
    private readonly FanControlSettings _fanSettings = SettingsStore.Load();
    private CheckBox _fanEnable = null!;
    private TextBox _fanExePath = null!;
    private Button _fanBrowseButton = null!;
    private FlowLayoutPanel _fanProfilesPanel = null!;
    private Button _fanAddButton = null!;
    private Label _fanHint = null!;
    private Label _fanStatus = null!;

    /// <summary>Temp/name input pairs, one per visible profile row.</summary>
    private readonly List<TextBox> _fanTempInputs = new();
    private readonly List<TextBox> _fanNameInputs = new();

    /// <summary>True while the code itself is writing to the Fan Control inputs
    /// (so the resulting TextChanged events don't re-enter commit logic).</summary>
    private bool _fanLoading;

    /// <summary>Fingerprint of the last ladder applied to the service. Prevents a
    /// keystroke that doesn't change the effective ladder from resetting it (which
    /// would make the next refresh re-launch Fan Control needlessly).</summary>
    private string _fanLadderSignature = string.Empty;

    private ErrorCard? _errorPanel;
    private bool _busy;

    /// <summary>Accepted poll rate bounds (seconds).</summary>
    private const int MinPollSeconds = 1;
    private const int MaxPollSeconds = 60;

    private int _pollSeconds = 5; // last accepted value; the input reverts to this

    // Header fonts are shared between the wide/compact layouts and swapped in
    // UpdateHeader; they live for the form's lifetime and are never disposed.
    private readonly Font _headerFont = new("Segoe UI", 15f, FontStyle.Bold);
    private readonly Font _headerCompactFont = new("Segoe UI", 11f, FontStyle.Bold);

    public MainForm()
    {
        Text = "NVIDIA Fan Control Utility";
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;
        StartPosition = FormStartPosition.CenterScreen;
        // The UI adapts down to a very narrow window (compact cards, compact
        // tabs, wrapping options rows), so the floor is low: at ~170px the
        // status bar plus one slim GPU card stay fully usable.
        MinimumSize = new Size(170, 320);
        ClientSize = new Size(1080, 680);
        Font = new Font("Segoe UI", 9f);

        _statusLabel = new Label
        {
            Text = "Starting…",
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(12, 0, 12, 0),
            Width = 640,
            Height = 44,
        };

        _refreshButton = new Button
        {
            Text = "Refresh",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelBackground,
            ForeColor = Theme.NvGreen,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Size = new Size(96, 32),
            Cursor = Cursors.Hand,
        };
        _refreshButton.FlatAppearance.BorderColor = Theme.PanelBorder;
        _refreshButton.FlatAppearance.MouseOverBackColor = Theme.PanelBorder;
        _refreshButton.Click += async (_, _) => await RefreshAsync();

        var statusBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            BackColor = Theme.WindowBackground,
            Padding = new Padding(0, 0, 12, 0),
        };
        // Index 0 lays out first in a FlowLayoutPanel, so the status text
        // sits on the left and the Refresh button on the right.
        statusBar.Controls.Add(_statusLabel);
        statusBar.Controls.Add(_refreshButton);

        // Keep label and button on ONE line at every width: the label shrinks
        // (SetStatus already truncates long text with a tooltip) so the
        // button never wraps below the 44px bar and disappears in a narrow
        // window. 36 = label margins (24) + bar's right padding (12). When
        // there is less than 40px for the label it is hidden entirely and
        // the Refresh button gets the whole bar — it must stay reachable at
        // the 170px minimum window. (Invisible flow children are skipped.)
        var fitStatusLabel = () =>
        {
            int labelW = statusBar.ClientSize.Width - _refreshButton.Width - 36;
            _statusLabel.Visible = labelW >= 40;
            if (_statusLabel.Visible)
            {
                _statusLabel.Width = labelW;
            }
        };
        statusBar.SizeChanged += (_, _) => fitStatusLabel();
        fitStatusLabel();

        _contentPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.WindowBackground,
            Padding = new Padding(16, 12, 16, 12),
        };
        // Refit the cards whenever this panel is resized — a window resize, or
        // coming back to the Statistics tab after resizing while on another tab.
        _contentPanel.SizeChanged += (_, _) => ReFitCards();

        var titleLabel = new Label
        {
            Text = "NVIDIA Fan Control Utility",
            Font = _headerFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
        };

        var subtitleLabel = new Label
        {
            Text = "Live data from nvidia-smi, run through PowerShell",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
        };

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Theme.WindowBackground,
            Padding = new Padding(16, 0, 16, 0),
        };
        header.Controls.Add(titleLabel);
        header.Controls.Add(subtitleLabel);

        // Adaptive header: a wide window gets the big title with the
        // subtitle to its right; a narrow one gets a smaller centered title
        // and no subtitle (the 15pt title plus the subtitle don't fit in a
        // ~100-300px window without overlapping).
        void UpdateHeader()
        {
            int available = header.ClientSize.Width - header.Padding.Horizontal;
            if (available < 460)
            {
                titleLabel.Font = _headerCompactFont;
                subtitleLabel.Visible = false;
                titleLabel.Location = new Point(
                    (header.ClientSize.Width - titleLabel.Width) / 2, 20);
            }
            else
            {
                titleLabel.Font = _headerFont;
                subtitleLabel.Visible = true;
                titleLabel.Location = new Point(0, 12);
                subtitleLabel.Location = new Point(
                    titleLabel.PreferredWidth + 16, 22);
            }
        }
        header.SizeChanged += (_, _) => UpdateHeader();
        UpdateHeader();

        // ── Tab section: Statistics (GPU data) / Options (settings) ────────
        // A custom dark, owner-drawn tab strip sits under the header. Its
        // content area (_tabContent) holds two Dock=Fill pages; only the one
        // matching the active tab is visible, so switching tabs swaps what
        // fills the window between the strip and the status bar.
        _tabStrip = new TabStrip { Dock = DockStyle.Top };
        // The second argument is the short label shown when the strip
        // collapses to a single toggle button in a very narrow window.
        _tabStrip.AddTab("Statistics", "Stats");
        _tabStrip.AddTab("Options", "Opts");
        _tabStrip.SelectionChanged += (_, _) => ShowTab(_tabStrip.SelectedIndex);

        _tabContent = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.WindowBackground,
        };

        // Options page: a TopDown flow so the heading and the poll-rate
        // row each get their own line and never overlap at any DPI. The poll
        // row itself is a nested LeftToRight flow (label, input, hint in one
        // line) — the same "no hardcoded x" pattern as the status bar.
        _optionsPage = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            // The page holds several rows (poll rate, Fan Control section);
            // in a short/narrow window the content is taller than the page,
            // so it must scroll instead of clipping the bottom rows.
            AutoScroll = true,
            BackColor = Theme.WindowBackground,
            Padding = new Padding(24, 20, 24, 20),
        };

        var optionsHeading = new Label
        {
            Text = "Options",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 20),
        };

        var pollRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 0),
        };

        var pollLabel = new Label
        {
            Text = "Poll rate (seconds):",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 4, 12, 0),
        };

        _pollInput = new TextBox
        {
            Text = _pollSeconds.ToString(),
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.WindowBackground,
            BorderStyle = BorderStyle.FixedSingle,
            Width = 56,
            Margin = new Padding(0, 0, 12, 0),
        };
        // Digits and Backspace only — letters, symbols, and spaces are
        // blocked at the keyboard level. (e.Handled = true means "block this
        // key", so we handle everything that is NOT allowed.) Ctrl shortcuts
        // (copy/paste/undo/select-all) get through; pasted text still slips
        // past the keyboard, so CommitPollRate re-validates whatever is in
        // the box. (Delete/arrows/Tab never fire KeyPress, so they're
        // unaffected.)
        _pollInput.KeyPress += (_, e) =>
        {
            e.Handled = !(ModifierKeys == Keys.Control
                          || char.IsDigit(e.KeyChar)
                          || e.KeyChar == (char)Keys.Back);
        };
        _pollInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true; // no ding sound; we commit instead
                CommitPollRate();
            }
        };
        _pollInput.LostFocus += (_, _) => CommitPollRate();
        // Live warning for pasted junk (KeyPress can't intercept a paste):
        // flag it in the hint now, but don't touch the text — the user can
        // fix it in place, and CommitPollRate enforces the rules on commit.
        _pollInput.TextChanged += (_, _) =>
        {
            string t = _pollInput.Text.Trim();
            if (t.Length == 0)
            {
                SetPollHint(null);
            }
            else if (t.Any(ch => !char.IsDigit(ch))
                     || !int.TryParse(t, out int v)
                     || v < MinPollSeconds || v > MaxPollSeconds)
            {
                SetPollHint(t.Any(ch => !char.IsDigit(ch)) ? "Numbers only" : $"Enter {MinPollSeconds}–{MaxPollSeconds}");
            }
            else
            {
                SetPollHint(null);
            }
        };

        _pollHint = new Label
        {
            Text = "1–60",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0),
        };

        pollRow.Controls.Add(pollLabel);
        pollRow.Controls.Add(_pollInput);
        pollRow.Controls.Add(_pollHint);

        _optionsPage.Controls.Add(optionsHeading);
        _optionsPage.Controls.Add(pollRow);
        BuildFanControlSection();

        // Both pages are Dock=Fill inside _tabContent; only one is visible at
        // a time, so the visible page takes the whole area (see ShowTab).
        _tabContent.Controls.Add(_contentPanel);
        _tabContent.Controls.Add(_optionsPage);

        // Add order matters for docking: the layout engine works from the
        // BACK of the Z-order (last-added) forward, so the Fill panel
        // (_tabContent) must be added FIRST — it's laid out last and gets the
        // space left between the top-docked panels and the status bar.
        //
        // Among the two Top-docked panels, the one added LATER (closer to
        // the front of the Z-order) is laid out last and hugs the top edge;
        // the earlier one stacks directly beneath it. Hence: tabStrip first,
        // header second → header on top, tab strip directly under it.
        Controls.Add(_tabContent);
        Controls.Add(_tabStrip);
        Controls.Add(header);
        Controls.Add(statusBar);

        // Open on the Statistics tab (the GPU data, exactly as before).
        ShowTab(0);

        _timer = new Timer { Interval = _pollSeconds * 1000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        Load += async (_, _) =>
        {
            ApplyDarkTitleBar();
            await RefreshAsync();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkTitleBar();
    }

    private void ApplyDarkTitleBar()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        int useDark = 1;
        _ = DwmSetWindowAttribute(Handle, DwmAttributeUseDarkMode, ref useDark, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Runs nvidia-smi off the UI thread and updates every card.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _refreshButton.Enabled = false;

        try
        {
            var gpus = await Task.Run(() => _service.QueryGpus());
            ApplySnapshots(gpus);
            EvaluateFanControl(gpus);
            SetStatus($"Updated {DateTime.Now:HH:mm:ss}  ·  {gpus.Count} GPU{(gpus.Count == 1 ? "" : "s")} found", Theme.NvGreen);
        }
        catch (Exception ex)
        {
            // Full stack goes to the log; the card shows type + message so the
            // user can tell "nvidia-smi missing" apart from a real bug.
            AppLogger.Log("refresh", ex);
            ShowErrorState($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _refreshButton.Enabled = true;
        }
    }

    private void ApplySnapshots(IReadOnlyList<GpuSnapshot> gpus)
    {
        if (_errorPanel is not null)
        {
            _contentPanel.Controls.Remove(_errorPanel);
            _errorPanel.Dispose();
            _errorPanel = null;
        }

        // Add or remove cards to match the GPU count.
        while (_panels.Count < gpus.Count)
        {
            var panel = new GpuPanel { Margin = new Padding(0, 0, 0, 14) };
            _contentPanel.Controls.Add(panel);
            _panels.Add(panel);
        }

        while (_panels.Count > gpus.Count)
        {
            var panel = _panels[^1];
            _panels.RemoveAt(_panels.Count - 1);
            _contentPanel.Controls.Remove(panel);
            panel.Dispose();
        }

        // Each card spans the full content width (the FlowLayout then stacks them).
        ReFitCards();

        for (int i = 0; i < _panels.Count; i++)
        {
            _panels[i].UpdateFromSnapshot(gpus[i]);
        }
    }

    private void ShowErrorState(string message)
    {
        foreach (var panel in _panels)
        {
            _contentPanel.Controls.Remove(panel);
            panel.Dispose();
        }
        _panels.Clear();

        if (_errorPanel is not null)
        {
            _contentPanel.Controls.Remove(_errorPanel);
            _errorPanel.Dispose();
        }

        // Point at the log file so the user (or we) can find the full stack.
        _errorPanel = new ErrorCard(message + $"\n\nFull details were written to:\n{AppLogger.LogPath}");
        _contentPanel.Controls.Add(_errorPanel);

        ReFitCards();

        SetStatus("nvidia-smi is not reporting data — see message above", Theme.ErrorRed);
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text.Length <= 90 ? text : text[..90] + "…";
        _statusLabel.ForeColor = color;
        _toolTip.SetToolTip(_statusLabel, text); // full message on hover
    }

    /// <summary>
    /// Validates the poll-rate input and applies it to the timer.
    /// Rules: digits only, whole seconds, 1–60 inclusive. Anything else is
    /// rejected with a message and the input reverts to the last accepted
    /// value, so the timer can never be left with a bad interval.
    /// Called on Enter and on focus loss (which also catches pasted text).
    /// </summary>
    private void CommitPollRate()
    {
        string text = _pollInput.Text.Trim();

        // Empty: user deleted everything. Not a valid rate — revert quietly
        // (the hint stays in its resting state rather than scolding).
        if (text.Length == 0)
        {
            _pollInput.Text = _pollSeconds.ToString();
            SetPollHint(null);
            return;
        }

        // Digits only. KeyPress blocks typing letters, but a paste like
        // "5s" or "1 0" lands here. int.TryParse would also reject this,
        // but checking first lets us give a specific message.
        if (text.Any(ch => !char.IsDigit(ch)))
        {
            RejectPollInput("Numbers only");
            return;
        }

        // Overflow-proof parse (a 30-digit paste would throw on int.Parse).
        if (!int.TryParse(text, out int seconds))
        {
            RejectPollInput("Numbers only");
            return;
        }

        if (seconds < MinPollSeconds || seconds > MaxPollSeconds)
        {
            RejectPollInput($"Enter {MinPollSeconds}–{MaxPollSeconds}");
            return;
        }

        // Accepted. Leading zeros ("007") normalize to the plain value.
        if (seconds != _pollSeconds)
        {
            _pollSeconds = seconds;
            _timer.Interval = seconds * 1000;
            SetStatus($"Poll rate set to {seconds}s", Theme.NvGreen);
        }
        _pollInput.Text = seconds.ToString();
        SetPollHint(null);
    }

    private void RejectPollInput(string message)
    {
        // Set the text FIRST: assigning Text fires TextChanged, which would
        // reset the hint to neutral and erase the error we're about to show.
        _pollInput.Text = _pollSeconds.ToString();
        SetPollHint(message);
        _pollInput.SelectAll();
        _pollInput.Focus();
    }

    /// <summary>message == null restores the neutral "1–60" hint.</summary>
    private void SetPollHint(string? message)
    {
        _pollHint.Text = message ?? "1–60";
        _pollHint.ForeColor = message is null ? Theme.TextSecondary : Theme.ErrorRed;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Fan Control integration
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the "Fan Control" section of the Options tab: a master checkbox,
    /// the exe path with a Browse button, a list of profile rows (trigger °C →
    /// config name), an Add button, and the hint/status labels.
    /// </summary>
    private void BuildFanControlSection()
    {
        var sectionHeading = new Label
        {
            Text = "Fan Control",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 24, 0, 8),
        };

        _fanEnable = new CheckBox
        {
            Text = "Enable Fan Control profile switching (driven by the hottest GPU)",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
            Checked = _fanSettings.Enabled,
        };
        _fanEnable.CheckedChanged += (_, _) =>
        {
            _fanSettings.Enabled = _fanEnable.Checked;
            SetFanControlsEnabled(_fanEnable.Checked);
            CommitFanSettings();
        };

        // ── FanControl.exe path row ────────────────────────────────────────
        // FlowDirection is switched by fitOptionsWidths: side-by-side in a
        // wide window, stacked (TopDown) in a narrow one, so the Browse
        // button stays reachable instead of clipping off the right edge.
        var exeRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10),
        };

        var exeLabel = new Label
        {
            Text = "FanControl.exe:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 4, 12, 0),
        };

        _fanExePath = new TextBox
        {
            Text = _fanSettings.ExePath,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.WindowBackground,
            BorderStyle = BorderStyle.FixedSingle,
            Width = 380,
            Margin = new Padding(0, 0, 12, 0),
        };
        _fanExePath.TextChanged += (_, _) =>
        {
            if (_fanLoading)
            {
                return;
            }

            _fanSettings.ExePath = _fanExePath.Text.Trim();
            CommitFanSettings();
        };
        _toolTip.SetToolTip(
            _fanExePath,
            "The folder containing this exe is used as Fan Control's working directory — " +
            "it must be the same folder as your config .json files.");

        _fanBrowseButton = MakeSmallButton("Browse…");
        _fanBrowseButton.Click += (_, _) => BrowseFanControlExe();

        exeRow.Controls.Add(exeLabel);
        exeRow.Controls.Add(_fanExePath);
        exeRow.Controls.Add(_fanBrowseButton);

        // ── Profile rows ───────────────────────────────────────────────────
        var profilesHeading = new Label
        {
            Text = "Profiles (coolest first; the hottest one whose trigger is at or below " +
                   "the current max GPU temperature becomes active):",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };

        _fanProfilesPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 4),
        };

        _fanAddButton = MakeSmallButton("Add profile");
        _fanAddButton.Click += (_, _) => AddFanProfileRow(string.Empty, string.Empty);

        _fanHint = new Label
        {
            Text = string.Empty,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0),
        };

        _fanStatus = new Label
        {
            Text = string.Empty,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0),
        };

        // The options page is a TopDown flow, so a row KEEPS its full
        // preferred width even when it's narrower than the page — wrapping
        // never triggers on its own. Instead: below ~520px the exe row
        // stacks vertically (label / box / Browse on their own lines, box at
        // full page width), and the profile rows are always stacked — so
        // "Browse…" and "Remove" stay on their rows and reachable down to
        // the minimum window width. (Constants cover the page padding 48,
        // the row's label/margin totals, and the buttons' measured widths.)
        var fitOptionsWidths = () =>
        {
            int pageWidth = _optionsPage.ClientSize.Width;
            if (pageWidth <= 0)
            {
                return;
            }

            bool stack = pageWidth < 520;
            exeRow.FlowDirection = stack ? FlowDirection.TopDown : FlowDirection.LeftToRight;
            exeLabel.Margin = new Padding(0, 4, 12, 0);
            _fanExePath.Width = stack
                ? Math.Max(120, pageWidth - 48)
                : Math.Clamp(pageWidth - 238, 140, 380);
            _fanExePath.Margin = new Padding(0, 0, stack ? 0 : 12, 0);

            // Profile rows: one line each in a wide window, stacked in a
            // narrow one (a full row needs ~440px, and a TopDown parent never
            // constrains a row's width, so wrapping can't be relied on).
            foreach (Control child in _fanProfilesPanel.Controls)
            {
                if (child is FlowLayoutPanel profileRow)
                {
                    profileRow.FlowDirection = stack
                        ? FlowDirection.TopDown
                        : FlowDirection.LeftToRight;
                }
            }

            foreach (var input in _fanNameInputs)
            {
                input.Width = stack
                    ? Math.Min(240, Math.Max(120, pageWidth - 48))
                    : Math.Clamp(pageWidth - 190, 120, 240);
            }

            // Long hint/status/heading text wraps onto extra lines (AutoSize
            // labels wrap when their width is capped) instead of overflowing.
            int labelCap = Math.Max(100, pageWidth - 48);
            _fanEnable.MaximumSize = new Size(labelCap, 0);
            _fanHint.MaximumSize = new Size(labelCap, 0);
            _fanStatus.MaximumSize = new Size(labelCap, 0);
            profilesHeading.MaximumSize = new Size(labelCap, 0);
        };
        _optionsPage.SizeChanged += (_, _) => fitOptionsWidths();

        // Populate the rows from the saved settings. The _fanLoading guard stops
        // the programmatic Text assignments from bouncing back through commit.
        _fanLoading = true;
        try
        {
            foreach (var profile in _fanSettings.Profiles)
            {
                AddFanProfileRow(profile.TemperatureC.ToString(), profile.ConfigName);
            }
        }
        finally
        {
            _fanLoading = false;
        }

        SetFanControlsEnabled(_fanSettings.Enabled);
        _optionsPage.Controls.Add(sectionHeading);
        _optionsPage.Controls.Add(_fanEnable);
        _optionsPage.Controls.Add(exeRow);
        _optionsPage.Controls.Add(profilesHeading);
        _optionsPage.Controls.Add(_fanProfilesPanel);
        _optionsPage.Controls.Add(_fanAddButton);
        _optionsPage.Controls.Add(_fanHint);
        _optionsPage.Controls.Add(_fanStatus);

        // First commit: arms the service with the persisted ladder (so the
        // next refresh re-applies it) and paints the hint/status labels.
        CommitFanSettings();

        // Apply the width fit once up front; later resizes are handled by the
        // SizeChanged hook above.
        fitOptionsWidths();
    }

    /// <summary>
    /// Appends one profile row: "at [temp] °C load [name] [Remove]".
    /// The row is wired up but NOT yet committed — the next
    /// <see cref="CommitFanSettings"/> (or an immediate one from the caller)
    /// picks it up. Horizontal when the page is wide enough, stacked when
    /// narrow (the same <c>stack</c> rule fitOptionsWidths applies on resize).
    /// </summary>
    private void AddFanProfileRow(string temp, string name)
    {
        bool stack = _optionsPage.ClientSize.Width > 0
                     && _optionsPage.ClientSize.Width < 520;

        var row = new FlowLayoutPanel
        {
            // FlowDirection is switched by fitOptionsWidths on resize (see
            // above); the initial value matches the current page width.
            FlowDirection = stack ? FlowDirection.TopDown : FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6),
        };

        var atLabel = MakeRowLabel("at");

        var tempInput = new TextBox
        {
            Text = temp,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.WindowBackground,
            BorderStyle = BorderStyle.FixedSingle,
            Width = 52,
            Margin = new Padding(0, 0, 6, 0),
        };
        // Digits and Backspace only — same keyboard-level filtering as the
        // poll-rate box (letters/symbols blocked; pastes caught by commit).
        tempInput.KeyPress += (_, e) =>
        {
            e.Handled = !(ModifierKeys == Keys.Control
                          || char.IsDigit(e.KeyChar)
                          || e.KeyChar == (char)Keys.Back);
        };
        tempInput.TextChanged += (_, _) =>
        {
            if (!_fanLoading)
            {
                CommitFanSettings();
            }
        };

        var unitLabel = MakeRowLabel("°C load");

        var nameInput = new TextBox
        {
            Text = name,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.WindowBackground,
            BorderStyle = BorderStyle.FixedSingle,
            Width = 240,
            Margin = new Padding(0, 0, 6, 0),
        };
        nameInput.TextChanged += (_, _) =>
        {
            if (!_fanLoading)
            {
                CommitFanSettings();
            }
        };
        _toolTip.SetToolTip(nameInput, "Fan Control config name, e.g. \"Gaming\" (\".json\" is added automatically).");

        var removeButton = MakeSmallButton("Remove");
        // Capture the ROW itself, not an index: indices shift when earlier
        // rows are removed, but the control reference stays valid for the
        // row's whole lifetime.
        removeButton.Click += (_, _) => RemoveFanProfileRow(row);

        row.Controls.Add(atLabel);
        row.Controls.Add(tempInput);
        row.Controls.Add(unitLabel);
        row.Controls.Add(nameInput);
        row.Controls.Add(removeButton);

        _fanTempInputs.Add(tempInput);
        _fanNameInputs.Add(nameInput);
        _fanProfilesPanel.Controls.Add(row);

        // A brand-new row wasn't sized by the (not re-fired) SizeChanged
        // event, so fit its name box to the current page width explicitly.
        if (_optionsPage.ClientSize.Width > 0)
        {
            int pageWidth = _optionsPage.ClientSize.Width;
            nameInput.Width = stack
                ? Math.Min(240, Math.Max(120, pageWidth - 48))
                : Math.Clamp(pageWidth - 190, 120, 240);
        }
    }

    /// <summary>Removes the given profile row and its inputs from the ladder.</summary>
    private void RemoveFanProfileRow(FlowLayoutPanel row)
    {
        int index = -1;
        for (int i = 0; i < _fanTempInputs.Count; i++)
        {
            if (ReferenceEquals(_fanTempInputs[i].Parent, row))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            _fanTempInputs.RemoveAt(index);
            _fanNameInputs.RemoveAt(index);
        }

        _fanProfilesPanel.Controls.Remove(row);
        row.Dispose();

        CommitFanSettings();
    }

    /// <summary>Opens a file dialog to pick FanControl.exe.</summary>
    private void BrowseFanControlExe()
    {
        string current = _fanExePath.Text.Trim();
        string initialDir = "";
        if (current.Length > 0 && Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(current))))
        {
            initialDir = Path.GetDirectoryName(Path.GetFullPath(current))!;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Select FanControl.exe",
            Filter = "FanControl.exe|FanControl.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (initialDir.Length > 0)
        {
            dialog.InitialDirectory = initialDir;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _fanExePath.Text = dialog.FileName; // fires TextChanged → commit
        }
    }

    /// <summary>Enables/disables every editable Fan Control control (master switch).</summary>
    private void SetFanControlsEnabled(bool enabled)
    {
        _fanExePath.Enabled = enabled;
        _fanBrowseButton.Enabled = enabled;
        _fanAddButton.Enabled = enabled;

        for (int i = 0; i < _fanTempInputs.Count; i++)
        {
            _fanTempInputs[i].Enabled = enabled;
            _fanNameInputs[i].Enabled = enabled;

            if (_fanTempInputs[i].Parent is Control row)
            {
                foreach (Control child in row.Controls)
                {
                    if (child is Button)
                    {
                        child.Enabled = enabled;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds <see cref="_fanSettings"/> from the current row contents (skipping
    /// invalid rows), persists it, and hands the new ladder to the service.
    /// Any invalid row is reported in the hint but never blocks the valid ones.
    /// </summary>
    private void CommitFanSettings()
    {
        var profiles = new List<FanControlProfile>();
        string? problem = null;

        for (int i = 0; i < _fanTempInputs.Count; i++)
        {
            string tempText = _fanTempInputs[i].Text.Trim();
            string name = _fanNameInputs[i].Text.Trim();

            int parsedTemp = 0;
            bool tempOk = tempText.Length > 0
                          && tempText.All(char.IsDigit)
                          && int.TryParse(tempText, out parsedTemp)
                          && parsedTemp >= 0 && parsedTemp <= 125;

            if (!tempOk)
            {
                problem = problem ?? $"Row {i + 1}: temperature must be a number from 0 to 125.";
            }
            else if (name.Length == 0)
            {
                problem = problem ?? $"Row {i + 1}: profile name is empty.";
            }
            else
            {
                profiles.Add(new FanControlProfile { TemperatureC = parsedTemp, ConfigName = name });
            }
        }

        _fanSettings.Profiles = profiles;
        SettingsStore.Save(_fanSettings);

        // Only re-arm the service when the effective ladder actually changed,
        // so editing one field at a time doesn't fire a Fan Control relaunch
        // for every keystroke.
        string signature = BuildLadderSignature(
            _fanSettings.Enabled, _fanSettings.ExePath, profiles);
        if (signature != _fanLadderSignature)
        {
            _fanLadderSignature = signature;
            _fanControl.SetSettings(_fanSettings);
        }

        SetFanHint(problem);
        RefreshFanStatus();
    }

    /// <summary>
    /// A stable string for a ladder's effective content (order-insensitive for
    /// the profile list — the service sorts it itself, so the UI row order is
    /// cosmetic and must not count as a change).
    /// </summary>
    private static string BuildLadderSignature(
        bool enabled, string exePath, IReadOnlyList<FanControlProfile> profiles)
    {
        var parts = new List<string> { enabled ? "on" : "off", exePath.Trim() };
        foreach (var profile in profiles.OrderBy(p => p.TemperatureC))
        {
            parts.Add($"{profile.TemperatureC}|{profile.ConfigName.Trim()}");
        }

        return string.Join(";;", parts);
    }

    /// <summary>problem == null shows the neutral behavior hint.</summary>
    private void SetFanHint(string? problem)
    {
        if (problem is not null)
        {
            _fanHint.Text = problem + " (This row is ignored until fixed.)";
            _fanHint.ForeColor = Theme.ErrorRed;
            return;
        }

        _fanHint.Text = $"When Fan Control is switched, the active profile is re-applied on the next refresh. " +
                        $"Cooling steps down only after {FanControlService.HysteresisDegreesC}° below a trigger (anti-flap). " +
                        $"Saved to {SettingsStore.SettingsPath}";
        _fanHint.ForeColor = Theme.TextSecondary;
    }

    /// <summary>
    /// Runs the Fan Control ladder against the latest snapshots. The hottest
    /// GPU's temperature is the trigger source; the service only starts
    /// FanControl.exe when the selected profile actually changes.
    /// </summary>
    private void EvaluateFanControl(IReadOnlyList<GpuSnapshot> gpus)
    {
        int? maxTemp = null;
        foreach (var gpu in gpus)
        {
            if (gpu.TemperatureC is int t && (maxTemp is null || t > maxTemp.Value))
            {
                maxTemp = t;
            }
        }

        _fanControl.Update(maxTemp);
        RefreshFanStatus();
    }

    /// <summary>Updates the Fan Control status line under the Options tab.</summary>
    private void RefreshFanStatus()
    {
        if (!_fanSettings.Enabled)
        {
            _fanStatus.Text = "Fan Control: disabled — NVIDIA Fan Control Utility will not touch Fan Control.";
            _fanStatus.ForeColor = Theme.TextSecondary;
            return;
        }

        if (_fanControl.ActiveConfigName is string active)
        {
            string text = $"Fan Control: profile \"{active}\" is active.";
            if (_fanControl.LastLaunchFailed)
            {
                text = $"Fan Control: last switch to \"{active}\" FAILED — check the exe path and log: {AppLogger.LogPath}";
                _fanStatus.ForeColor = Theme.ErrorRed;
            }
            else
            {
                _fanStatus.ForeColor = Theme.NvGreen;
            }

            _fanStatus.Text = text;
            return;
        }

        if (_fanSettings.Profiles.Count == 0)
        {
            _fanStatus.Text = "Fan Control: enabled — add at least one valid profile below to activate it.";
        }
        else
        {
            _fanStatus.Text = "Fan Control: enabled — waiting for the first temperature reading.";
        }
        _fanStatus.ForeColor = Theme.TextSecondary;
    }

    /// <summary>A small flat, themed button (Browse / Add / Remove rows).</summary>
    private Button MakeSmallButton(string text)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelBackground,
            ForeColor = Theme.NvGreen,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 0),
        };
        button.FlatAppearance.BorderColor = Theme.PanelBorder;
        button.FlatAppearance.MouseOverBackColor = Theme.PanelBorder;
        return button;
    }

    /// <summary>A small secondary label used inside a profile row.</summary>
    private static Label MakeRowLabel(string text)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 4, 6, 0),
        };
    }

    /// <summary>
    /// Shows the page for the given tab (0 = Statistics, 1 = Options) and
    /// hides the other. Both pages are Dock=Fill, so making one visible gives
    /// it the whole area between the tab strip and the status bar.
    /// </summary>
    private void ShowTab(int index)
    {
        bool statistics = index == 0;
        _contentPanel.Visible = statistics;
        _optionsPage.Visible = !statistics;
    }

    /// <summary>
    /// Sizes every GPU card (and the error card, if shown) to the current
    /// content width. Called on refresh and whenever the content panel is
    /// resized, so the cards stay full-width even after the window is resized
    /// while another tab is active.
    /// </summary>
    private void ReFitCards()
    {
        if (_contentPanel.ClientSize.Width <= 0)
        {
            return;
        }

        // 150 matches GpuPanel.MinimumSize, so cards follow the window all the
        // way down to the compact layout with no horizontal scroll even at
        // the 170px minimum window.
        int targetWidth = Math.Max(
            150,
            _contentPanel.ClientSize.Width - _contentPanel.Padding.Horizontal - 4);

        foreach (var panel in _panels)
        {
            panel.Width = targetWidth;
        }

        if (_errorPanel is not null)
        {
            _errorPanel.Width = targetWidth;
        }
    }
}
