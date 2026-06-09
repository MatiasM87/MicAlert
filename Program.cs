using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;

namespace MicAlert;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MicAlertContext());
    }
}

public sealed class MicAlertContext : ApplicationContext
{
    private const string StartupMessage = "MicAlert iniciado.";
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<IndicatorForm> _forms = new();
    // Hidden control used only to marshal background-scan results back to the UI thread.
    private readonly Control _sync = new();
    private AppConfig _config;
    private bool _lastVisible;
    private string _lastReason = "";
    private volatile bool _scanInProgress;
    private volatile bool _exiting;
    private readonly string _baseDir;
    private readonly string _configPath;
    private readonly string _logPath;
    private SettingsForm? _settingsForm;

    public MicAlertContext()
    {
        _baseDir = AppContext.BaseDirectory;
        _configPath = Path.Combine(_baseDir, "config.json");
        _logPath = Path.Combine(_baseDir, "micalert-debug.log");
        _config = AppConfig.Load(_configPath);

        _sync.CreateControl();
        RebuildIndicatorForms();

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "MicAlert",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(200, _config.PollMs) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Log(StartupMessage);
        Tick();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Configuración", null, (_, _) => OpenSettings());
        menu.Items.Add("Recargar config", null, (_, _) => ReloadConfig());
        menu.Items.Add("Mostrar prueba", null, (_, _) => ShowIndicator("Prueba manual"));
        menu.Items.Add("Ocultar prueba", null, (_, _) => HideIndicator("Prueba manual"));
        menu.Items.Add("Abrir log", null, (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true }); } catch { }
        });
        menu.Items.Add("Salir", null, (_, _) => ExitThread());
        return menu;
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Show();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_config.Clone(),
            onSave: cfg =>
            {
                _config = cfg;
                if (!_config.Save(_configPath))
                    Log("ERROR: no se pudo guardar config en " + _configPath);
                ApplyConfig();
                Log("Config guardada desde UI.");
                Tick();
            },
            onPreview: cfg =>
            {
                _config = cfg;
                ApplyConfig();
                Tick();
            },
            onTestShow: () => ShowIndicator("Prueba desde UI"),
            onTestHide: () => HideIndicator("Prueba desde UI"));

        // Pause polling while settings are open: UIA scans run on the UI thread
        // and block it for hundreds of ms, making the form unresponsive.
        _timer.Stop();
        _settingsForm.FormClosed += (_, _) => _timer.Start();

        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void ReloadConfig()
    {
        _config = AppConfig.Load(_configPath);
        ApplyConfig();
        Log("Config recargada.");
        Tick();
    }

    private void ApplyConfig()
    {
        _timer.Interval = Math.Max(200, _config.PollMs);
        RebuildIndicatorForms();
    }

    private void RebuildIndicatorForms()
    {
        foreach (var form in _forms)
        {
            try { form.Hide(); form.Dispose(); } catch { }
        }
        _forms.Clear();
        _lastVisible = false;
        _lastReason = "";

        var screens = GetTargetScreens(_config).ToArray();
        if (screens.Length == 0)
            Log("ERROR: sin pantallas disponibles, indicador desactivado.");
        foreach (var screen in screens)
        {
            var f = new IndicatorForm();
            f.ApplyConfig(_config, screen.WorkingArea);
            _forms.Add(f);
        }
    }

    private static IEnumerable<Screen> GetTargetScreens(AppConfig cfg)
    {
        var mode = (cfg.MonitorMode ?? "primary").Trim().ToLowerInvariant();
        if (mode == "all" || mode == "todos") return Screen.AllScreens;
        var primary = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
        return primary != null ? new[] { primary } : Array.Empty<Screen>();
    }

    // Detection (UIA scan / registry walk) is slow, so it runs on a background
    // thread; only the show/hide result is marshaled back to the UI thread.
    // The _scanInProgress guard prevents scans from piling up when one takes
    // longer than the poll interval.
    private void Tick()
    {
        if (_scanInProgress || _exiting) return;
        _scanInProgress = true;
        var cfg = _config;
        System.Threading.Tasks.Task.Run(() =>
        {
            MicState result;
            try
            {
                result = ShouldShowIndicator(cfg);
            }
            catch (Exception ex)
            {
                Log("ERROR Tick: " + ex.Message);
                result = new MicState(false, "Error: " + ex.Message);
            }

            try
            {
                if (!_exiting && _sync.IsHandleCreated)
                {
                    _sync.BeginInvoke(() =>
                    {
                        _scanInProgress = false;
                        if (_exiting) return;
                        if (result.Show) ShowIndicator(result.Reason);
                        else HideIndicator(result.Reason);
                    });
                }
                else
                {
                    _scanInProgress = false;
                }
            }
            catch
            {
                _scanInProgress = false;
            }
        });
    }

    private MicState ShouldShowIndicator(AppConfig cfg)
    {
        var mode = (cfg.Mode ?? "zoom").Trim().ToLowerInvariant();

        if (mode == "windows")
        {
            var win = WindowsMicDetector.IsMicrophoneInUse(out var windowsReason);
            return new MicState(win, "Windows: " + windowsReason);
        }

        var zoom = ZoomMuteDetector.IsZoomUnmuted(out var zoomReason, cfg.Debug);
        if (zoom.HasValue)
            return new MicState(zoom.Value, "Zoom: " + zoomReason);

        if (cfg.FallbackToWindowsMicInUse)
        {
            var win = WindowsMicDetector.IsMicrophoneInUse(out var winReason);
            return new MicState(win, "Zoom sin estado claro; fallback Windows: " + winReason);
        }

        return new MicState(false, "Zoom sin estado claro: " + zoomReason);
    }

    private void ShowIndicator(string reason)
    {
        foreach (var form in _forms)
        {
            if (!form.Visible) form.Show();
        }
        if (!_lastVisible || _lastReason != reason)
        {
            Log("VISIBLE: " + reason);
            _lastVisible = true;
            _lastReason = reason;
        }
    }

    private void HideIndicator(string reason)
    {
        foreach (var form in _forms)
        {
            if (form.Visible) form.Hide();
        }
        if (_lastVisible || _lastReason != reason)
        {
            Log("OCULTO: " + reason);
            _lastVisible = false;
            _lastReason = reason;
        }
    }

    private void Log(string message)
    {
        if (!_config.Debug && !message.StartsWith("ERROR") && message != StartupMessage) return;
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        _timer.Stop();
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var form in _forms) form.Dispose();
        _settingsForm?.Dispose();
        _sync.Dispose();
        base.ExitThreadCore();
    }
}

public sealed class IndicatorForm : Form
{
    public IndicatorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Red;
        Opacity = 1.0;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOPMOST = 0x00000008;
            const int WS_EX_TRANSPARENT = 0x00000020; // click-through: el punto no roba clicks del mouse
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void ApplyConfig(AppConfig c, Rectangle area)
    {
        var size = Math.Max(2, c.Size);
        Size = new Size(size, size);
        BackColor = ColorHelper.FromName(c.Color);
        Opacity = Math.Clamp(c.Opacity, 0.1, 1.0);

        var pos = (c.Position ?? "top-right").Trim().ToLowerInvariant();
        int x = area.Right - size - c.OffsetX;
        int y = area.Top + c.OffsetY;

        if (pos == "top-left")
        {
            x = area.Left + c.OffsetX;
            y = area.Top + c.OffsetY;
        }
        else if (pos == "bottom-right")
        {
            x = area.Right - size - c.OffsetX;
            y = area.Bottom - size - c.OffsetY;
        }
        else if (pos == "bottom-left")
        {
            x = area.Left + c.OffsetX;
            y = area.Bottom - size - c.OffsetY;
        }

        Location = new Point(x, y);
    }
}

public sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly Action<AppConfig> _onSave;
    private readonly Action<AppConfig> _onPreview;
    private readonly Action _onTestShow;
    private readonly Action _onTestHide;

    private readonly NumericUpDown _size;
    private readonly NumericUpDown _offsetX;
    private readonly NumericUpDown _offsetY;
    private readonly NumericUpDown _pollMs;
    private readonly NumericUpDown _opacity;
    private readonly ComboBox _position;
    private readonly ComboBox _monitorMode;
    private readonly ComboBox _mode;
    private readonly TextBox _color;
    private readonly CheckBox _fallback;
    private readonly CheckBox _debug;
    private readonly Panel _preview;

    public SettingsForm(AppConfig config, Action<AppConfig> onSave, Action<AppConfig> onPreview, Action onTestShow, Action onTestHide)
    {
        _config = config;
        _onSave = onSave;
        _onPreview = onPreview;
        _onTestShow = onTestShow;
        _onTestHide = onTestHide;

        Text = "MicAlert - Configuración";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 430;
        Height = 520;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 0,
            AutoSize = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        Controls.Add(root);

        _mode = ComboBox(new[] { "zoom", "windows" }, _config.Mode);
        _size = Num(_config.Size, 2, 200, 1);
        _position = ComboBox(new[] { "top-right", "top-left", "bottom-right", "bottom-left" }, _config.Position);
        _monitorMode = ComboBox(new[] { "primary", "all" }, _config.MonitorMode);
        _offsetX = Num(_config.OffsetX, 0, 1000, 1);
        _offsetY = Num(_config.OffsetY, 0, 1000, 1);
        _pollMs = Num(_config.PollMs, 200, 10000, 100);
        _color = new TextBox { Text = _config.Color, Dock = DockStyle.Fill };
        _opacity = Num((int)Math.Round(_config.Opacity * 100), 10, 100, 5);
        _fallback = new CheckBox { Checked = _config.FallbackToWindowsMicInUse, Text = "Usar Windows si Zoom no responde", AutoSize = true };
        _debug = new CheckBox { Checked = _config.Debug, Text = "Debug log", AutoSize = true };
        _preview = new Panel { Width = 36, Height = 24, BackColor = ColorHelper.FromName(_config.Color), BorderStyle = BorderStyle.FixedSingle };

        AddRow(root, "Modo", _mode);
        AddRow(root, "Tamaño pixel", _size);
        AddRow(root, "Ubicación", _position);
        AddRow(root, "Monitores", _monitorMode);
        AddRow(root, "Offset X", _offsetX);
        AddRow(root, "Offset Y", _offsetY);
        AddRow(root, "Chequeo cada ms", _pollMs);

        var colorPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        var btnColor = new Button { Text = "Elegir", Width = 70 };
        btnColor.Click += (_, _) => PickColor();
        _color.TextChanged += (_, _) => _preview.BackColor = ColorHelper.FromName(_color.Text);
        colorPanel.Controls.Add(_color);
        colorPanel.Controls.Add(btnColor);
        colorPanel.Controls.Add(_preview);
        AddRow(root, "Color", colorPanel);

        AddRow(root, "Opacidad %", _opacity);
        AddRow(root, "Fallback", _fallback);
        AddRow(root, "Log", _debug);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var save = new Button { Text = "Guardar", Width = 90 };
        var cancel = new Button { Text = "Cerrar", Width = 90 };
        var testShow = new Button { Text = "Probar", Width = 80 };
        var testHide = new Button { Text = "Ocultar", Width = 80 };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        testShow.Click += (_, _) => { Preview(); _onTestShow(); };
        testHide.Click += (_, _) => _onTestHide();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(testHide);
        buttons.Controls.Add(testShow);

        var buttonRow = root.RowCount;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowCount = buttonRow + 1;
        root.Controls.Add(buttons, 0, buttonRow);
        root.SetColumnSpan(buttons, 2);
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = ColorHelper.FromName(_color.Text), FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _color.Text = ColorTranslator.ToHtml(dlg.Color);
            _preview.BackColor = dlg.Color;
        }
    }

    private void CollectFormValues()
    {
        _config.Mode = _mode.Text;
        _config.Size = (int)_size.Value;
        _config.Position = _position.Text;
        _config.MonitorMode = _monitorMode.Text;
        _config.OffsetX = (int)_offsetX.Value;
        _config.OffsetY = (int)_offsetY.Value;
        _config.PollMs = (int)_pollMs.Value;
        _config.Color = string.IsNullOrWhiteSpace(_color.Text) ? "red" : _color.Text.Trim();
        _config.Opacity = (double)_opacity.Value / 100.0;
        _config.FallbackToWindowsMicInUse = _fallback.Checked;
        _config.Debug = _debug.Checked;
    }

    // Applies current form values to the running indicator without writing to disk.
    private void Preview()
    {
        CollectFormValues();
        _onPreview(_config.Clone());
    }

    // Applies current form values and persists them to config.json.
    private void Save()
    {
        CollectFormValues();
        _onSave(_config.Clone());
    }

    private static ComboBox ComboBox(string[] items, string? value)
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        cb.Items.AddRange(items.Cast<object>().ToArray());
        var selected = items.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        cb.SelectedItem = selected;
        return cb;
    }

    private static NumericUpDown Num(decimal value, decimal min, decimal max, decimal inc)
    {
        return new NumericUpDown { Minimum = min, Maximum = max, Increment = inc, Value = Math.Min(max, Math.Max(min, value)), Dock = DockStyle.Fill };
    }

    private static void AddRow(TableLayoutPanel panel, string label, Control control)
    {
        var row = panel.RowCount;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowCount = row + 1;
        panel.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);
        panel.Controls.Add(control, 1, row);
    }
}

public sealed class AppConfig
{
    public string Mode { get; set; } = "zoom";
    public int Size { get; set; } = 14;
    public string Position { get; set; } = "top-right";
    public string MonitorMode { get; set; } = "primary"; // primary | all
    public int OffsetX { get; set; } = 12;
    public int OffsetY { get; set; } = 12;
    public int PollMs { get; set; } = 500;
    public string Color { get; set; } = "red";
    public double Opacity { get; set; } = 1.0;
    public bool FallbackToWindowsMicInUse { get; set; } = false;
    public bool Debug { get; set; } = false;

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var cfg = new AppConfig();
                cfg.Save(path);
                return cfg;
            }
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public bool Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch { return false; }
    }

    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }
}

public static class ColorHelper
{
    public static Color FromName(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return Color.Red;
        try
        {
            if (color.StartsWith("#")) return ColorTranslator.FromHtml(color);
            var c = Color.FromName(color);
            return c.IsKnownColor || c.IsNamedColor ? c : Color.Red;
        }
        catch { return Color.Red; }
    }
}

public readonly record struct MicState(bool Show, string Reason);

public static class ZoomMuteDetector
{
    private static readonly Dictionary<int, string> _procCache = new();
    private static DateTime _procCacheTime = DateTime.MinValue;

    private static string GetCachedProcessName(int pid)
    {
        var now = DateTime.UtcNow;
        if (now - _procCacheTime > TimeSpan.FromSeconds(5))
        {
            _procCache.Clear();
            _procCacheTime = now;
        }
        if (_procCache.TryGetValue(pid, out var cached))
            return cached;

        string name;
        try { using var p = Process.GetProcessById(pid); name = Normalize(p.ProcessName); } catch { name = ""; }
        // Only cache zoom-related names. Non-zoom PIDs are not cached so if Zoom starts on a
        // recently-recycled PID it is detected on the very next tick rather than after the TTL.
        if (name.Contains("zoom") || name.Contains("cpt") || name.Contains("zcef"))
            _procCache[pid] = name;
        return name;
    }

    // Property caches: without these, every property read (Name, HelpText, etc.) is a
    // separate cross-process COM call. With a CacheRequest active, FindAll fetches all
    // requested properties in a single batch, cutting scan time dramatically.
    private static readonly CacheRequest _windowCache = BuildWindowCache();
    private static readonly CacheRequest _elementCache = BuildElementCache();

    private static CacheRequest BuildWindowCache()
    {
        var cr = new CacheRequest();
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.ClassNameProperty);
        cr.Add(AutomationElement.ProcessIdProperty);
        // Full mode: we still need a live reference to call FindAll on the window.
        cr.AutomationElementMode = AutomationElementMode.Full;
        return cr;
    }

    private static CacheRequest BuildElementCache()
    {
        var cr = new CacheRequest();
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.HelpTextProperty);
        cr.Add(AutomationElement.AutomationIdProperty);
        cr.Add(AutomationElement.ClassNameProperty);
        cr.Add(AutomationElement.LocalizedControlTypeProperty);
        // None: descendants are read-only; skipping the live reference avoids one
        // cross-process handle per element.
        cr.AutomationElementMode = AutomationElementMode.None;
        return cr;
    }

    // Hoisted to avoid per-tick COM-interop allocation; ToolBar removed — its child buttons are returned separately.
    private static readonly Condition _buttonCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton)
    );

    private static readonly string[] MutedTerms =
    {
        "reactivar audio", "activar audio", "unmute", "unmute audio", "join audio", "conectar audio"
    };

    private static readonly string[] UnmutedTerms =
    {
        "silenciar", "silenciar audio", "mute audio", "mute my audio"
    };

    // Word-boundary-aware substring match: prevents "mute" matching inside "unmute", "MuteButton", etc.
    private static bool ContainsTerm(string normalized, string term)
    {
        var idx = normalized.IndexOf(term, StringComparison.Ordinal);
        if (idx < 0) return false;
        var beforeOk = idx == 0 || !char.IsLetterOrDigit(normalized[idx - 1]);
        var afterOk  = idx + term.Length >= normalized.Length || !char.IsLetterOrDigit(normalized[idx + term.Length]);
        return beforeOk && afterOk;
    }

    public static bool? IsZoomUnmuted(out string reason, bool debug)
    {
        reason = "";
        var foundMuted = false;
        var foundUnmuted = false;
        string mutedHit = "";
        string unmutedHit = "";
        var scanned = 0;

        try
        {
            AutomationElementCollection windows;
            using (_windowCache.Activate())
                windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);

            foreach (AutomationElement win in windows)
            {
                if (!IsZoomWindow(win)) continue;

                string winName = SafeName(win);
                scanned++;

                AutomationElementCollection elements;
                using (_elementCache.Activate())
                    elements = win.FindAll(TreeScope.Descendants, _buttonCondition);
                foreach (AutomationElement el in elements)
                {
                    var text = BuildText(el);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var n = Normalize(text);

                    if (MutedTerms.Any(t => ContainsTerm(n, t)))
                    {
                        foundMuted = true;
                        mutedHit = text;
                        break;
                    }

                    if (!foundUnmuted && UnmutedTerms.Any(t => ContainsTerm(n, t)))
                    {
                        foundUnmuted = true;
                        unmutedHit = text;
                    }
                }

                if (foundMuted)
                {
                    reason = $"MUTEADO. Ventana='{winName}'. Match='{mutedHit}'";
                    return false;
                }

                if (foundUnmuted)
                {
                    reason = $"ABIERTO. Ventana='{winName}'. Match='{unmutedHit}'";
                    return true;
                }
            }

            reason = scanned == 0 ? "no encontré ventana de Zoom" : "ventana Zoom encontrada, pero no encontré Reactivar/Silenciar";
            return null;
        }
        catch (Exception ex)
        {
            reason = "error UI Automation: " + ex.Message;
            return null;
        }
    }

    private static bool IsZoomWindow(AutomationElement win)
    {
        try
        {
            var name = Normalize(SafeName(win));
            var cls = Normalize(SafeClass(win));
            var pid = win.Cached.ProcessId;
            var proc = GetCachedProcessName(pid);

            return name.Contains("zoom") || cls.Contains("zoom") || proc.Contains("zoom") || proc.Contains("cpt") || proc.Contains("zcef");
        }
        catch { return false; }
    }

    private static string BuildText(AutomationElement el)
    {
        try
        {
            var p = el.Cached;
            return string.Join(" | ", new[]
            {
                p.Name,
                p.HelpText,
                p.AutomationId,
                p.ClassName,
                p.LocalizedControlType
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch { return ""; }
    }

    private static string SafeName(AutomationElement el) { try { return el.Cached.Name ?? ""; } catch { return ""; } }
    private static string SafeClass(AutomationElement el) { try { return el.Cached.ClassName ?? ""; } catch { return ""; } }
    private static string Normalize(string s) => (s ?? "").Trim().ToLowerInvariant();
}

public static class WindowsMicDetector
{
    private const string MicRoot = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    // Registry.GetValue returns long for REG_QWORD and int for REG_DWORD; handle both.
    private static long? ToLong(object? v) => v switch { long l => l, int i => (long)i, _ => null };

    public static bool IsMicrophoneInUse(out string reason)
    {
        reason = "no activo";
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(MicRoot);
            if (root == null)
            {
                reason = "no existe key microphone";
                return false;
            }

            foreach (var hit in Walk(root, "HKCU\\" + MicRoot))
            {
                reason = hit;
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = "error registry: " + ex.Message;
        }
        return false;
    }

    private static IEnumerable<string> Walk(RegistryKey key, string path)
    {
        object? stop = null;
        try { stop = key.GetValue("LastUsedTimeStop"); } catch { }
        // Short-circuit: only read start when stop is already 0 (possible active use).
        // Requiring start != 0 avoids false positives from stale zero-stop keys left by crashed apps.
        if (ToLong(stop) == 0L)
        {
            object? start = null;
            try { start = key.GetValue("LastUsedTimeStart"); } catch { }
            if (ToLong(start) is long startLong && startLong != 0L)
                yield return path;
        }

        string[] names;
        try { names = key.GetSubKeyNames(); } catch { yield break; }
        foreach (var name in names)
        {
            RegistryKey? sub = null;
            try { sub = key.OpenSubKey(name); } catch { }
            if (sub == null) continue;
            using (sub)
            {
                foreach (var hit in Walk(sub, path + "\\" + name)) yield return hit;
            }
        }
    }
}
