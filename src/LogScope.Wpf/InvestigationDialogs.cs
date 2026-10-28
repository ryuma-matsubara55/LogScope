using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LogScope.Domain;

namespace LogScope.Wpf;

internal sealed class InvestigationPickerWindow : Window
{
    private readonly ListBox _items;
    public int? SelectedInvestigationId { get; private set; }

    public InvestigationPickerWindow(IReadOnlyList<Investigation> investigations)
    {
        Title = "保存した調査を開く";
        InvestigationDialogAppearance.Configure(this, 640, 500, 480, 320);
        var root = InvestigationDialogAppearance.Layout();
        root.Children.Add(InvestigationDialogAppearance.Header(Title, "続きから再開する調査を選んでください。"));
        _items = new ListBox
        {
            Margin = new Thickness(24, 0, 24, 20), ItemsSource = investigations,
            ItemTemplate = CreateTemplate(), HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _items.SetResourceReference(Control.BackgroundProperty, "InputBackgroundBrush");
        _items.SetResourceReference(Control.ForegroundProperty, "PrimaryForegroundBrush");
        _items.SetResourceReference(Control.BorderBrushProperty, "ControlBorderBrush");
        AutomationProperties.SetName(_items, "保存した調査");
        Grid.SetRow(_items, 1);
        root.Children.Add(_items);
        var open = InvestigationDialogAppearance.Button("調査を開く", primary: true);
        open.IsDefault = true;
        open.IsEnabled = false;
        open.Click += (_, _) => Accept();
        _items.SelectionChanged += (_, _) => open.IsEnabled = _items.SelectedItem is Investigation;
        _items.MouseDoubleClick += (_, _) => Accept();
        var cancel = InvestigationDialogAppearance.Button("キャンセル");
        cancel.IsCancel = true;
        var footer = InvestigationDialogAppearance.Footer([cancel, open]);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => _items.Focus();
    }

    private static DataTemplate CreateTemplate()
    {
        var template = new DataTemplate(typeof(Investigation));
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(8));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Investigation.Title)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var summary = new FrameworkElementFactory(typeof(TextBlock));
        summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(Investigation.Summary)));
        summary.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        summary.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0));
        summary.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForegroundBrush");
        var updated = new FrameworkElementFactory(typeof(TextBlock));
        updated.SetBinding(TextBlock.TextProperty, new Binding(nameof(Investigation.UpdatedAt)) { StringFormat = "更新: {0:yyyy/MM/dd HH:mm}" });
        updated.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 0));
        updated.SetValue(TextBlock.FontSizeProperty, 11d);
        updated.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForegroundBrush");
        panel.AppendChild(title);
        panel.AppendChild(summary);
        panel.AppendChild(updated);
        template.VisualTree = panel;
        return template;
    }

    private void Accept()
    {
        if (_items.SelectedItem is not Investigation selected) return;
        SelectedInvestigationId = selected.InvestigationId;
        DialogResult = true;
    }
}

internal sealed class InvestigationDetailsWindow : Window
{
    private readonly TextBox[] _fields;
    private readonly TextBlock _titleError;
    private static readonly (string Label, string Property)[] FieldDefinitions =
    {
        ("調査名", nameof(Investigation.Title)),
        ("概要", nameof(Investigation.Summary)),
        ("発生事象", nameof(Investigation.IncidentDescription)),
        ("原因仮説", nameof(Investigation.Hypothesis)),
        ("確認済み原因", nameof(Investigation.ConfirmedCause)),
        ("対応", nameof(Investigation.ActionsTaken)),
        ("未解決事項", nameof(Investigation.UnresolvedItems)),
        ("結論", nameof(Investigation.Conclusion))
    };

    public Investigation Draft { get; }

    public InvestigationDetailsWindow(Investigation source, bool isNew = false, bool saveOnConfirm = false)
    {
        Draft = new Investigation
        {
            InvestigationId = source.InvestigationId, Title = source.Title, Summary = source.Summary,
            IncidentDescription = source.IncidentDescription, Hypothesis = source.Hypothesis,
            ConfirmedCause = source.ConfirmedCause, ActionsTaken = source.ActionsTaken,
            UnresolvedItems = source.UnresolvedItems, Conclusion = source.Conclusion,
            CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt
        };
        Title = saveOnConfirm ? "調査を保存" : isNew ? "新しい調査を作成" : "調査情報を編集";
        InvestigationDialogAppearance.Configure(this, 720, isNew ? 480 : 660, 520, 400);
        var root = InvestigationDialogAppearance.Layout();
        root.Children.Add(InvestigationDialogAppearance.Header(Title,
            isNew ? "名前を付けて調査を始めましょう。詳細はあとから追記できます。" : "調査名や、これまでに分かったことを更新できます。"));
        var form = new StackPanel { Margin = new Thickness(24, 0, 24, 20) };
        var titleLabel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        titleLabel.Children.Add(new TextBlock { Text = "調査名", FontWeight = FontWeights.SemiBold });
        var required = new TextBlock { Text = "必須", FontSize = 11, Margin = new Thickness(8, 1, 0, 0) };
        required.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForegroundBrush");
        titleLabel.Children.Add(required);
        form.Children.Add(titleLabel);

        _fields = new TextBox[FieldDefinitions.Length];
        for (int i = 0; i < FieldDefinitions.Length; i++)
        {
            var box = new TextBox
            {
                Text = (string)(typeof(Investigation).GetProperty(FieldDefinitions[i].Property)!.GetValue(Draft) ?? string.Empty),
                MinHeight = i == 0 ? 40 : 84, MaxHeight = i == 0 ? 40 : 180,
                AcceptsReturn = i != 0, TextWrapping = i == 0 ? TextWrapping.NoWrap : TextWrapping.Wrap,
                VerticalScrollBarVisibility = i == 0 ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalContentAlignment = i == 0 ? VerticalAlignment.Center : VerticalAlignment.Top
            };
            AutomationProperties.SetName(box, FieldDefinitions[i].Label + (i == 0 ? "（必須）" : "（任意）"));
            AutomationProperties.SetAutomationId(box, "Investigation" + FieldDefinitions[i].Property);
            _fields[i] = box;
        }
        form.Children.Add(_fields[0]);
        _titleError = new TextBlock { Text = "調査名を入力してください。", Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _titleError.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        AutomationProperties.SetLiveSetting(_titleError, AutomationLiveSetting.Assertive);
        form.Children.Add(_titleError);
        var example = new TextBlock { Text = "例：APIタイムアウトの原因調査", Margin = new Thickness(0, 7, 0, 24), FontSize = 12 };
        example.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForegroundBrush");
        form.Children.Add(example);
        _fields[0].TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_fields[0].Text)) return;
            _titleError.Visibility = Visibility.Collapsed;
            _fields[0].ClearValue(Control.BorderBrushProperty);
        };

        var optionalFields = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var optionalHint = new TextBlock { Text = "すべて任意です。分かっている項目だけ入力してください。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        optionalHint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForegroundBrush");
        optionalFields.Children.Add(optionalHint);
        for (int i = 1; i < FieldDefinitions.Length; i++)
        {
            var label = new Label
            {
                Content = FieldDefinitions[i].Label, Target = _fields[i], FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 7)
            };
            label.SetResourceReference(Control.ForegroundProperty, "PrimaryForegroundBrush");
            optionalFields.Children.Add(label);
            _fields[i].Margin = new Thickness(0, 0, 0, 18);
            optionalFields.Children.Add(_fields[i]);
        }
        var details = new Expander
        {
            Header = "調査の詳細を入力（任意）", Content = optionalFields,
            IsExpanded = !isNew && _fields.Skip(1).Any(field => !string.IsNullOrWhiteSpace(field.Text)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        details.SetResourceReference(Control.ForegroundProperty, "PrimaryForegroundBrush");
        details.SetResourceReference(Control.BackgroundProperty, "WindowBackgroundBrush");
        AutomationProperties.SetAutomationId(details, "InvestigationOptionalDetails");
        double collapsedHeight = Height;
        details.Expanded += (_, _) => ResizeDetailsArea(740);
        details.Collapsed += (_, _) => ResizeDetailsArea(collapsedHeight);
        if (details.IsExpanded) Height = Math.Min(740, MaxHeight);
        form.Children.Add(details);
        var scroll = new ScrollViewer
        {
            Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var confirm = InvestigationDialogAppearance.Button(saveOnConfirm ? "調査を保存" : isNew ? "調査を作成" : "変更を反映", primary: true);
        confirm.IsDefault = true;
        confirm.Click += (_, _) => Accept();
        AutomationProperties.SetAutomationId(confirm, "ConfirmInvestigation");
        var cancel = InvestigationDialogAppearance.Button("キャンセル");
        cancel.IsCancel = true;
        var footer = InvestigationDialogAppearance.Footer([cancel, confirm], saveOnConfirm
            ? "ログ構成・検索状態・注記を保存します。ログ本文は含まれず、再開時にも元のファイルが必要です。"
            : "内容は「調査を保存」または Ctrl+S で保存できます。");
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => { _fields[0].Focus(); _fields[0].SelectAll(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            Accept();
        };
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_fields[0].Text))
        {
            _titleError.Visibility = Visibility.Visible;
            _fields[0].SetResourceReference(Control.BorderBrushProperty, "ErrorBrush");
            _fields[0].Focus();
            _fields[0].BringIntoView();
            return;
        }
        for (int i = 0; i < FieldDefinitions.Length; i++)
            typeof(Investigation).GetProperty(FieldDefinitions[i].Property)!.SetValue(Draft, _fields[i].Text.Trim());
        DialogResult = true;
    }

    private void ResizeDetailsArea(double requestedHeight)
    {
        double previousHeight = Height;
        Height = Math.Min(requestedHeight, MaxHeight);
        if (!IsLoaded || !double.IsFinite(Top)) return;
        var workArea = SystemParameters.WorkArea;
        double minimumTop = workArea.Top + 16;
        double maximumTop = Math.Max(minimumTop, workArea.Bottom - Height - 16);
        Top = Math.Clamp(Top + (previousHeight - Height) / 2, minimumTop, maximumTop);
    }
}

internal enum UnsavedDecision { Save, Discard, Cancel }

internal sealed class UnsavedChangesWindow : Window
{
    public UnsavedDecision Decision { get; private set; } = UnsavedDecision.Cancel;

    public UnsavedChangesWindow()
    {
        Title = "調査の変更を保存";
        InvestigationDialogAppearance.Configure(this, 500, 250, 440, 230);
        ResizeMode = ResizeMode.NoResize;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(InvestigationDialogAppearance.Header("変更を保存しますか？", "調査に未保存の変更があります。続ける前に保存できます。"));
        var buttons = new List<Button>();
        foreach (var pair in new[] { ("キャンセル", UnsavedDecision.Cancel), ("保存しない", UnsavedDecision.Discard), ("保存", UnsavedDecision.Save) })
        {
            var button = InvestigationDialogAppearance.Button(pair.Item1, pair.Item2 == UnsavedDecision.Save);
            button.IsDefault = pair.Item2 == UnsavedDecision.Save;
            button.IsCancel = pair.Item2 == UnsavedDecision.Cancel;
            button.Click += (_, _) => { Decision = pair.Item2; DialogResult = true; };
            buttons.Add(button);
        }
        var footer = InvestigationDialogAppearance.Footer(buttons);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;
    }
}

/// <summary>調査用ダイアログだけに適用し、メイン画面の暗色パレットと操作部を揃える。</summary>
internal static class InvestigationDialogAppearance
{
    public static void Configure(Window window, double width, double height, double minWidth, double minHeight)
    {
        var workArea = SystemParameters.WorkArea;
        window.MaxWidth = Math.Max(1, workArea.Width - 32);
        window.MaxHeight = Math.Max(1, workArea.Height - 32);
        window.MinWidth = Math.Min(minWidth, window.MaxWidth);
        window.MinHeight = Math.Min(minHeight, window.MaxHeight);
        window.Width = Math.Min(width, window.MaxWidth);
        window.Height = Math.Min(height, window.MaxHeight);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar = false;
        window.SetResourceReference(Control.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "PrimaryForegroundBrush");
        window.SetResourceReference(Control.FontFamilyProperty, "BodyFontFamily");
        window.FontSize = 13;
        window.UseLayoutRounding = true;
        var textBoxStyle = new Style(typeof(TextBox));
        textBoxStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("InputBackgroundBrush")));
        textBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryForegroundBrush")));
        textBoxStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("ControlBorderBrush")));
        textBoxStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        textBoxStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        textBoxStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty, new DynamicResourceExtension("PrimaryForegroundBrush")));
        textBoxStyle.Setters.Add(new Setter(TextBox.SelectionBrushProperty, new DynamicResourceExtension("SelectionBrush")));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("FocusBrush")));
        textBoxStyle.Triggers.Add(focus);
        window.Resources.Add(typeof(TextBox), textBoxStyle);
    }

    public static Grid Layout()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return grid;
    }

    public static FrameworkElement Header(string title, string description)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var caption = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0), LineHeight = 20 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForegroundBrush");
        panel.Children.Add(caption);
        return panel;
    }

    public static Button Button(string label, bool primary = false)
    {
        var button = new Button
        {
            Content = label, MinWidth = 100, MinHeight = 36, Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal
        };
        button.SetResourceReference(Control.BackgroundProperty, primary ? "FocusBrush" : "ButtonBackgroundBrush");
        button.SetResourceReference(Control.ForegroundProperty, primary ? "SelectionTextBrush" : "ButtonForegroundBrush");
        button.SetResourceReference(Control.BorderBrushProperty, primary ? "FocusBrush" : "ControlBorderBrush");
        var border = new FrameworkElementFactory(typeof(Border), "ButtonBorder");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "ButtonBorder"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("PrimaryForegroundBrush"), "ButtonBorder"));
        template.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ButtonBackgroundBrush"), "ButtonBorder"));
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "ButtonBorder"));
        template.Triggers.Add(disabled);
        button.Template = template;
        return button;
    }

    public static FrameworkElement Footer(IEnumerable<Button> actions, string? note = null)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 14, 24, 18) };
        if (note is not null)
        {
            var caption = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForegroundBrush");
            panel.Children.Add(caption);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in actions) buttons.Children.Add(action);
        panel.Children.Add(buttons);
        var border = new Border { Child = panel, BorderThickness = new Thickness(0, 1, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBackgroundBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "PanelDividerBrush");
        return border;
    }
}
