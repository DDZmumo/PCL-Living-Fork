# LivingPCL 蓝白水晶视觉

## 当前选用版本

按用户最终选择，当前程序图标已切换为第二版六边形角色头像，图标母版与 256 像素 PNG、ICO 已同步替换。草方块背景继续使用。“打开启动器时显示 PCL 图标”默认关闭，已有用户设置不强制覆盖。

下文 Q 版角色抱草方块的图标提示词保留为设计迭代记录，不代表当前选用图标。

使用内置 `image_gen` 工具，根据用户提供的人设图与仓库原 PCL 六边形图标生成。用户人设原文件与原 PCL 图标没有被修改；生成后的图标只做了标准尺寸导出和 ICO 格式封装。

## 最终素材

- `DefaultBackground.png`：1672 × 941，蓝白水晶角色与草方块，左侧留白。
- `LivingPCL.png`：1254 × 1254，透明背景图标母版。
- `LivingPCL-256.png`：256 × 256，供启动画面与界面显示。
- `LivingPCL.ico`：含 16、24、32、48、64、128、256 像素 PNG 帧。

## 必须保留的人设特征

- 蓝色长发、呆毛、蓝眼睛、水晶发饰和蓝色三角发夹。
- 头上仅有一个水晶蝴蝶结，位于角色自身右侧、画面左侧。
- 角色自身右眼下方、画面左侧脸颊为蓝色实心小倒三角。
- 白色水手服与蓝色领口；图标采用 Q 版人物，双手抱着草方块。
- 图标外轮廓延续原 PCL 六边形，主体为人物与草方块，不放大字母遮挡。

## 最终生成与修订提示词

背景以已生成、已修正脸颊标记的蓝白角色背景为输入，最终编辑提示词如下：

> Edit this existing landscape launcher wallpaper to add tasteful, clearly recognizable Minecraft-style GRASS BLOCK elements while preserving the existing blue-haired character, her face, pose, solid-blue small cheek triangle, white sailor dress, crystal hair ornaments, the blue/white palette, and the quiet LEFT HALF for launcher interface text. Keep the canvas wide 16:9. Add ONE main voxel grass block floating near the character at the lower-right side, around her waist/hand region without covering her face or chest bow. It should have an unmistakable square green pixel-grass top and brown pixel-soil sides, with softly luminous icy-blue edge reflections so it belongs in this crystalline illustration. Add TWO smaller grass cubes along the very bottom edge / lower corners, airy and separated, subtle depth of field. Keep green as a restrained fresh accent against the blue-white wallpaper. Do not put a block in the leftmost 45% above the bottom quarter; this area must stay calm and nearly white. The character remains normal anime proportions in the wallpaper, NOT chibi; only the separately designed icon is chibi. Blend the voxel elements with the airy crystal atmosphere using a few small glints and very soft shadows, no block terrain, no dense landscape, no extra characters. Preserve the TINY SOLID BLUE INVERTED TRIANGLE beneath her own right eye (viewer-left cheek), fully filled blue, no hollow mark. No text, no logo, no watermark, no UI. Produce the final wallpaper, premium coherent anime illustration with Minecraft grass-block accents.

图标基础生成提示词：

> Create ONE finished Windows launcher application icon, square, genuinely transparent outside the badge, using the original PCL icon's upright blue HEXAGONAL OUTER CONTOUR in reference image 1 and the character identity from reference image 2. Main subject: a very cute CHIBI version of this blue-haired girl, 2-to-2.5-head proportions, HUGGING A LARGE MINECRAFT-STYLE GRASS BLOCK lovingly with BOTH ARMS. The grass block is a clearly readable voxel cube: green pixelated grass top, brown pixelated soil sides, softly crisp block edges. The girl's hands and arms naturally wrap around the cube, with her face visible above it. Show a compact complete chibi composition inside the hexagon, head dominant, little body, with long flowing azure/cyan hair, blue eyes, the curved ahoge, translucent crystalline hair bows and triangular blue hair clip, white sailor dress with dark-blue collar and pale-blue chest bow. IMPORTANT identity detail: under HER OWN RIGHT EYE, on the viewer's LEFT cheek, is one TINY SOLID BLUE DOWNWARD-POINTING TRIANGLE, fully filled, no hollow center. Gentle cheerful expression, wholesome non-suggestive, clean simplified anime mascot style. Keep a strong cobalt and cyan hexagonal border like the old PCL icon. Pale ice-blue or white interior backdrop. The chibi character and grass cube are the clear focus. Keep the character, hair, arms, cube and all decoration INSIDE the hexagonal silhouette, with a little transparent padding outside. No PCL lettering, no other text, no extra characters, no scene, no external ribbon loops, no tiny clutter, no watermark, no checkerboard, no square colored background. Aim for a polished adorable game launcher icon that stays clear at 32 and 48 pixels. Ideally 1024x1024 or higher.

图标最后一次编辑提示词，修正为头上只有一个蝴蝶结：

> Make a precise character-design correction to the FIRST image (the finished chibi girl hugging a grass-block application icon). The SECOND reference is the original character sheet, provided ONLY to confirm the hairstyle accessories. The girl has EXACTLY ONE hair bow on her head: keep the large translucent crystal bow on the VIEWER'S LEFT side of her head, matching the original reference. REMOVE the EXTRA crystal bow on the VIEWER'S RIGHT upper side of her head. Replace that removed right-side bow with normal continuous blue hair, following the surrounding hairstyle. Keep the blue TRIANGULAR HAIR CLIP on the viewer's right side: that clip must remain. Also remove any other extra bow attached to her hair on the viewer-right side behind her shoulder, so there is one and only one hair bow total. Clothing's small chest bow, if visible, is separate and may remain. Preserve everything else from the first icon exactly: chibi identity and cheerful expression, large blue eyes, the tiny SOLID BLUE DOWNWARD TRIANGLE under her own right eye/viewer-left cheek, both hands naturally hugging the large green-and-brown voxel grass cube, white sailor dress, flowing blue crystal hair, thick blue hexagonal outer silhouette, all icon geometry and transparent background. Do not add text. Output the corrected complete icon at the same square resolution with genuine transparency.

## 接入规则

背景内嵌于程序集，只有 `PCL/Pictures` 没有自定义文件时才使用。透明度、模糊和适应方式继续使用原设置；清空自定义图片后恢复默认背景。ICO 用于程序和窗口图标，256 像素 PNG 用于启动画面和关于页。
