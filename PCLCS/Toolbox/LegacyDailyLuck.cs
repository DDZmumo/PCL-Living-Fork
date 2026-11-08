// PCL behavior reconstruction from the user-supplied PCL 2.99 artifact;
// upstream PCL repository LICENCE applies. This file is not MPL-original code.
// SHA256 5b64d6d540c376390470cea3ab99d47661e8bca81be2ba647ea83e7d2e9428fa,
// MVID 0b152d43-0403-4f95-b151-c673fa4af019, PageOtherTest.Jrrp 0x06000aea.
// REA ev_0a4c8189706d69793a42c026cea90f7c63b0d33494ed22690c66062d7bac1739.
using System;
using MeloongCore.Extensions;

namespace PCLCS;

public static class LegacyDailyLuck {
    public static int Score(DateTime date, string identify) {
        ulong day = string.Format("asdfgbn{0}12#3$45{1}IUY", date.DayOfYear, date.Year).GetStableHashCode();
        ulong machine = string.Format("QWERTY{0}0*8&6{1}kjhg", identify, date.Day).GetStableHashCode();
        int raw = checked((int)Math.Round(Math.Abs((day / 3d + machine / 3d) / 527d) % 1001d));
        return raw >= 970 ? 100 : checked((int)Math.Round(raw / 969d * 99d));
    }

    public static string Description(int score) => score switch {
        100 => "！100！100！！！！！",
        >= 98 => "！差点就到 100 了呢……",
        >= 90 => "！好评如潮！",
        >= 65 => "！今天运气不错呢！",
        > 50 => "！还行啦，还行啦。",
        50 => "！五五开……",
        >= 40 => "！勉强还行吧……？",
        >= 20 => "！呜……",
        >= 11 => "？！不会吧……",
        >= 1 => "……（是百分制哦）",
        _ => "？！"
    };
}
