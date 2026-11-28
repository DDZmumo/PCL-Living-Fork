// Behavior source: LF Pages/PageSetup and Pages/PageOther; repository LICENCE.
// LF lineage f4aa0fae62c3ee8af24c81e61844c8fdd13c8983; upstream PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8.
// Existing LF policies are preserved; no new native/performance/MMC behavior.
using System;
using System.IO;
using System.Windows.Input;
using MeloongCore;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;
public sealed class LegacyToolboxPageController<TFile, TDownload, TSave, TCombo>
    where TCombo : class
{
    private readonly LegacyToolboxPageRuntime<TFile, TDownload, TSave, TCombo> runtime;
    public readonly LegacyToolboxPageView View;
    private LegacyToolboxPageHost<TFile, TDownload, TSave, TCombo> Host => runtime.Host;

    public LegacyToolboxPageController(LegacyToolboxPageRuntime<TFile, TDownload, TSave, TCombo> runtime, LegacyToolboxPageView view)
    {
        this.runtime = runtime;
        View = view;
    }

    public void InitializeValidation()
    {
        Host.AddValidation(View.Url(), value =>
        {
            try
            {
                LegacyToolboxDownloadSession.ParseUrl(value);
                return "";
            }
            catch (ArgumentException error)
            {
                return error.Message;
            }
        });
        Host.AddFolderValidation(View.Folder());
        Host.AddValidation(View.Name(), LegacyToolboxDownloadSession.ValidateName);
    }

    public void Loaded()
    {
        if (View.Loaded())
            return;
        string folder = Conversions.ToString(Host.Common.Read("CacheDownloadFolder"))!;
        if (string.IsNullOrWhiteSpace(folder) || Host.ValidateFolder(folder) != "")
            folder = Host.Common.BaseFolder() + "PCL\\MyDownload\\";
        View.Folder().Text = folder;
        View.Folder().ValidateInput();
        View.Url().ValidateInput();
        View.Name().ValidateInput();
        View.SetLoaded(true);
        StartButtonRefresh();
    }

    public void StartButtonRefresh()
    {
        if (View.Start()is null)
            return;
        View.Start().IsEnabled = View.Loaded() && View.Url().IsValidated && View.Folder().IsValidated && View.Name().IsValidated;
        View.Open().IsEnabled = View.Loaded() && View.Folder().IsValidated;
    }

    public void UrlChanged()
    {
        if (!View.Loaded() || !string.IsNullOrEmpty(View.Name().Text))
            return;
        View.Name().Text = LegacyToolboxDownloadSession.SuggestName(View.Url().Text);
    }

    public void SaveFolder()
    {
        if (View.Loaded() && View.Folder().IsValidated)
            Host.Common.Write("CacheDownloadFolder", View.Folder().Text);
    }

    public void UrlKeyDown(KeyEventArgs args)
    {
        if (args.Key == Key.Return && View.Start().IsEnabled)
            StartDownload();
    }

    public void StartDownload()
    {
        if (!runtime.TryStartCustomDownload(View.Url().Text, View.Name().Text, View.Folder().Text))
            return;
        View.Url().Text = "";
        View.Name().Text = "";
        View.Url().ValidateInput();
        View.Name().ValidateInput();
        View.Url().ClearValidationFeedback();
        View.Name().ClearValidationFeedback();
        StartButtonRefresh();
    }

    public void SelectFolder()
    {
        string? folder = Host.SelectFolder();
        if (folder is not null)
            View.Folder().Text = folder;
    }

    public void OpenFolder()
    {
        try
        {
            string folder = View.Folder().Text;
            if (Host.ValidateFolder(folder) != "")
                return;
            Directory.CreateDirectory(folder);
            Host.StartProcess(folder);
        }
        catch (Exception error)
        {
            Logger.Error(error, "打开下载文件夹失败", LogBehavior.Toast);
        }
    }

    public void Clear() => runtime.RubbishClear();
    public void Memory() => Host.Common.InThread(() => runtime.MemoryOptimize(true));
}
