using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Presentation.Controls;

namespace PCLLF.Presentation.Accounts;

public static class MinecraftSkinHead {
    public static (ImageSource Back, ImageSource? Fore) Crop(LegacyBitmap image) {
        int scale = Conversions.ToInteger(image.Pic!.Width / 64d);
        if (image.Pic.Width < 32 || image.Pic.Height < 32) throw new Exception("图片大小不足，长为 " + image.Pic.Height + "，宽为 " + image.Pic.Width);
        ImageSource? fore = null;
        if (image.Pic.Width >= 64 && image.Pic.Height >= 32) {
            if ((image.Pic.GetPixel(1, 1).A == 0 || image.Pic.GetPixel(image.Pic.Width - 1, image.Pic.Height - 1).A == 0 || image.Pic.GetPixel(image.Pic.Width - 2, Conversions.ToInteger(image.Pic.Height / 2d - 2)).A == 0) ||
                (image.Pic.GetPixel(1, 1) != image.Pic.GetPixel(scale * 41, scale * 9) && image.Pic.GetPixel(image.Pic.Width - 1, image.Pic.Height - 1) != image.Pic.GetPixel(scale * 41, scale * 9) && image.Pic.GetPixel(image.Pic.Width - 2, Conversions.ToInteger(image.Pic.Height / 2d - 2)) != image.Pic.GetPixel(scale * 41, scale * 9))) fore = image.Clip(scale * 40, scale * 8, scale * 8, scale * 8).ToImageSource();
        }
        ImageSource back = image.Clip(scale * 8, scale * 8, scale * 8, scale * 8).ToImageSource();
        return (back, fore);
    }
}

