using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SchematicKit;

/// <summary>
///     可嵌入任何页面的投影预览宿主：调用 <see cref="LoadAsync" /> 后在后台解析并显示预览，
///     返回按钮会关闭预览并抛出 <see cref="Closed" />。适合接在实例资源页的文件列表上。
/// </summary>
public sealed class SchematicPreviewHost : UserControl
{
    private readonly Grid _root = new();
    private readonly SchematicService _service;
    private readonly TextBlock _status;
    private int _requestId;
    private SchematicSession? _session;

    public SchematicPreviewHost(SchematicService service)
    {
        _service = service;
        Background = SchematicTheme.WindowBackground;
        _status = Ui.WrappedText("", 13d, SchematicTheme.TextSecondary);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextAlignment = TextAlignment.Center;
        _status.Margin = new Thickness(40);
        _root.Children.Add(_status);
        Content = _root;
    }

    /// <summary>预览已关闭。</summary>
    public event EventHandler? Closed;

    /// <summary>当前显示的会话；没有预览时为 null。</summary>
    public SchematicSession? Session => _session;

    /// <summary>是否正在显示预览。</summary>
    public bool HasPreview => _session is not null;

    /// <summary>
    ///     加载并显示一个投影文件。返回是否成功显示；失败时控件里会显示原因。
    ///     <paramref name="onBack" /> 在用户点返回时先执行，再抛 <see cref="Closed" />。
    /// </summary>
    public async Task<bool> LoadAsync(string path, string? jarPath = null, string? title = null, Action? onBack = null)
    {
        var request = ++_requestId;
        var strings = SchematicStrings.Current;
        ShowStatus(strings.Loading);
        try
        {
            var session = await _service.LoadAsync(path, jarPath).ConfigureAwait(true);
            if (request != _requestId)
            {
                // 用户已经切到别的文件，这次结果作废
                session.Dispose();
                return false;
            }

            DisposeSession();
            if (session.Data.IsEmpty)
            {
                session.Dispose();
                ShowStatus(session.Data.Error ??
                           string.Format(CultureInfo.CurrentCulture, strings.LoadFailed, Path.GetFileName(path)));
                return false;
            }

            _session = session;
            var view = new SchematicPreviewView(title ?? Path.GetFileName(path), session.Info, session.Data,
                session.Textures, null, () =>
                {
                    onBack?.Invoke();
                    Close();
                }, jarPath ?? _service.ResolveJarPath(path));
            _root.Children.Clear();
            _root.Children.Add(view);
            return true;
        }
        catch (Exception ex)
        {
            SchematicOptions.Log(ex, $"加载投影预览失败（{path}）");
            if (request == _requestId)
                ShowStatus(string.Format(CultureInfo.CurrentCulture, strings.LoadFailed, Path.GetFileName(path)));
            return false;
        }
    }

    /// <summary>关闭预览并释放贴图集。</summary>
    public void Close()
    {
        _requestId++;
        DisposeSession();
        _root.Children.Clear();
        _root.Children.Add(_status);
        _status.Text = "";
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowStatus(string text)
    {
        DisposeSession();
        _root.Children.Clear();
        _status.Text = text;
        _root.Children.Add(_status);
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
    }
}
