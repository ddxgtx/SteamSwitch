using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher;

public partial class MainWindow
{
    private ICollectionView? _accountResultsView;

    private void InitializeAccountLibraryFeedback()
    {
        if (Resources["FilteredAccounts"] is not CollectionViewSource source || source.View == null) return;
        _accountResultsView = source.View;
        // Keep filtered results and pinned-first sorting in step with item changes,
        // without refreshing the entire library for each property notification.
        if (_accountResultsView is ICollectionViewLiveShaping live)
        {
            if (live.CanChangeLiveFiltering)
            {
                foreach (var property in new[] { nameof(AccountViewModel.IsPinned), nameof(AccountViewModel.IsCurrent), nameof(AccountViewModel.IsArchived) })
                    live.LiveFilteringProperties.Add(property);
                live.IsLiveFiltering = true;
            }
            if (live.CanChangeLiveSorting) live.IsLiveSorting = true;
        }
        _accountResultsView.CollectionChanged += AccountResults_CollectionChanged;
        UpdateAccountLibraryFeedback();
    }

    private void AccountResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateAccountLibraryFeedback();

    private void DetachAccountLibraryFeedback()
    {
        if (_accountResultsView != null) _accountResultsView.CollectionChanged -= AccountResults_CollectionChanged;
        _accountResultsView = null;
    }

    private void UpdateAccountLibraryFeedback()
    {
        // TextChanged can run while InitializeComponent is still creating the list.
        if (AccountResultsSummary == null || _accountResultsView == null) return;
        var count = _accountResultsView is CollectionView collection ? collection.Count : _accountResultsView.Cast<object>().Count();
        var total = _viewModel.Accounts.Count;
        var searching = !_accountSearchPlaceholder && !string.IsNullOrWhiteSpace(AccountSearchBox.Text);
        ClearAccountSearchButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        AccountResultsSummary.Text = searching
            ? $"搜索结果 {count} 个 / 本地收录 {total} 个 · {_accountFilterMode switch { "Archived" => "待找回", "Pinned" => "已固定", "Current" => "当前账号", _ => "全部账号" }}"
            : $"显示 {count} 个 / 本地收录 {total} 个";
        AccountResultsEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountResultsEmptyTitle.Text = total == 0 ? "开始建立你的多账号库" : searching ? "没有找到匹配的账号" : "当前筛选下没有账号";
        AccountResultsEmptyHint.Text = total == 0
            ? "先在 Steam 登录账号，再点击收录；也可以从上方导入已有账号库。"
            : searching ? "试试昵称、登录名或 Steam ID。搜索只在当前筛选范围内进行，也可以清除条件查看全部。"
            : _accountFilterMode switch
            {
                "Pinned" => "暂未固定账号。查看全部后，点击账号旁的星标，即可建立常用入口。",
                "Archived" => "目前没有待找回账号，已收录的资料仍保留在本地库中。",
                "Current" => "目前没有标记为当前登录的账号，可以查看全部已收录账号。",
                _ => "点击收录账号重新读取 Steam 记录，或导入已有账号库。"
            };
        ResetAccountFiltersButton.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyCollectAccountsButton.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_viewModel.SelectedAccount != null && !_accountResultsView.Contains(_viewModel.SelectedAccount))
            _viewModel.SelectedAccount = null;
    }

    private void ClearAccountSearch_Click(object sender, RoutedEventArgs e)
    {
        _accountSearchPlaceholder = false;
        AccountSearchBox.SetResourceReference(Control.ForegroundProperty, "SearchTextFgBrush");
        AccountSearchBox.Text = string.Empty;
        AccountSearchBox.Focus();
    }

    private void AccountLibrary_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (AccountView.Visibility != Visibility.Visible) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            AccountSearchBox.Focus();
            AccountSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && AccountSearchBox.IsKeyboardFocusWithin && !_accountSearchPlaceholder && !string.IsNullOrWhiteSpace(AccountSearchBox.Text))
        {
            ClearAccountSearch_Click(sender, e);
            e.Handled = true;
        }
    }
}
