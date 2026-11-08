// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections.Generic;
using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PCLCS;

public sealed record PersonalOptionDefinition(string Key, string Title, string Category, string Setting);

public readonly record struct PersonalOptionEdit(string Key, string Value, string? Expected);

public static class PersonalOptionsCatalog {
    public static readonly IReadOnlyList<PersonalOptionDefinition> Items = Create();
    private static readonly HashSet<string> Keys = new(Items.Select(item => item.Key), System.StringComparer.Ordinal);
    public static bool Contains(string key) => Keys.Contains(PersonalOptionsRules.CanonicalKey(key));
    public static bool IsToggle(string key) => key is "invertYMouse" or "bobView";
    public static int SliderMaximum(string key) => key == "fov" ? 80 : key == "mouseSensitivity" ? 200 : 100;
    public static string ValidateValue(string key, string value) {
        if (!Contains(key) || value.Length > 4096 || value.IndexOfAny(['\0', '\n', '\r']) >= 0)
            throw new IOException("无法保存该个人设置。");
        if (key.StartsWith("key_", StringComparison.Ordinal))
            return PersonalOptionsRules.NormalizeBinding(value) ?? throw new IOException("无法识别这个按键。");
        if (IsToggle(key)) return bool.TryParse(value, out bool on) ? on ? "true" : "false" : throw new IOException("开关设置无效。");
        string? normalized = PersonalOptionsRules.CanonicalValue(key, value);
        if (normalized is null || !double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || double.IsNaN(number) || double.IsInfinity(number) ||
            number < (key == "fov" ? 30 : 0) || number > (key == "fov" ? 110 : 1)) throw new IOException("数值超出此设置允许的范围。");
        return number.ToString("0.########", CultureInfo.InvariantCulture);
    }
    public static int SliderValue(string key, string value) {
        double number = double.Parse(ValidateValue(key, value), CultureInfo.InvariantCulture);
        return Convert.ToInt32(Math.Round(key == "fov" ? number - 30 : number * SliderMaximum(key)));
    }
    public static string FromSlider(string key, int value) {
        if (!Contains(key) || IsToggle(key) || key.StartsWith("key_", StringComparison.Ordinal) || value < 0 || value > SliderMaximum(key)) throw new IOException("滑动条数值无效。");
        return ValidateValue(key, (key == "fov" ? value + 30 : (double)value / SliderMaximum(key)).ToString("0.########", CultureInfo.InvariantCulture));
    }
    public static string SliderText(string key, int value) => key == "fov" ? (value + 30) + "°" : value + "%";
    private static IReadOnlyList<PersonalOptionDefinition> Create() {
        var items = new List<PersonalOptionDefinition>();
        void Add(string category, string setting, string text) {
            foreach (string entry in text.Split('|')) {
                string[] pair = entry.Split('=');
                items.Add(new(pair[0], pair[1], category, setting));
            }
        }
        Add("原版按键", "LaunchPersonalOptionsKeys", "key_key.attack=攻击 / 破坏|key_key.use=使用 / 放置|key_key.pickItem=选取方块|key_key.forward=前进|key_key.back=后退|key_key.left=向左|key_key.right=向右|key_key.jump=跳跃|key_key.sneak=潜行|key_key.sprint=疾跑|key_key.drop=丢弃物品|key_key.inventory=物品栏|key_key.chat=聊天|key_key.playerlist=玩家列表|key_key.command=命令|key_key.screenshot=截图|key_key.togglePerspective=切换视角|key_key.smoothCamera=平滑视角|key_key.fullscreen=全屏|key_key.spectatorOutlines=旁观者轮廓|key_key.advancements=进度|key_key.swapOffhand=交换副手|key_key.saveToolbarActivator=保存快捷栏|key_key.loadToolbarActivator=加载快捷栏");
        for (int slot = 1; slot <= 9; slot++) items.Add(new("key_key.hotbar." + slot, "快捷栏 " + slot, "原版按键", "LaunchPersonalOptionsKeys"));
        Add("鼠标", "LaunchPersonalOptionsMouse", "mouseSensitivity=鼠标灵敏度|invertYMouse=反转鼠标|mouseWheelSensitivity=滚轮灵敏度|discrete_mouse_scroll=离散滚动|rawMouseInput=原始输入|touchscreen=触屏模式|toggleSprint=切换疾跑|toggleCrouch=切换潜行");
        Add("视野与视角", "LaunchPersonalOptionsView", "fov=视野|fovEffectScale=视野变化幅度|screenEffectScale=屏幕扭曲效果|bobView=行走视角摇晃");
        Add("辅助功能", "LaunchPersonalOptionsAccessibility", "showSubtitles=字幕|autoJump=自动跳跃|highContrast=高对比度|darknessEffectScale=黑暗效果|hideLightningFlashes=隐藏闪电闪光|notificationDisplayTime=通知显示时长");
        Add("音量", "LaunchPersonalOptionsAudio", "soundCategory_master=主音量|soundCategory_music=音乐|soundCategory_record=唱片|soundCategory_weather=天气|soundCategory_block=方块|soundCategory_hostile=敌对生物|soundCategory_neutral=友好生物|soundCategory_player=玩家|soundCategory_ambient=环境|soundCategory_voice=语音");
        // Menu names follow the Java Edition options screens. A field shown in
        // multiple game screens appears once here; legacy setting keys stay stable.
        var basic = new HashSet<string>(new[] { "attack", "use", "pickItem", "forward", "back", "left", "right", "jump", "sneak", "sprint", "drop", "inventory", "chat", "playerlist", "command", "screenshot", "togglePerspective", "smoothCamera", "fullscreen" }.Select(key => "key_key." + key), System.StringComparer.Ordinal);
        foreach (int slot in Enumerable.Range(1, 9)) basic.Add("key_key.hotbar." + slot);
        foreach (string key in new[] { "mouseSensitivity", "invertYMouse", "fov", "bobView" }) basic.Add(key);
        foreach (var item in items.Where(item => item.Setting == "LaunchPersonalOptionsAudio" && item.Key != "soundCategory_voice")) basic.Add(item.Key);
        string[] order = { "选项", "按键控制 · 按键绑定", "按键控制 · 鼠标设置", "视频设置", "音乐和声音" };
        return items.Where(item => basic.Contains(item.Key)).Select(item => item with {
            Title = item.Key == "fov" ? "视场角" : item.Key == "fovEffectScale" ? "视场角效果" : item.Title,
            Category = PersonalOptionsRules.VanillaBinding(item.Key) ? "按键控制 · 按键绑定"
                : item.Key == "autoJump" ? "按键控制"
                : item.Key is "toggleSprint" or "toggleCrouch" ? "辅助功能设置"
                : item.Setting == "LaunchPersonalOptionsMouse" ? "按键控制 · 鼠标设置"
                : item.Key == "fov" ? "选项"
                : item.Setting == "LaunchPersonalOptionsView" ? "视频设置"
                : item.Setting == "LaunchPersonalOptionsAudio" ? "音乐和声音"
                : "辅助功能设置"
        }).OrderBy(item => System.Array.IndexOf(order, item.Category)).ToList().AsReadOnly();
    }
    public static string Title(string key) => Items.FirstOrDefault(item => item.Key == key)?.Title ?? key;
}
