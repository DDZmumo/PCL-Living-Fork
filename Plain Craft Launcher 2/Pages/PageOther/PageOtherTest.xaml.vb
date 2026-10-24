Public Class PageOtherTest

    Public Shared Sub Jrrp()
        Hint("本版本暂未提供今日人品功能。")
    End Sub
    Public Shared Function GetRandomCave() As String
        '兼容旧主页的 {cave} 占位符，沿用本项目的推荐内容。
        Return GetRandomPresetHint()
    End Function
    Public Shared Function GetRandomHint() As String
        Return GetRandomPresetHint()
    End Function
    Public Shared Function GetRandomPresetHint() As String
        Return RandomOne(New String() {
            "喜欢 PCL 的使用体验？欢迎前往原作者的官方渠道获取 PCL，支持龙腾猫跃持续开发。",
            "想体验 PCL 官方快照版和更多主题？可以前往龙腾猫跃的爱发电主页，了解赞助与获取方式。",
            "购买正版 Minecraft，支持 Mojang 继续创造新的冒险！请通过 Minecraft 官网购买并使用自己的微软账号登录。",
            "PCL Living Fork 是基于 PCL 原项目的第三方修改版本。欢迎了解并支持原作者开发的官方 PCL。",
            "已经拥有正版 Minecraft？使用购买游戏的微软账号登录，即可使用自己的正版身份开始冒险。"})
    End Function

End Class
