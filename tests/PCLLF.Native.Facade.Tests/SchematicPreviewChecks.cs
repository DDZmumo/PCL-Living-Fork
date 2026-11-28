// SPDX-License-Identifier: MPL-2.0
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.IO.Compression;
using fNbt;
using PCL;
using PCLLF.Wpf.Glass;
using SchematicKit;
using Controller = PCLCS.ResourcePageController<PCL.ModMinecraft.McInstance, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;
using ResourceView = PCLCS.ResourcePageView<PCL.ModMinecraft.McInstance, PCL.InstanceResourceEntry, PCL.InstanceResourceOnlineInfo>;

namespace PCLLF.Native.Facade.Tests;

internal static class SchematicPreviewChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        string root = Directory.CreateDirectory(Path.Combine(environment.Data, "schematic-preview")).FullName;
        string litematic = Save(root, "model.litematic", Litematic());
        string jarPath = WriteClientJar(root);
        using var lite = SchematicService.LoadAsync(litematic, jarPath).GetAwaiter().GetResult();
        Native.Check(lite.Metadata.Name == "Facade test" && lite.Metadata.TotalBlocks == 2 &&
            lite.Data.Error is null && lite.Data.Blocks.Count == 2 && lite.Data.Blocks.Min(block => block.X) == -1 &&
            lite.Data.Blocks.Min(block => block.Y) == 0,
            "A Litematica negative region or mixed metadata numeric types were lost.");
        SchematicBlockCount stone = lite.BlockCounts.Single();
        Native.Check(stone.Name == "Fixture stone" && stone.Count == 2 && stone.Icon.IsFrozen &&
            lite.Textures!.GetLegacyFaces("stone").All(face => face == "block/stone"),
            "The schematic preview failed to use the selected client JAR's texture and localization.");
        Color top = ColorOf(stone.Icon, 24, 12);
        Color left = ColorOf(stone.Icon, 12, 24);
        Color right = ColorOf(stone.Icon, 36, 24);
        Native.Check(ColorOf(stone.Icon, 0, 0).A == 0 && top.R == 255 && left.A == 255 && right.A == 255 &&
            top.R > left.R && left.R > right.R, "The statistics thumbnail lost its transparent corners or three shaded faces.");

        string schem = Save(root, "model.schem", Sponge());
        using var sponge = SchematicService.LoadAsync(schem, null).GetAwaiter().GetResult();
        Native.Check(sponge.Data.Error is null && sponge.Metadata.Version == 3 && sponge.Data.Blocks.Count == 1 &&
            sponge.Data.Blocks[0].Name == "minecraft:stone" && sponge.Data.Blocks[0].X == 1,
            "Sponge v3 palette ids and VarInt data did not produce the expected block.");

        string structure = Save(root, "model.nbt", Structure());
        using var vanilla = SchematicService.LoadAsync(structure, null).GetAwaiter().GetResult();
        Native.Check(vanilla.Data.Error is null && vanilla.Data.Blocks.Count == 1 && vanilla.Textures is not null,
            "A vanilla structure did not load its block and fallback atlas.");
        Native.Check(Directory.EnumerateFiles(root).Count() == 4, "Preview created a sidecar beside the original files.");

        CheckViewer(lite);
        CheckNativeEntry(environment, structure, lite);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try {
            using var unused = SchematicService.LoadAsync(structure, null, canceled.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("A canceled preview admitted new work.");
        } catch (OperationCanceledException) { }
        environment.AssertNoForbiddenIo();
        Console.WriteLine("PASS schematic preview: formats, model assets, camera controls, rounded frost, and two-column block counts.");
    }

    private static string WriteClientJar(string root)
    {
        string path = Path.Combine(root, "fixture.jar");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteTexture(archive, "assets/minecraft/textures/block/stone.png", Colors.White);
        WriteJson(archive, "assets/minecraft/lang/en_us.json", """{"block.minecraft.stone":"Fixture stone"}""");
        return path;
    }

    private static void WriteTexture(ZipArchive archive, string path, Color color)
    {
        byte[] pixels = Enumerable.Range(0, 16 * 16).SelectMany(_ => new[] { color.B, color.G, color.R, color.A }).ToArray();
        var texture = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
        var image = new PngBitmapEncoder();
        image.Frames.Add(BitmapFrame.Create(texture));
        using var png = new MemoryStream();
        image.Save(png);
        png.Position = 0;
        using var stream = archive.CreateEntry(path).Open();
        png.CopyTo(stream);
    }

    private static void WriteJson(ZipArchive archive, string path, string json)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(json);
    }

    private static void CheckNativeEntry(FixtureEnvironment environment, string structure, SchematicSession session)
    {
        using var scope = new FileTaskScope(environment);
        string game = Path.Combine(scope.Root, "game");
        scope.SelectMinecraft(game);
        string version = Directory.CreateDirectory(Path.Combine(game, "versions", "fixture")).FullName;
        File.WriteAllText(Path.Combine(version, "fixture.json"), """{"id":"fixture","type":"release","libraries":[]}""");
        var instance = new ModMinecraft.McInstance(version);
        FileTaskScope.CacheInstance(instance);
        string folder = Path.Combine(instance.PathIndie, "schematics");
        Native.Check(Native.Full(folder).StartsWith(Native.Full(game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The schematic fixture escaped its game directory.");
        Directory.CreateDirectory(folder);
        File.Copy(structure, Path.Combine(folder, "preview.nbt"));
        var main = FileTaskShell.Install();
        try {
            using var page = new GlassLocalResourcesPage(main, InstanceResourceKind.Schematics);
            page.Open(loadData: false);
            var controller = (Controller)Native.Get(page, "_controller")!;
            controller.Owner = instance;
            var view = (ResourceView)Native.Get(controller, "View")!;
            var entries = InstanceResourceStore.Scan(folder, folder, InstanceResourceKind.Schematics, default);
            controller.ShowResources(folder, folder, entries);
            var row = view.PanList.Children.OfType<GlassLocalResourceRow>().Single();
            Native.Call(typeof(GlassLocalResourceRow), "ShowActions", row);
            var tools = (StackPanel)Native.Get(row, "_tools")!;
            var button = tools.Children.OfType<Button>().Single(item => Equals(item.ToolTip, "3D 预览"));
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Native.Check(page.Content is GlassSchematicPreview && page.ActiveScroll is null, "The instance row did not mount a native preview in the content area.");
            CheckBlockList((GlassSchematicPreview)page.Content, session);
            Native.Check(page.CloseSchematicPreview() && page.Content is Grid && ReferenceEquals(page.ActiveScroll, page.Scroll) &&
                view.PanList.Children.Contains(row), "Closing the preview discarded the originating resource list.");
            CheckLayerBackdrop(instance, entries.Single());
            CheckModAssets(instance, folder);
            main.PageChange(FormMain.PageType.Other, FormMain.PageSubType.OtherSchematics);
            Native.Check(main.PageCurrent.Page == FormMain.PageType.Other && main.PageCurrentSub == FormMain.PageSubType.OtherSchematics,
                "The More sidebar route did not admit the direct schematic manager.");
        } finally { FileTaskShell.CloseUnshownHosts(); }
    }

    private static void CheckModAssets(ModMinecraft.McInstance instance, string folder)
    {
        string mods = Directory.CreateDirectory(Path.Combine(instance.PathIndie, "mods")).FullName;
        using (var shapes = ZipFile.Open(Path.Combine(mods, "shapes.jar"), ZipArchiveMode.Create)) {
            WriteJson(shapes, "assets/shapes/models/block/frame.json", """
                {"textures":{"surface":"shapes:block/frame"},"elements":[{"from":[0,0,0],"to":[8,16,16],
                    "faces":{"north":{"texture":"#surface"},"south":{"texture":"#surface"}}}]}
                """);
            WriteTexture(shapes, "assets/shapes/textures/block/frame.png", Colors.Blue);
        }
        using (var machines = ZipFile.Open(Path.Combine(mods, "machines.jar"), ZipArchiveMode.Create)) {
            WriteJson(machines, "assets/machines/blockstates/frame.json", """{"variants":{"axis=y":{"model":"machines:block/frame"}}}""");
            WriteJson(machines, "assets/machines/models/block/frame.json", """
                {"parent":"shapes:block/frame","textures":{"surface":"machines:block/frame"}}
                """);
            WriteJson(machines, "assets/machines/models/block/runtime.json", """
                {"loader":"machines:dynamic","textures":{"particle":"shapes:block/frame"}}
                """);
            WriteJson(machines, "assets/machines/lang/en_us.json", """{"block.machines.frame":"Equipment frame"}""");
            WriteTexture(machines, "assets/machines/textures/block/frame.png", Colors.Red);
            WriteTexture(machines, "assets/machines/textures/block/plate.png", Colors.Green);
        }
        using (var disabled = ZipFile.Open(Path.Combine(mods, "example.jar.disabled"), ZipArchiveMode.Create))
            WriteTexture(disabled, "assets/machines/textures/block/hidden.png", Colors.Magenta);

        string[] blockNames = ["machines:frame", "shapes:frame", "machines:runtime", "machines:plate", "machines:hidden"];
        Save(folder, "mod-blocks.nbt", new NbtCompound("") {
            new NbtList("size", [new NbtInt(blockNames.Length), new NbtInt(1), new NbtInt(1)]),
            new NbtList("palette", blockNames.Select(name => new NbtCompound {
                new NbtString("Name", name), new NbtCompound("Properties") { new NbtString("axis", "y") }
            })),
            new NbtList("blocks", blockNames.Select((_, index) => new NbtCompound {
                new NbtInt("state", index), new NbtList("pos", [new NbtInt(index), new NbtInt(0), new NbtInt(0)])
            }))
        });
        var entry = InstanceResourceStore.Scan(folder, folder, InstanceResourceKind.Schematics, default).Single(file => file.Name == "mod-blocks.nbt");
        using var preview = LauncherSchematicApplication.PreviewAsync(instance, entry, default).GetAwaiter().GetResult();
        var atlas = preview.Textures!;
        var frame = atlas.Models.Get("machines:frame", "axis=y");
        Native.Check(frame is { IsFullCube: false } && frame.Quads.Count == 2 &&
            frame.Quads.All(face => face.Texture == "machines:block/frame"), "The instance did not load a mod blockstate and a cross-JAR model parent.");
        Native.Check(ColorOf(atlas.GetBlockIcon("machines:frame")) == Colors.Red &&
            ColorOf(atlas.GetBlockIcon("shapes:frame")) == Colors.Blue &&
            preview.BlockCounts.Any(block => block.Name == "Equipment frame"), "Mod texture names or localized names were resolved in the wrong namespace.");
        Native.Check(atlas.Models.Get("machines:runtime", "axis=y") is null &&
            ColorOf(atlas.GetBlockIcon("machines:runtime")) == Colors.Blue &&
            ColorOf(atlas.GetBlockIcon("machines:plate")) == Colors.Green &&
            ColorOf(atlas.GetBlockIcon("machines:hidden")) != Colors.Magenta, "Runtime or texture-only fallbacks failed, or a disabled mod was indexed.");
        var shaped = preview.BlockCounts.Single(block => block.BlockId == "machines:frame").Icon;
        int colored = 0;
        for (int y = 0; y < shaped.PixelHeight; y++)
        for (int x = 0; x < shaped.PixelWidth; x++)
            if (ColorOf(shaped, x, y).A != 0) colored++;
        Native.Check(colored is > 100 and < 1000, "A narrow model face was painted as a full cube in the statistics.");
    }

    private static Color ColorOf(BitmapSource image, int x = 16, int y = 16)
    {
        byte[] pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void CheckBlockList(GlassSchematicPreview preview, SchematicSession session)
    {
        SchematicBlockCount[] blocks = [
            session.BlockCounts[0],
            new("minecraft:stone", "Second fixture block", 1, session.Textures!),
            new("minecraft:stone", "Third fixture block", 1, session.Textures!)
        ];
        using var statistics = new SchematicSession(session.Data, session.Metadata, null, session.Info, blocks);
        Native.Call(typeof(GlassSchematicPreview), "AddBlockCounts", preview, statistics);
        ArrangePreview(840);
        var body = (Grid)Native.Get(preview, "_body")!;
        var side = (GlassSurface)body.Children[1];
        var commands = (StackPanel)Native.Get(preview, "_commands")!;
        var toggle = commands.Children.OfType<Button>().Single(button => Equals(button.Content, "方块统计"));
        Native.Check(side.Visibility == Visibility.Visible && toggle.Visibility == Visibility.Collapsed,
            "The statistics panel did not use the side column at wide sizes.");
        var dock = (DockPanel)side.Child;
        var list = dock.Children.OfType<ListBox>().Single();
        var size = new Size(185, 130);
        list.Measure(size);
        list.Arrange(new Rect(size));
        list.UpdateLayout();
        var image = FindImage(list);
        Native.Check(ReferenceEquals(image?.Source, session.BlockCounts[0].Icon), "The statistics row did not render its atlas icon.");

        foreach (bool enabled in new[] { true, false }) {
            preview.IsEnabled = enabled; // The details modal disables the preview behind it.
            list.UpdateLayout();
            var snapshot = new RenderTargetBitmap(185, 130, 96, 96, PixelFormats.Pbgra32);
            snapshot.Render(list);
            var pixel = new byte[4];
            snapshot.CopyPixels(new Int32Rect(90, 110, 1, 1), pixel, 4, 0);
            Native.Check(list.IsEnabled == enabled && pixel[3] == 0, "The statistics list painted a background over its glass surface.");
        }
        preview.IsEnabled = true;
        ArrangePreview(560);
        Native.Check(side.Visibility == Visibility.Collapsed && body.ColumnDefinitions[1].Width.Value == 0 &&
            toggle.Visibility == Visibility.Visible, "Narrow preview still reserved space for its side statistics.");
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var overlays = (Grid)Native.Get(preview, "_overlays")!;
        var flyout = overlays.Children.OfType<GlassSurface>().Single();
        var compact = ((DockPanel)flyout.Child).Children.OfType<ListBox>().Single();
        var pair = compact.Items[0];
        var last = compact.Items[1];
        Native.Check(flyout.Visibility == Visibility.Visible && compact.Items.Count == 2 &&
            compact.ItemTemplate.LoadContent() is Grid { ColumnDefinitions.Count: 2 } &&
            ReferenceEquals(Native.Property(pair.GetType(), "Second").GetValue(pair), blocks[1]) &&
            Native.Property(last.GetType(), "Second").GetValue(last) is null,
            "The compact statistics did not show two columns with the final unpaired block.");

        var stage = (Grid)Native.Get(preview, "_stage")!;
        Native.Check(stage.Clip is RectangleGeometry corners && corners.RadiusX > 0 &&
            !corners.FillContains(new Point(1, 1)) && corners.FillContains(new Point(stage.ActualWidth / 2, stage.ActualHeight / 2)),
            "The preview did not clip the scene to its rounded corners.");
        ArrangePreview(840);
        Native.Check(side.Visibility == Visibility.Visible && flyout.Visibility == Visibility.Collapsed,
            "Resizing a compact preview left the statistics overlay open.");

        void ArrangePreview(double width)
        {
            var viewport = new Size(width, 620);
            preview.Measure(viewport);
            preview.Arrange(new Rect(viewport));
            preview.UpdateLayout();
        }
    }

    private static Image? FindImage(DependencyObject item)
    {
        if (item is Image image) return image;
        for (int child = 0; child < VisualTreeHelper.GetChildrenCount(item); child++)
            if (FindImage(VisualTreeHelper.GetChild(item, child)) is { } found) return found;
        return null;
    }

    private static void CheckViewer(SchematicSession session)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try {
            var viewer = new SchematicViewer3D(session.Textures!, animate: false);
            Task ready = viewer.InitializeAsync(session.Data);
            VisualAssetChecks.Pump(() => ready.IsCompleted, "build the schematic mesh");
            ready.GetAwaiter().GetResult();
            var viewport = (Viewport3D)viewer.Content;
            var scene = (Model3DGroup)((ModelVisual3D)viewport.Children[0]).Content;
            var rotatingModel = (Model3DGroup)scene.Children[^1];
            CheckOrbit(viewer, viewport, rotatingModel);
            var model = (GeometryModel3D)rotatingModel.Children[0];
            var original = (MeshGeometry3D)model.Geometry;
            Native.Check(original.IsFrozen && viewer.MinLayer == 0 && viewer.MaxLayer == 1, "The native viewport lost its mesh or layer bounds.");
            viewer.VisibleTopLayer = 0;
            VisualAssetChecks.Pump(() => !ReferenceEquals(model.Geometry, original), "rebuild one visible schematic layer");
            Native.Check(((MeshGeometry3D)model.Geometry).Positions.Count < original.Positions.Count, "Layer slicing did not update the viewport.");
        } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void CheckLayerBackdrop(ModMinecraft.McInstance instance, InstanceResourceEntry entry)
    {
        using var preview = new GlassSchematicPreview(instance, entry, () => { });
        if (!GlassMaterial.AllowsTransparency(preview)) return;
        var window = new Window { Content = preview, Width = 850, Height = 640, Left = -30000, Top = -30000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var sceneOverlays = (List<(Grid Host, Border Tint, GlassSceneFrost Frost)>)Native.Get(preview, "_sceneOverlays")!;
            VisualAssetChecks.Pump(() => sceneOverlays.Count == 3 && sceneOverlays.All(overlay => overlay.Frost.IsVisible),
                "attach the live backdrop to the hint, commands and layer bar");
            var frost = sceneOverlays[^1].Frost;
            var scene = (Grid)Native.Get(preview, "_scene")!;
            Native.Check(sceneOverlays.All(overlay => ReferenceEquals(((VisualBrush)Native.Get(overlay.Frost, "_body")!).Visual, scene)),
                "The preview controls sampled a surface outside the model scene.");
            var sample = new Border { Background = Brushes.Red };
            scene.Children.Add(sample);
            Color red = ReadBackdrop();
            sample.Background = Brushes.Blue;
            Color blue = ReadBackdrop();
            Native.Check(red.R > red.B + 100 && blue.B > blue.R + 100, "The layer bar did not track changes in its backdrop.");
            scene.Children.Remove(sample);
            window.Width = 560;
            window.UpdateLayout();
            var commands = (StackPanel)Native.Get(preview, "_commands")!;
            var toggle = commands.Children.OfType<Button>().Single(command => Equals(command.Content, "方块统计"));
            Native.Check(toggle.IsVisible, "The statistics control did not appear in compact mode.");
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var motion = (PCLLF.Wpf.Motion.SpringMotion)Native.Get(preview, "_statisticsMotion")!;
            Native.Check(motion.Target == 1 && (!PCLLF.Wpf.Motion.MotionPreferences.AllowsAnimation || motion.IsRunning),
                "The statistics flyout skipped its entrance.");
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            VisualAssetChecks.Pump(() => motion.Target == 0 && !motion.IsRunning, "reverse and close the statistics flyout");

            Color ReadBackdrop()
            {
                window.UpdateLayout();
                VisualAssetChecks.Drain();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frost.ActualWidth), (int)Math.Ceiling(frost.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(frost);
                return ColorOf(bitmap, bitmap.PixelWidth / 2, bitmap.PixelHeight / 2);
            }
        } finally
        {
            window.Content = null;
            window.Close();
            VisualAssetChecks.Drain();
        }
    }

    private static void CheckOrbit(SchematicViewer3D viewer, Viewport3D viewport, Model3DGroup model)
    {
        var size = new Size(700, 370);
        viewer.Measure(size);
        viewer.Arrange(new Rect(size));
        var camera = (PerspectiveCamera)viewport.Camera;
        var yaw = (AxisAngleRotation3D)Native.Get(viewer, "_yaw")!;
        var pitch = (AxisAngleRotation3D)Native.Get(viewer, "_pitch")!;
        yaw.Angle = 66;
        Native.Call(typeof(SchematicViewer3D), "UpdateCamera", viewer);
        double initialDistance = camera.Position.Z;

        // Reproduce a vertical drag after the model has already been turned sideways.
        pitch.Angle = -62;
        Native.Call(typeof(SchematicViewer3D), "UpdateCamera", viewer);
        Vector3D up = model.Transform.Transform(new Vector3D(0, 1, 0));
        Native.Check(Math.Abs(up.X) < 0.000001, "Vertical dragging rolled the model after horizontal rotation.");
        Native.Check(Math.Abs(camera.Position.Z - initialDistance) < 0.000001, "Vertical dragging changed the zoom distance.");
        var pan = (TranslateTransform3D)Native.Get(viewer, "_panTransform")!;
        Point3D modelCenter = model.Transform.Inverse.Transform(new Point3D());
        Native.Call(typeof(SchematicViewer3D), "PanByPixels", viewer, new Vector(52, 24));
        Point3D moved = model.Transform.Transform(modelCenter);
        Native.Check(moved.X > 0 && moved.Y < 0 && Math.Abs(moved.Z) < 0.000001 && yaw.Angle == 66 && pitch.Angle == -62,
            "Pointer panning did not follow the screen axes independently of rotation.");
        Point3D target = model.Transform.Inverse.Transform(new Point3D());
        yaw.Angle += 42;
        pitch.Angle += 15;
        Native.Check((model.Transform.Transform(target) - new Point3D()).Length < .000001 &&
            (target - modelCenter).Length > .000001, "Orbiting after a pan did not use the newly centered point.");
        viewer.ResetView();
        Native.Check(pan.OffsetX == 0 && pan.OffsetY == 0 && pan.OffsetZ == 0 && yaw.Angle == -35 && pitch.Angle == 18,
            "Resetting the view did not restore the default camera.");
    }

    private static string Save(string directory, string name, NbtCompound data)
    {
        string path = Path.Combine(directory, name);
        new NbtFile(data).SaveToFile(path, NbtCompression.GZip);
        return path;
    }

    private static NbtCompound Litematic() => new("") {
        new NbtInt("Version", 6),
        new NbtCompound("Metadata") { new NbtString("Name", "Facade test"), new NbtLong("TotalBlocks", 2), new NbtInt("RegionCount", 1) },
        new NbtCompound("Regions") {
            new NbtCompound("Test") {
                new NbtCompound("Position") { new NbtInt("x", -1), new NbtInt("y", 1), new NbtInt("z", 0) },
                new NbtCompound("Size") { new NbtInt("x", 1), new NbtInt("y", -2), new NbtInt("z", 1) },
                new NbtList("BlockStatePalette", [Stone()]), new NbtLongArray("BlockStates", [0L])
            }
        }
    };

    private static NbtCompound Sponge() => new("") {
        new NbtCompound("Schematic") {
            new NbtInt("Version", 3), new NbtShort("Width", 2), new NbtShort("Height", 1), new NbtShort("Length", 1),
            new NbtCompound("Blocks") {
                new NbtCompound("Palette") { new NbtInt("minecraft:air", 0), new NbtInt("minecraft:stone", 130) },
                new NbtByteArray("Data", [0, 130, 1])
            }
        }
    };

    private static NbtCompound Structure() => new("") {
        new NbtList("size", [new NbtInt(1), new NbtInt(1), new NbtInt(1)]),
        new NbtList("palette", [Stone()]),
        new NbtList("blocks", [new NbtCompound { new NbtInt("state", 0),
            new NbtList("pos", [new NbtInt(0), new NbtInt(0), new NbtInt(0)]) }])
    };

    private static NbtCompound Stone() => new() { new NbtString("Name", "minecraft:stone") };
}
