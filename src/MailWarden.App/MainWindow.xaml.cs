using System.ComponentModel;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Storage;
using MailWarden.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MailWarden.App;

/// <summary>
/// Main window: navigation on the left, message list and reading pane switchable between columns and rows. Only wires
/// controls to the <see cref="ShellViewModel"/>; the behaviour lives there.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string BrowserDataDirectoryName = "webview2";
    private const double SplitterThickness = 6;

    // The next page loads this many pixels before the end of the list.
    private const double LoadMoreDistance = 400;

    private readonly MailWebViewHost webViewHost;
    private readonly List<ThemeOption> themeOptions =
    [
        new(AppTheme.System, UiText.ThemeSystem),
        new(AppTheme.Light, UiText.ThemeLight),
        new(AppTheme.Dark, UiText.ThemeDark),
    ];

    private ScrollViewer? messageListScroller;
    private double dragStartNavigationWidth;
    private double dragStartListSize;
    private double draggedListSize;
    private bool isBrowserUnavailable;
    private bool isListDragActive;
    private bool isNavigationDragActive;

    /// <summary>Creates the window.</summary>
    /// <param name="viewModel">The shell.</param>
    /// <param name="paths">Data folder; the browser keeps its data below it.</param>
    /// <param name="log">Critical log.</param>
    public MainWindow(ShellViewModel viewModel, DataPaths paths, ICriticalLog log)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Title = UiText.AppTitle;
        ToggleLayoutButton.Content = UiText.ToggleLayout;
        RefreshButton.Content = UiText.Refresh;
        ThemeLabel.Text = UiText.ThemeLabel;
        ThemeChoice.ItemsSource = themeOptions;
        ThemeChoice.DisplayMemberPath = nameof(ThemeOption.Label);
        ThemeChoice.SelectedItem = themeOptions.First(option => option.Theme == ViewModel.Theme);

        webViewHost = new MailWebViewHost(ReadingWebView, Path.Combine(paths.Root, BrowserDataDirectoryName), log);
        webViewHost.InitializationFailed += OnBrowserInitializationFailed;

        NavigationSplitter.Dragged += OnNavigationSplitterDragged;
        NavigationSplitter.DragCompleted += OnNavigationSplitterDragCompleted;
        ListSplitter.Dragged += OnListSplitterDragged;
        ListSplitter.DragCompleted += OnListSplitterDragCompleted;

        ViewModel.PropertyChanged += OnShellPropertyChanged;
        ViewModel.Navigation.PropertyChanged += OnNavigationPropertyChanged;
        ViewModel.Messages.PropertyChanged += OnMessagesPropertyChanged;
        ViewModel.Reading.PropertyChanged += OnReadingPropertyChanged;

        ApplyTheme();
        ApplyLayout();
        NavigationColumn.Width = new GridLength(ViewModel.NavigationWidth);
        UpdateReadingView();
    }

    /// <summary>The shell the bindings of this window read from.</summary>
    public ShellViewModel ViewModel { get; }

    /// <summary>Ends the browser processes; called when the application shuts down.</summary>
    public void CloseReadingView() => webViewHost.Close();

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            T? match = child as T ?? FindDescendant<T>(child);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ShellViewModel.Layout))
        {
            ApplyLayout();
        }
        else if (args.PropertyName == nameof(ShellViewModel.Theme))
        {
            ApplyTheme();
        }
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ViewModel.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    // Only moves the three parts inside the same grid, so the browser control keeps its document.
    private void ApplyLayout()
    {
        bool isSideBySide = ViewModel.Layout == ReadingPaneLayout.SideBySide;
        ListSplitter.IsHorizontalBar = !isSideBySide;

        PaneColumnSplitter.Width = new GridLength(isSideBySide ? SplitterThickness : 0);
        PaneColumnLast.Width = isSideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PaneRowSplitter.Height = new GridLength(isSideBySide ? 0 : SplitterThickness);
        PaneRowLast.Height = isSideBySide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ApplyListSize(ViewModel.ListSize);

        Grid.SetColumn(ListSplitter, isSideBySide ? 1 : 0);
        Grid.SetRow(ListSplitter, isSideBySide ? 0 : 1);
        Grid.SetColumn(ReadingPane, isSideBySide ? 2 : 0);
        Grid.SetRow(ReadingPane, isSideBySide ? 0 : 2);
    }

    private void ApplyListSize(double size)
    {
        bool isSideBySide = ViewModel.Layout == ReadingPaneLayout.SideBySide;
        PaneColumnFirst.Width = isSideBySide ? new GridLength(size) : new GridLength(1, GridUnitType.Star);
        PaneRowFirst.Height = isSideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(size);
    }

    private void OnThemeChoiceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ThemeChoice.SelectedItem is ThemeOption option)
        {
            ViewModel.Theme = option.Theme;
        }
    }

    // The sizes change live; the view model (and with it the settings file) only sees the final value.
    private void OnNavigationSplitterDragged(object? sender, double distance)
    {
        if (!isNavigationDragActive)
        {
            isNavigationDragActive = true;
            dragStartNavigationWidth = NavigationColumn.ActualWidth;
        }

        NavigationColumn.Width = new GridLength(Math.Clamp(dragStartNavigationWidth + distance, UiSettings.MinimumSize, UiSettings.MaximumSize));
    }

    private void OnNavigationSplitterDragCompleted(object? sender, EventArgs args)
    {
        if (isNavigationDragActive)
        {
            isNavigationDragActive = false;
            ViewModel.NavigationWidth = NavigationColumn.Width.Value;
        }
    }

    private void OnListSplitterDragged(object? sender, double distance)
    {
        if (!isListDragActive)
        {
            isListDragActive = true;
            dragStartListSize = ViewModel.ListSize;
        }

        draggedListSize = Math.Clamp(dragStartListSize + distance, UiSettings.MinimumSize, UiSettings.MaximumSize);
        ApplyListSize(draggedListSize);
    }

    private void OnListSplitterDragCompleted(object? sender, EventArgs args)
    {
        if (isListDragActive)
        {
            isListDragActive = false;
            ViewModel.ListSize = draggedListSize;
        }
    }

    // A parent or account node is not selectable: the tree goes back to the selection of the view model.
    private void OnNavigationItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        ViewModel.Navigation.SelectedItem = args.InvokedItem as NavigationItem;
        NavigationTree.SelectedItem = ViewModel.Navigation.SelectedItem;
    }

    private void OnNavigationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NavigationViewModel.SelectedItem))
        {
            NavigationTree.SelectedItem = ViewModel.Navigation.SelectedItem;
        }
    }

    private void OnMessageListLoaded(object sender, RoutedEventArgs args)
    {
        messageListScroller = FindDescendant<ScrollViewer>(MessageList);
        if (messageListScroller is not null)
        {
            messageListScroller.ViewChanged += OnMessageListScrolled;
        }
    }

    private void OnMessageListScrolled(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (messageListScroller is ScrollViewer scroller
            && scroller.VerticalOffset >= scroller.ScrollableHeight - LoadMoreDistance)
        {
            // LoadMoreAsync logs its failures and ignores the call while a load runs.
            _ = ViewModel.Messages.LoadMoreAsync();
        }
    }

    // Replacing the row object of the selected message makes the control report "nothing selected" for a moment; that is
    // no user action as long as the view model still has the row.
    private void OnMessageListSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        MessageRowViewModel? selected = MessageList.SelectedItem as MessageRowViewModel;
        MessageRowViewModel? current = ViewModel.Messages.SelectedRow;
        if (selected is null && current is not null && ViewModel.Messages.Items.Contains(current))
        {
            MessageList.SelectedItem = current;
            return;
        }

        ViewModel.Messages.SelectedRow = selected;
    }

    private void OnMessagesPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MessageListViewModel.SelectedRow)
            && !ReferenceEquals(MessageList.SelectedItem, ViewModel.Messages.SelectedRow))
        {
            MessageList.SelectedItem = ViewModel.Messages.SelectedRow;
        }
    }

    private void OnReadingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ReadingPaneViewModel.State) or nameof(ReadingPaneViewModel.DocumentHtml))
        {
            UpdateReadingView();
        }
    }

    private void UpdateReadingView()
    {
        ReadingPaneViewModel reading = ViewModel.Reading;
        bool isDocumentReady = reading.State == ReadingState.Ready && reading.DocumentHtml is not null;
        bool showBrowser = isDocumentReady && !isBrowserUnavailable;
        ReadingWebView.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;
        ReadingStatusText.Visibility = showBrowser ? Visibility.Collapsed : Visibility.Visible;
        ReadingStatusText.Text = isDocumentReady ? UiText.ReadingViewUnavailable : reading.StatusText;

        if (showBrowser)
        {
            webViewHost.Show(reading.DocumentHtml!, reading.AreRemoteImagesAllowed);
        }
        else if (!isDocumentReady)
        {
            webViewHost.Clear();
        }
    }

    private void OnBrowserInitializationFailed(object? sender, EventArgs args)
    {
        isBrowserUnavailable = true;
        UpdateReadingView();
    }

    /// <summary>One entry of the theme choice.</summary>
    /// <param name="Theme">The theme.</param>
    /// <param name="Label">Its text.</param>
    private sealed record ThemeOption(AppTheme Theme, string Label);
}
