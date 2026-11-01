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

    // The list never pushes the reading pane below this size.
    private const double MinimumReadingPaneSize = 200;

    // Order of the entries of the theme choice.
    private static readonly AppTheme[] ThemeOrder = [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    private readonly MailWebViewHost webViewHost;

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
        ThemeChoice.Items.Add(new ComboBoxItem { Content = UiText.ThemeSystem });
        ThemeChoice.Items.Add(new ComboBoxItem { Content = UiText.ThemeLight });
        ThemeChoice.Items.Add(new ComboBoxItem { Content = UiText.ThemeDark });
        ThemeChoice.SelectedIndex = Array.IndexOf(ThemeOrder, ViewModel.Theme);

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

        // Once here: a settings load error set the banner before this window existed.
        ApplyBanner();
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
        else if (args.PropertyName == nameof(ShellViewModel.Banner))
        {
            ApplyBanner();
        }
    }

    private void ApplyBanner()
    {
        BannerBar.Message = ViewModel.Banner ?? string.Empty;
        BannerBar.IsOpen = ViewModel.Banner is not null;
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
        ApplyListSize(StoredListSize());

        Grid.SetColumn(ListSplitter, isSideBySide ? 1 : 0);
        Grid.SetRow(ListSplitter, isSideBySide ? 0 : 1);
        Grid.SetColumn(ReadingPane, isSideBySide ? 2 : 0);
        Grid.SetRow(ReadingPane, isSideBySide ? 0 : 2);
    }

    // Each layout keeps its own size in the view model.
    private double StoredListSize()
    {
        return ViewModel.Layout == ReadingPaneLayout.SideBySide ? ViewModel.ListWidth : ViewModel.ListHeight;
    }

    // Before the first layout the available size is unknown; SizeChanged applies the limit then.
    private double LimitListSize(double size)
    {
        double available = ViewModel.Layout == ReadingPaneLayout.SideBySide ? MessagePanes.ActualWidth : MessagePanes.ActualHeight;
        return available <= 0 ? size : Math.Min(size, Math.Max(0, available - SplitterThickness - MinimumReadingPaneSize));
    }

    private void ApplyListSize(double size)
    {
        bool isSideBySide = ViewModel.Layout == ReadingPaneLayout.SideBySide;
        double limitedSize = LimitListSize(size);
        PaneColumnFirst.Width = isSideBySide ? new GridLength(limitedSize) : new GridLength(1, GridUnitType.Star);
        PaneRowFirst.Height = isSideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(limitedSize);
    }

    private void OnMessagePanesSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ApplyListSize(isListDragActive ? draggedListSize : StoredListSize());
    }

    private void OnThemeChoiceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ThemeChoice.SelectedIndex >= 0)
        {
            ViewModel.Theme = ThemeOrder[ThemeChoice.SelectedIndex];
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
            dragStartListSize = LimitListSize(StoredListSize());
        }

        draggedListSize = LimitListSize(Math.Clamp(dragStartListSize + distance, UiSettings.MinimumSize, UiSettings.MaximumSize));
        ApplyListSize(draggedListSize);
    }

    private void OnListSplitterDragCompleted(object? sender, EventArgs args)
    {
        if (isListDragActive)
        {
            isListDragActive = false;
            if (ViewModel.Layout == ReadingPaneLayout.SideBySide)
            {
                ViewModel.ListWidth = draggedListSize;
            }
            else
            {
                ViewModel.ListHeight = draggedListSize;
            }
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

    // Replacing the row object of the selected message makes the control report "nothing selected" for a moment. That is
    // no user action while the list still has a row of that message, so the pane and its image approval stay. The control
    // gets its selection back after the collection change, by then the view model holds the new row.
    private void OnMessageListSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        MessageRowViewModel? selected = MessageList.SelectedItem as MessageRowViewModel;
        MessageRowViewModel? current = ViewModel.Messages.SelectedRow;
        if (selected is null
            && current is not null
            && ViewModel.Messages.Items.Any(row => row.Message.Id == current.Message.Id))
        {
            DispatcherQueue.TryEnqueue(RestoreListSelection);
            return;
        }

        ViewModel.Messages.SelectedRow = selected;
    }

    private void RestoreListSelection()
    {
        if (MessageList.SelectedItem is null)
        {
            MessageList.SelectedItem = ViewModel.Messages.SelectedRow;
        }
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
}
