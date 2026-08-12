using System.Text.Json;
using System.Text.Json.Serialization;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UAFScenarioAnalyzer;

internal readonly record struct ScenarioHistoryKey(int SingleModeCharaId, int Turn);

internal sealed class ScenarioHistory(
    IApplication application,
    string workspaceTitle,
    ScenarioHistorySettings settings) : IDisposable
{
    const string TrainingPanelKey = "training";

    readonly object gate = new();
    readonly List<Entry> entries = [];

    Workspace? workspace;
    WorkspaceContent? panelContent;
    WorkspaceContent? liveSnapshot;
    HistoryView? view;
    int historyLimit = settings.HistoryLimit;
    int selectedIndex = -1;
    bool hasUnread;
    bool panelPublished;
    volatile bool disposed;

    internal void Publish(ScenarioHistoryKey key, WorkspaceContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var refresh = false;
        var notifyUnread = false;
        Workspace target;
        lock (gate)
        {
            if (disposed)
                return;

            target = workspace ??= Workspace.Create(workspaceTitle);
            panelContent ??= new(CreateView);
            target.SetPanel(
                TrainingPanelKey,
                "训练分析",
                panelContent,
                fullBleed: true,
                switchToWorkspace: !panelPublished);
            panelPublished = true;

            liveSnapshot = content;
            if (historyLimit == 0)
            {
                entries.Clear();
                selectedIndex = -1;
                hasUnread = false;
                refresh = true;
            }
            else
            {
                var existingIndex = entries.FindIndex(entry => entry.Key == key);
                if (existingIndex >= 0)
                {
                    entries[existingIndex] = new(key, content);
                    refresh = existingIndex == selectedIndex;
                }
                else
                {
                    var wasFollowingLatest = selectedIndex < 0 || selectedIndex == entries.Count - 1;
                    entries.Add(new(key, content));
                    if (wasFollowingLatest)
                    {
                        selectedIndex = entries.Count - 1;
                        hasUnread = false;
                        refresh = true;
                    }
                    else if (!hasUnread)
                    {
                        hasUnread = true;
                        notifyUnread = true;
                    }

                    if (TrimLocked())
                    {
                        refresh = true;
                        notifyUnread = false;
                    }
                }
            }
        }

        if (refresh)
            Refresh();
        if (notifyUnread)
            target.Notify("有新的训练分析记录。按 → 查看最新。", UiSeverity.Info);
    }

    internal void ApplyLimit(int value)
    {
        ScenarioHistorySettings.Validate(value);
        lock (gate)
        {
            if (disposed)
                return;
            historyLimit = value;
            if (value == 0)
            {
                entries.Clear();
                selectedIndex = -1;
                hasUnread = false;
            }
            else
            {
                TrimLocked();
            }
        }
        Refresh();
    }

    public void Dispose()
    {
        HistoryView? publishedView;
        Workspace? target;
        bool removePanel;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            entries.Clear();
            liveSnapshot = null;
            selectedIndex = -1;
            hasUnread = false;
            publishedView = view;
            view = null;
            target = workspace;
            removePanel = panelPublished;
            panelPublished = false;
        }
        publishedView?.DetachKeyboard();

        if (removePanel)
            target!.RemovePanel(TrainingPanelKey);
    }

    bool TrimLocked()
    {
        var overflow = entries.Count - historyLimit;
        if (overflow <= 0)
            return false;

        if (selectedIndex < overflow)
        {
            entries.RemoveRange(0, overflow);
            selectedIndex = entries.Count - 1;
            hasUnread = false;
            return true;
        }

        entries.RemoveRange(0, overflow);
        selectedIndex -= overflow;
        return false;
    }

    bool Navigate(KeyCode keyCode)
    {
        int position;
        int count;
        lock (gate)
        {
            if (disposed || historyLimit == 0 || entries.Count == 0)
                return false;

            selectedIndex = keyCode switch
            {
                KeyCode.CursorUp => Math.Max(0, selectedIndex - 1),
                KeyCode.CursorDown => Math.Min(entries.Count - 1, selectedIndex + 1),
                KeyCode.CursorLeft => 0,
                KeyCode.CursorRight => entries.Count - 1,
                _ => selectedIndex,
            };
            if (selectedIndex == entries.Count - 1)
                hasUnread = false;

            position = selectedIndex + 1;
            count = entries.Count;
        }

        Refresh();
        workspace?.Notify($"训练分析历史 {position}/{count}", UiSeverity.Info);
        return true;
    }

    WorkspaceContent? SelectedContentLocked()
        => historyLimit > 0 && selectedIndex >= 0 && selectedIndex < entries.Count
            ? entries[selectedIndex].Content
            : liveSnapshot;

    View CreateView()
    {
        lock (gate)
        {
            if (disposed)
            {
                return new View
                {
                    Width = Dim.Fill(),
                    Height = Dim.Auto(),
                };
            }

            var created = new HistoryView(application, this);
            view = created;
            created.Show(SelectedContentLocked() ?? WorkspaceContent.Text(string.Empty));
            return created;
        }
    }

    void Refresh()
    {
        void Update()
        {
            lock (gate)
            {
                if (disposed || view is null || SelectedContentLocked() is not { } content)
                    return;

                view.Show(content);
                if (!panelPublished || workspace is null || panelContent is null)
                    return;
                workspace.SetPanel(
                    TrainingPanelKey,
                    "训练分析",
                    panelContent,
                    fullBleed: true,
                    switchToWorkspace: false);
            }
        }

        if (Environment.CurrentManagedThreadId == application.MainThreadId)
            Update();
        else
            application.Invoke(Update);
    }

    void ReleaseView(HistoryView released)
    {
        lock (gate)
        {
            if (ReferenceEquals(view, released))
                view = null;
        }
    }

    sealed record Entry(ScenarioHistoryKey Key, WorkspaceContent Content);

    sealed class HistoryView : View
    {
        readonly IApplication application;
        readonly ScenarioHistory owner;
        bool keyboardAttached = true;
        bool disposed;

        internal HistoryView(IApplication application, ScenarioHistory owner)
        {
            this.application = application;
            this.owner = owner;
            Width = Dim.Fill();
            Height = Dim.Auto();
            CanFocus = true;
            TabStop = TabBehavior.TabStop;
            application.Keyboard.KeyDown += ApplicationKeyDown;
            Initialized += (_, _) =>
            {
                if (ReferenceEquals(Workspace.Current, owner.workspace))
                    SetFocus();
            };
        }

        internal void Show(WorkspaceContent content)
        {
            if (disposed)
                return;

            var next = content.CreateView();
            next.X = 0;
            next.Y = 0;
            next.Width = Dim.Fill();
            next.Height = Dim.Auto();
            next.CanFocus = false;

            var previous = SubViews.FirstOrDefault();
            if (previous is not null)
            {
                Remove(previous);
                previous.Dispose();
            }
            Add(next);
            SetNeedsLayout();
            SetNeedsDraw();
        }

        internal void DetachKeyboard()
        {
            if (!keyboardAttached)
                return;
            keyboardAttached = false;
            application.Keyboard.KeyDown -= ApplicationKeyDown;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                DetachKeyboard();
                owner.ReleaseView(this);
            }
            base.Dispose(disposing);
        }

        void ApplicationKeyDown(object? sender, Key key)
        {
            if (key.Handled || key.IsCtrl || key.IsAlt || key.IsShift ||
                !ReferenceEquals(Workspace.Current, owner.workspace) ||
                !ContainsFocus())
            {
                return;
            }

            if (key.KeyCode is not (
                    KeyCode.CursorUp or
                    KeyCode.CursorDown or
                    KeyCode.CursorLeft or
                    KeyCode.CursorRight))
            {
                return;
            }

            if (owner.Navigate(key.KeyCode))
                key.Handled = true;
        }

        bool ContainsFocus()
        {
            for (var focused = application.TopRunnableView?.MostFocused;
                 focused is not null;
                 focused = focused.SuperView)
            {
                if (ReferenceEquals(focused, this))
                    return true;
            }
            return false;
        }
    }
}

internal sealed record ScenarioHistorySettings(
    [property: JsonRequired] int HistoryLimit)
{
    const string InternalName = "UAFScenarioAnalyzer";
    const int DefaultHistoryLimit = 100;
    const int MaximumHistoryLimit = 1000;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    static string SettingsDirectory => Path.Combine("PluginData", InternalName);
    static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    internal static ScenarioHistorySettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new(DefaultHistoryLimit);

        try
        {
            var settings = JsonSerializer.Deserialize<ScenarioHistorySettings>(
                    File.ReadAllText(SettingsPath),
                    JsonOptions)
                ?? throw new JsonException("配置内容不能是 null。");
            Validate(settings.HistoryLimit);
            return settings;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"UAFScenarioAnalyzer 配置文件无效: {SettingsPath}。{ex.Message}",
                ex);
        }
    }

    internal void Save()
    {
        Validate(HistoryLimit);
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    internal static void Validate(int value)
    {
        if (value is < 0 or > MaximumHistoryLimit)
        {
            throw new InvalidDataException(
                $"UAFScenarioAnalyzer historyLimit 必须在 0 到 {MaximumHistoryLimit} 之间，当前值: {value}。配置文件: {SettingsPath}");
        }
    }

    internal static async Task<ScenarioHistorySettings> EditAsync(
        IApplication application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        cancellationToken.ThrowIfCancellationRequested();
        if (application.TopRunnable is null &&
            Environment.CurrentManagedThreadId != application.MainThreadId)
        {
            throw new InvalidOperationException(
                "UAFScenarioAnalyzer 无法从非 UI thread 启动配置：Terminal.Gui 当前没有正在运行的 session。");
        }

        var draft = Load();
        if (Environment.CurrentManagedThreadId == application.MainThreadId)
            return RunDialog(application, draft, cancellationToken);

        var completion = new TaskCompletionSource<ScenarioHistorySettings>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        application.Invoke(() =>
        {
            try
            {
                completion.SetResult(RunDialog(application, draft, cancellationToken));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        var result = await completion.Task;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    static ScenarioHistorySettings RunDialog(
        IApplication application,
        ScenarioHistorySettings draft,
        CancellationToken cancellationToken)
    {
        using var dialog = new Dialog
        {
            Title = "UAFScenarioAnalyzer 配置",
            Width = 58,
            Height = 12,
        };
        var limit = new NumericUpDown<int>
        {
            X = 1,
            Y = 2,
            Width = 18,
            Value = draft.HistoryLimit,
            Increment = 1,
        };
        var validation = new Label
        {
            X = 1,
            Y = 5,
            Width = Dim.Fill(1),
            Height = 2,
        };
        dialog.Add(
            new Label { X = 1, Y = 1, Text = "History 保存上限（0 表示关闭）" },
            limit,
            new Label { X = 21, Y = 2, Text = "范围：0–1000" },
            validation);

        var accepted = false;
        var save = new Button { Text = "保存", IsDefault = true };
        save.Accepting += (_, e) =>
        {
            if (limit.Value is < 0 or > MaximumHistoryLimit)
            {
                validation.Text = "History 上限必须是 0 到 1000。";
                e.Handled = true;
                return;
            }
            accepted = true;
            application.RequestStop(dialog);
            e.Handled = true;
        };
        var cancel = new Button { Text = "取消" };
        cancel.Accepting += (_, e) =>
        {
            application.RequestStop(dialog);
            e.Handled = true;
        };
        dialog.AddButton(cancel);
        dialog.AddButton(save);
        limit.SetFocus();

        using (cancellationToken.Register(
                   () => application.Invoke(() => application.RequestStop(dialog))))
            application.Run(dialog);
        cancellationToken.ThrowIfCancellationRequested();
        if (!accepted)
        {
            throw new OperationCanceledException(
                "UAFScenarioAnalyzer 配置已取消。",
                cancellationToken);
        }
        return new(limit.Value);
    }
}
