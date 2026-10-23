namespace ClaudeCL.Pages;

/// <summary>
/// 页面在每次显示时收到通知，用于刷新数据
/// </summary>
public interface IPage
{
    void OnShown();
}
