using System.Linq;

namespace SteamSwitcher.ViewModels;

public partial class MainViewModel
{
    public bool CanUseSelectedAccount => SelectedAccount != null && !IsLoading;

    public string AccountActionLabel => SelectedAccount switch
    {
        null => "请先选择账号",
        { IsArchived: true } => "找回并打开 Steam",
        { IsCurrent: true } => "打开当前账号的 Steam",
        _ => "切换账号并打开 Steam"
    };

    public string AccountActionHint => SelectedAccount switch
    {
        null => "先在左侧选择账号，再确认要执行的操作。",
        { IsArchived: true } => "将恢复账号记录；Steam 可能要求密码或验证码。",
        { IsCurrent: true } => "这是当前账号，直接打开 Steam，无需切换。",
        _ when IsSteamRunning => "将先关闭当前 Steam，再切换至选中的账号。",
        _ => "切换至选中的账号，然后打开 Steam。"
    };

    public int SelectedAccountBoundGameCount => SelectedAccount == null ? 0 :
        GameList.Count(game => game.HasBinding && game.BindingAccountSteamId == SelectedAccount.SteamId);

    private void NotifyAccountPresentation()
    {
        OnPropertyChanged(nameof(CanUseSelectedAccount));
        OnPropertyChanged(nameof(AccountActionLabel));
        OnPropertyChanged(nameof(AccountActionHint));
        OnPropertyChanged(nameof(SelectedAccountBoundGameCount));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseSelectedAccount));
        OnPropertyChanged(nameof(CanExportBackup));
    }
}
