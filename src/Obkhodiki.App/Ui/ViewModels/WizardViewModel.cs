using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Obkhodiki.App.Ui.ViewModels;

/// <summary>
/// First-run guide. Every step works through the same view models as the pages (auto-select, game catalog,
/// VPS source, appearance), so whatever is done here is exactly what the pages show afterwards.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    public const int StepCount = 5;

    public WizardViewModel(ShellViewModel shell) => Shell = shell;

    public ShellViewModel Shell { get; }

    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(StepCaption), nameof(StepText), nameof(IsFirst), nameof(IsLast),
        nameof(IsWelcome), nameof(IsStrategy), nameof(IsGames), nameof(IsVpn), nameof(IsLook), nameof(NextText))]
    private int _step;

    public bool IsWelcome => Step == 0;
    public bool IsStrategy => Step == 1;
    public bool IsGames => Step == 2;
    public bool IsVpn => Step == 3;
    public bool IsLook => Step == 4;
    public bool IsFirst => Step == 0;
    public bool IsLast => Step == StepCount - 1;
    public string StepText => $"Шаг {Step + 1} из {StepCount}";
    public string NextText => IsLast ? "Готово" : IsWelcome ? "Начать" : "Далее";

    public string StepTitle => Step switch
    {
        0 => "Привет! Это Obkhodiki",
        1 => "Подбор стратегии обхода",
        2 => "Во что играешь?",
        3 => "Есть свой VPS или подписка?",
        _ => "Оформление",
    };

    public string StepCaption => Step switch
    {
        0 => "Программа обходит блокировки (YouTube, Discord и другие) через zapret, помогает играм и умеет пускать выбранное через ваш VPS. Настроим всё за пару минут — любой шаг можно пропустить и вернуться к нему позже.",
        1 => "Программа по очереди пробует стратегии Flowseal и останавливается на первой, которая открывает все проверочные сайты у вашего провайдера. Обычно это меньше минуты.",
        2 => "Добавьте свои игры: правила обхода применятся только к их серверам. «Работает сразу» — адреса известны заранее, для остальных потом нужно записать один матч на странице «Игры».",
        3 => "Вставьте ссылку подписки (3x-ui, Remnawave и др.) или одного сервера: vless://, hysteria2://, trojan://, ss://. Ссылка хранится зашифрованной. Нет VPS — просто пропустите.",
        _ => "Тема и цвет. Поменять можно в любой момент в «Настройках».",
    };

    [RelayCommand]
    private void Next()
    {
        if (IsLast)
        {
            Finish();
            return;
        }
        Step++;
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0) Step--;
    }

    [RelayCommand]
    private void Finish()
    {
        // A running auto-select keeps running on the Bypass page; the guide only closes.
        CloseRequested?.Invoke();
    }
}
