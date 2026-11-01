using System.ComponentModel;
using System.Windows.Input;
using MailWarden.Infrastructure.Logging;
using MailWarden.Infrastructure.Storage;
using MailWarden.Presentation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

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

    // Where the hint with the link target lies relative to the mouse, and its distance from the window edge.
    private const double LinkHintOffsetX = 12;
    private const double LinkHintOffsetY = 20;
    private const double LinkHintEdgeDistance = 8;

    // The hint wraps within the window, so that no part of a long target is cut off. Below the smaller value it would be unreadable anyway.
    private const double LinkHintMaximumWidth = 600;
    private const double LinkHintMinimumWidth = 100;

    // Order of the entries of the theme choice.
    private static readonly AppTheme[] ThemeOrder = [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    private readonly MailWebViewHost webViewHost;
    private readonly WindowPlacementKeeper placementKeeper;
    private readonly ICriticalLog log;

    private ScrollViewer? messageListScroller;
    private double dragStartNavigationWidth;
    private double dragStartListSize;
    private double draggedListSize;
    private bool isBrowserUnavailable;
    private bool isListDragActive;
    private bool isNavigationDragActive;
    private bool isSyncingListSelection;

    /// <summary>Creates the window.</summary>
    /// <param name="viewModel">The shell.</param>
    /// <param name="paths">Data folder; the browser keeps its data below it.</param>
    /// <param name="log">Critical log.</param>
    public MainWindow(ShellViewModel viewModel, DataPaths paths, ICriticalLog log)
    {
        ViewModel = viewModel;
        this.log = log;
        InitializeComponent();

        Title = UiText.AppTitle;
        ToggleLayoutButton.Content = UiText.ToggleLayout;
        RefreshButton.Content = UiText.Refresh;
        ThemeLabel.Text = UiText.ThemeLabel;
        ThemeChoice.Items.Add(new ComboBoxItem { Content = UiText.ThemeSystem });
        ThemeChoice.Items.Add(new ComboBoxItem { Content = UiText.ThemeLight });
        ThemeChoice.Items.Add(new ComboBoxItem { Content = UiText.ThemeDark });
        ThemeChoice.SelectedIndex = Array.IndexOf(ThemeOrder, ViewModel.Theme);
        ApplyActionTexts();

        webViewHost = new MailWebViewHost(ReadingWebView, Path.Combine(paths.Root, BrowserDataDirectoryName), log);
        webViewHost.InitializationFailed += OnBrowserInitializationFailed;
        webViewHost.StatusTextChanged += OnBrowserStatusTextChanged;
        placementKeeper = new WindowPlacementKeeper(this, ViewModel);

        NavigationSplitter.Dragged += OnNavigationSplitterDragged;
        NavigationSplitter.DragCompleted += OnNavigationSplitterDragCompleted;
        ListSplitter.Dragged += OnListSplitterDragged;
        ListSplitter.DragCompleted += OnListSplitterDragCompleted;

        ViewModel.PropertyChanged += OnShellPropertyChanged;
        ViewModel.Navigation.PropertyChanged += OnNavigationPropertyChanged;
        ViewModel.Messages.PropertyChanged += OnMessagesPropertyChanged;
        ViewModel.Reading.PropertyChanged += OnReadingPropertyChanged;
        ViewModel.Search.PropertyChanged += OnSearchPropertyChanged;
        ViewModel.ConfirmationRequested += OnConfirmationRequested;
        ViewModel.ActionRowsRemoved += OnActionRowsRemoved;

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

    /// <summary>Places the window as stored; called once before it is shown.</summary>
    public void ApplyStoredPlacement() => placementKeeper.ApplyStored();

    /// <summary>Stores size and position of the window; called when it closes.</summary>
    public void SaveWindowPlacement() => placementKeeper.SaveAndStop();

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

    private static bool IsKeyDown(VirtualKey key)
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }

    private void ApplyActionTexts()
    {
        MarkReadButton.Content = UiText.MarkRead;
        MarkUnreadButton.Content = UiText.MarkUnread;
        ToggleFlagButton.Content = UiText.ToggleFlag;
        DeleteButton.Content = UiText.Delete;
        MoveButton.Content = UiText.Move;
        EmptyFolderButton.Content = UiText.EmptyFolder;
        SettingsButton.Content = UiText.Settings;
        MarkReadOnSelectSwitch.Header = UiText.MarkReadOnSelect;
        FailedActionsButton.Content = UiText.FailedActionsTitle;
        SearchBox.PlaceholderText = UiText.SearchPlaceholder;
        ContextMarkRead.Text = UiText.MarkRead;
        ContextMarkUnread.Text = UiText.MarkUnread;
        ContextToggleFlag.Text = UiText.ToggleFlag;
        ContextDelete.Text = UiText.Delete;
        ContextDeletePermanently.Text = UiText.DeletePermanently;
        ContextMove.Text = UiText.Move;
        ContextEmptyFolder.Text = UiText.EmptyFolder;
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
        else if (args.PropertyName == nameof(ShellViewModel.MoveTargets))
        {
            FillMoveMenu(MoveToolbarMenu.Items);
            FillMoveMenu(ContextMove.Items);
        }
    }

    // A menu can already be open when the targets of a new selection arrive, so it follows the list instead of its opening.
    private void FillMoveMenu(IList<MenuFlyoutItemBase> menuItems)
    {
        menuItems.Clear();
        foreach (NavigationItem target in ViewModel.MoveTargets)
        {
            if (target.View?.FolderId is long targetFolderId)
            {
                var menuItem = new MenuFlyoutItem { Text = target.Label };
                menuItem.Click += (_, _) => ViewModel.MoveCommand.Execute(targetFolderId);
                menuItems.Add(menuItem);
            }
        }
    }

    // One dialog at a time: the shell asks the next question only after this answer. Everything but the button that goes on is no.
    private async void OnConfirmationRequested(object? sender, ConfirmationRequest request)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = UiText.ConfirmationTitle,
                Content = new TextBlock { Text = request.Text, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = UiText.ConfirmationAccept,
                CloseButtonText = UiText.ConfirmationCancel,
                DefaultButton = ContentDialogButton.Close,
            };
            ContentDialogResult result = await dialog.ShowAsync();
            request.Answer.TrySetResult(result == ContentDialogResult.Primary);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.Write(CriticalLogLevel.Critical, "MainWindow.OnConfirmationRequested", $"The confirmation failed: {exception.GetType().Name}");
            request.Answer.TrySetResult(false);
        }
    }

    // After the collection change, when the control has settled on the next row.
    private void OnActionRowsRemoved(object? sender, EventArgs args)
    {
        DispatcherQueue.TryEnqueue(FocusMessageList);
    }

    // So that the next key goes to the list again. Someone typing in the search box keeps the focus there.
    private void FocusMessageList()
    {
        if (IsFocusInSearchBox())
        {
            return;
        }

        DependencyObject? selectedContainer = ViewModel.Messages.SelectedRow is MessageRowViewModel row ? MessageList.ContainerFromItem(row) : null;
        UIElement focusTarget = selectedContainer as UIElement ?? MessageList;
        focusTarget.Focus(FocusState.Programmatic);
    }

    private bool IsFocusInSearchBox()
    {
        DependencyObject? element = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (ReferenceEquals(element, SearchBox))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void OnDiscardFailedActionClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is PendingActionRow row)
        {
            ViewModel.DiscardFailedActionCommand.Execute(row);
        }
    }

    // Only here: the keys belong to the list and fire neither in the search box nor in a dialog.
    private void OnMessageListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        ICommand? command = args.Key switch
        {
            VirtualKey.Delete => IsKeyDown(VirtualKey.Shift) ? ViewModel.DeletePermanentlyCommand : ViewModel.DeleteCommand,
            VirtualKey.Q when IsKeyDown(VirtualKey.Control) => ViewModel.MarkReadCommand,
            VirtualKey.U when IsKeyDown(VirtualKey.Control) => ViewModel.MarkUnreadCommand,
            _ => null,
        };
        if (command is null)
        {
            return;
        }

        args.Handled = true;

        // Holding a key acts once: repeats must not walk through the list and delete.
        if (!args.KeyStatus.WasKeyDown && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    // As in Outlook: the menu of a row outside the selection works on that row alone.
    private void OnMessageListContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is MessageRowViewModel row
            && !ViewModel.Messages.SelectedRows.Contains(row))
        {
            ViewModel.Messages.SelectedRows = [row];
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.Search.Query = args.QueryText;

        // SearchAsync logs its failures; without a token it does not throw.
        _ = ViewModel.Search.SearchAsync();
    }

    // Emptying the box is the way back to the view.
    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.Search.Query = sender.Text;
            if (string.IsNullOrWhiteSpace(sender.Text))
            {
                _ = ViewModel.Search.SearchAsync();
            }
        }
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SearchViewModel.Query) && SearchBox.Text != ViewModel.Search.Query)
        {
            SearchBox.Text = ViewModel.Search.Query;
        }
        else if (args.PropertyName == nameof(SearchViewModel.IsActive))
        {
            ApplyNavigationHighlight();
        }
    }

    // During a search the selection of the navigation stays, but it is not what the list shows.
    private void ApplyNavigationHighlight()
    {
        NavigationTree.SelectedItem = ViewModel.Search.IsActive ? null : ViewModel.Navigation.SelectedItem;
    }

    private void OnBrowserStatusTextChanged(object? sender, string statusText)
    {
        ViewModel.Reading.HoverLinkText = statusText;
    }

    // At the mouse; when Windows reports no position, at the lower left, so that the target stays visible.
    private void ApplyLinkHint()
    {
        string? linkText = ViewModel.Reading.HoverLinkText;
        LinkHint.Visibility = linkText is null ? Visibility.Collapsed : Visibility.Visible;
        if (linkText is null)
        {
            return;
        }

        LinkHintText.Text = linkText;
        LinkHint.MaxWidth = Math.Max(LinkHintMinimumWidth, Math.Min(LinkHintMaximumWidth, RootGrid.ActualWidth - (2 * LinkHintEdgeDistance)));
        LinkHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size hintSize = LinkHint.DesiredSize;
        double left = LinkHintEdgeDistance;
        double top = RootGrid.ActualHeight - hintSize.Height - LinkHintEdgeDistance;
        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (NativeDisplay.TryGetCursorInClient(windowHandle, out int cursorX, out int cursorY))
        {
            double scale = RootGrid.XamlRoot.RasterizationScale;
            double mouseX = cursorX / scale;
            double mouseY = cursorY / scale;
            left = Math.Max(LinkHintEdgeDistance, Math.Min(mouseX + LinkHintOffsetX, RootGrid.ActualWidth - hintSize.Width - LinkHintEdgeDistance));
            top = mouseY + LinkHintOffsetY + hintSize.Height > RootGrid.ActualHeight
                ? Math.Max(LinkHintEdgeDistance, mouseY - hintSize.Height - LinkHintEdgeDistance)
                : mouseY + LinkHintOffsetY;
        }

        Canvas.SetLeft(LinkHint, left);
        Canvas.SetTop(LinkHint, top);
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
        var invokedItem = args.InvokedItem as NavigationItem;
        ViewModel.Navigation.SelectedItem = invokedItem;

        // The view that is already selected loads nothing by itself, so a click on it has to end the search here.
        if (invokedItem?.View is not null && ViewModel.Search.IsActive)
        {
            ViewModel.Search.Query = string.Empty;
            _ = ViewModel.Search.SearchAsync();
        }

        ApplyNavigationHighlight();
    }

    // Mouse and context key. A menu only for an entry that can be emptied; the others show none, not an empty one. Handled in any case:
    // the menu of this entry must not also be asked of the one above it. The selection of the tree stays.
    private void OnNavigationItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        if (sender is not TreeViewItem treeItem
            || NavigationTree.ItemFromContainer(treeItem) is not NavigationItem { CanEmptyFolder: true, View: MailView view })
        {
            return;
        }

        var menuItem = new MenuFlyoutItem { Text = UiText.EmptyFolder };
        menuItem.Click += (_, _) => ViewModel.EmptyFolderOfViewCommand.Execute(view);
        var menu = new MenuFlyout();
        menu.Items.Add(menuItem);
        if (args.TryGetPosition(treeItem, out Point position))
        {
            menu.ShowAt(treeItem, new FlyoutShowOptions { Position = position });
        }
        else
        {
            menu.ShowAt(treeItem);
        }
    }

    private void OnNavigationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NavigationViewModel.SelectedItem))
        {
            ApplyNavigationHighlight();
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

    // Replacing the row object of a selected message makes the control drop it from the selection for a moment. That is no
    // user action while the list still has a row of that message, so the pane and its image approval stay. The control gets
    // its selection back after the collection change, by then the view model holds the new row.
    private void OnMessageListSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isSyncingListSelection)
        {
            return;
        }

        IList<MessageRowViewModel> items = ViewModel.Messages.Items;
        bool isRowReplaced = args.RemovedItems.OfType<MessageRowViewModel>().Any(removedRow =>
            !items.Contains(removedRow) && items.Any(row => row.Message.Id == removedRow.Message.Id));
        if (isRowReplaced)
        {
            DispatcherQueue.TryEnqueue(SyncListSelection);
            return;
        }

        ViewModel.Messages.SelectedRows = MessageList.SelectedItems.OfType<MessageRowViewModel>().ToList();
    }

    // Brings the control to the selection of the view model, without reporting that back.
    private void SyncListSelection()
    {
        isSyncingListSelection = true;
        try
        {
            IReadOnlyList<MessageRowViewModel> wantedRows = ViewModel.Messages.SelectedRows;
            IList<object> selectedItems = MessageList.SelectedItems;
            for (int index = selectedItems.Count - 1; index >= 0; index--)
            {
                if (selectedItems[index] is not MessageRowViewModel row || !wantedRows.Contains(row))
                {
                    selectedItems.RemoveAt(index);
                }
            }

            foreach (MessageRowViewModel row in wantedRows)
            {
                if (!selectedItems.Contains(row) && ViewModel.Messages.Items.Contains(row))
                {
                    selectedItems.Add(row);
                }
            }
        }
        finally
        {
            isSyncingListSelection = false;
        }
    }

    private void OnMessagesPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MessageListViewModel.SelectedRows))
        {
            SyncListSelection();
        }
    }

    private void OnReadingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ReadingPaneViewModel.State) or nameof(ReadingPaneViewModel.DocumentHtml))
        {
            UpdateReadingView();
        }
        else if (args.PropertyName == nameof(ReadingPaneViewModel.HoverLinkText))
        {
            ApplyLinkHint();
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
