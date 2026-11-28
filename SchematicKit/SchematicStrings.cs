namespace SchematicKit;

/// <summary>
///     界面文案。默认中文；宿主可以整体替换成自己的本地化系统，或逐项改写。
///     带 <c>{0}</c> 的成员需要调用方自行 <see cref="string.Format(string, object?)" />。
/// </summary>
public sealed class SchematicStrings
{
    #region 通用

    public string Preview { get; set; } = "预览";

    public string Back { get; set; } = "返回";

    public string Detail { get; set; } = "详情";

    public string Refresh { get; set; } = "刷新列表";

    public string OpenFolder { get; set; } = "打开文件夹";

    public string Import { get; set; } = "导入投影";

    public string ImportDialogTitle { get; set; } = "选择要导入的投影文件";

    public string ImportDialogFilter { get; set; } =
        "投影原理图(*.litematic;*.nbt;*.schem;*.schematic)|*.litematic;*.nbt;*.schem;*.schematic";

    public string ImportDone { get; set; } = "已导入 {0} 个文件";

    public string ImportFailed { get; set; } = "导入投影失败";

    public string OpenWebsite { get; set; } = "打开网页";

    #endregion

    #region 投影管理页

    public string Title { get; set; } = "投影管理";

    public string ListTitle { get; set; } = "投影列表";

    public string SearchHint { get; set; } = "搜索投影文件";

    public string InstancePick { get; set; } = "选择投影所在实例";

    public string NoInstance { get; set; } = "未找到任何实例";

    public string Count { get; set; } = "共 {0} 个投影文件";

    public string VersionInfo { get; set; } = "{0} 个投影文件";

    public string Empty { get; set; } =
        "当前实例的 schematics 文件夹中还没有投影文件。把 .litematic / .schem 文件放入该文件夹后点击刷新。";

    public string Loading { get; set; } = "正在读取投影…";

    public string LoadFailed { get; set; } = "读取投影「{0}」失败";

    public string SortBy { get; set; } = "排序：{0}";

    public string SortModifyTime { get; set; } = "修改时间";

    public string SortFileName { get; set; } = "文件名";

    #endregion

    #region 3D 预览

    public string BlockList { get; set; } = "所需方块 · {0} 种 / {1} 个";

    public string BuildFailed { get; set; } = "3D 预览构建失败，详情见日志";

    public string PreviewHint { get; set; } = "拖动旋转 · 滚轮缩放 · WASD 移动 · R 复位";

    public string LayerAll { get; set; } = "全部层";

    public string LayerInfo { get; set; } = "已显示 {0} / {1} 层（Y ≤ {2}）";

    public string LayerOnly { get; set; } = "分层显示";

    public string LayerTotal { get; set; } = "共 {0} 层";

    public string TooLarge { get; set; } = "该原理图包含 {0} 个方块，超出 3D 预览上限（{1}），仅显示详情与方块列表";

    #endregion

    #region 文件详情

    public string FileInfo { get; set; } = "文件：{0}（{1}）";

    public string DetailInfo { get; set; } = "详细信息：";

    public string OriginalName { get; set; } = "原始名称：";

    public string Author { get; set; } = "作者：{0}";

    public string Description { get; set; } = "说明：";

    public string Version { get; set; } = "原理图版本：";

    public string EnclosingSize { get; set; } = "包围盒大小：";

    public string EnclosingDimensions { get; set; } = "包围盒尺寸：";

    public string TotalBlocks { get; set; } = "总方块数：";

    public string TotalVolume { get; set; } = "总体积：";

    public string RegionCount { get; set; } = "区域数量：";

    public string CreatedTime { get; set; } = "创建时间：";

    public string ModifiedTime { get; set; } = "修改时间：";

    public string DataVersion { get; set; } = "数据版本：";

    public string SpongeVersion { get; set; } = "Sponge 版本：";

    public string GameVersion { get; set; } = "游戏版本：";

    public string FileType { get; set; } = "文件类型：";

    public string FileTypeLitematic { get; set; } = "Litematica";

    public string FileTypeSponge { get; set; } = "Sponge Schematic";

    public string FileTypeNbt { get; set; } = "原版结构";

    public string FileTypeMcedit { get; set; } = "MCEdit/WorldEdit Schematic";

    public string FileTypeUnknown { get; set; } = "未知";

    public string UnsupportedPreview { get; set; } = "该格式暂不支持 3D 预览";

    #endregion

    /// <summary>当前的文案集合，默认中文。</summary>
    public static SchematicStrings Current { get; set; } = Chinese();

    public static SchematicStrings Chinese() => new();

    public static SchematicStrings English() => new()
    {
        Preview = "Preview",
        Back = "Back",
        Detail = "Details",
        Refresh = "Refresh",
        OpenFolder = "Open folder",
        Import = "Import",
        ImportDialogTitle = "Select schematic files",
        ImportDialogFilter = "Schematics (*.litematic;*.nbt;*.schem;*.schematic)|*.litematic;*.nbt;*.schem;*.schematic",
        ImportDone = "Imported {0} file(s)",
        ImportFailed = "Failed to import schematics",
        OpenWebsite = "Open website",
        Title = "Schematics",
        ListTitle = "Schematic list",
        SearchHint = "Search schematic files",
        InstancePick = "Select the instance that owns these schematics",
        NoInstance = "No instance found",
        Count = "{0} schematic file(s)",
        VersionInfo = "{0} schematic file(s)",
        Empty = "The schematics folder of this instance is empty. Put .litematic / .schem files into it and refresh.",
        Loading = "Reading schematic…",
        LoadFailed = "Failed to read \"{0}\"",
        SortBy = "Sort: {0}",
        SortModifyTime = "modified time",
        SortFileName = "file name",
        BlockList = "Blocks required · {0} kinds / {1} total",
        BuildFailed = "Failed to build the 3D preview, see the log for details",
        PreviewHint = "Drag to rotate · Wheel to zoom · WASD to move · R to reset",
        LayerAll = "All layers",
        LayerInfo = "Showing {0} / {1} layers (Y ≤ {2})",
        LayerOnly = "Slice by layer",
        LayerTotal = "{0} layer(s)",
        TooLarge = "This schematic has {0} blocks, above the 3D preview limit ({1}); only details and the block list are shown",
        FileInfo = "File: {0} ({1})",
        DetailInfo = "Details:",
        OriginalName = "Original name: ",
        Author = "Author: {0}",
        Description = "Description: ",
        Version = "Schematic version: ",
        EnclosingSize = "Enclosing size: ",
        EnclosingDimensions = "Enclosing dimensions: ",
        TotalBlocks = "Total blocks: ",
        TotalVolume = "Total volume: ",
        RegionCount = "Regions: ",
        CreatedTime = "Created: ",
        ModifiedTime = "Modified: ",
        DataVersion = "Data version: ",
        SpongeVersion = "Sponge version: ",
        GameVersion = "Game version: ",
        FileType = "File type: ",
        FileTypeLitematic = "Litematica",
        FileTypeSponge = "Sponge Schematic",
        FileTypeNbt = "Vanilla structure",
        FileTypeMcedit = "MCEdit/WorldEdit Schematic",
        FileTypeUnknown = "Unknown",
        UnsupportedPreview = "This format cannot be previewed in 3D yet"
    };
}
