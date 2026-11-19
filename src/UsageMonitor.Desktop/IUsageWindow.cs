using UsageMonitor.Models;

namespace UsageMonitor;

/// <summary>
/// 사용량을 보여주는 창. 고정 바(BarWindow)와 플로팅 카드(CardWindow)가 구현하고, App은 공통 동작을 이 인터페이스로 다룬다.
/// Opacity, Show, Hide, Close는 Window에 이미 있는 멤버가 그대로 구현한다.
/// </summary>
public interface IUsageWindow
{
    double Opacity { get; set; }

    /// <summary>true면 마우스 입력이 아래 창으로 통과한다.</summary>
    bool ClickThrough { get; set; }

    void SetToolVisible(string tool, bool visible);
    void Apply(IReadOnlyList<UsageSnapshot> snapshots);
    void Show();
    void Hide();
    void Close();
}
