using System.Windows.Interop;
using System.Windows.Threading;
using QuickClip.Native;

namespace QuickClip.Services;

/// <summary>
/// 基于 AddClipboardFormatListener 的剪贴板变更监听。
///
/// 监听挂载在自建的隐藏消息窗口上，不依赖主窗口：开机自启动时主窗口不会显示（其 HWND 也不存在），
/// 若把监听挂到主窗口，就必须等用户手动 Win+V 唤起面板后才会开始记录剪贴板。
/// </summary>
public sealed class ClipboardMonitor
{
    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>剪贴板内容变化时触发（UI 线程）。</summary>
    public event Action? ClipboardUpdated;

    /// <summary>
    /// 创建隐藏消息窗口并注册剪贴板监听；可重复调用，实际建窗与销窗都切到 UI 线程执行。
    /// </summary>
    public void Attach(Dispatcher uiDispatcher)
    {
        uiDispatcher.Invoke(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                return;
            }

            var parameters = new HwndSourceParameters("QuickClipClipboardWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                HwndSourceHook = WndProc
            };
            _source = new HwndSource(parameters);
            _hwnd = _source.Handle;
            NativeMethods.AddClipboardFormatListener(_hwnd);
            DebugLog.Log($"剪贴板监听窗口已创建: {_hwnd}");
        });
    }

    public void Detach()
    {
        HwndSource? source = _source;
        if (source == null)
        {
            return;
        }

        if (!source.Dispatcher.CheckAccess())
        {
            source.Dispatcher.Invoke(Destroy);
            return;
        }

        Destroy();
    }

    private void Destroy()
    {
        IntPtr hwnd = _hwnd;
        _hwnd = IntPtr.Zero;

        HwndSource? source = _source;
        _source = null;

        if (hwnd != IntPtr.Zero)
        {
            try
            {
                NativeMethods.RemoveClipboardFormatListener(hwnd);
            }
            catch (Exception ex)
            {
                DebugLog.LogException("移除剪贴板监听失败", ex);
            }
        }

        source?.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            ClipboardUpdated?.Invoke();
        }

        return IntPtr.Zero;
    }
}
