namespace PCLCS;

// Shared source identities only. Account IO remains in the existing login service.
public static class LegacyAuthenticationAgentSources {
    public const string NideMetadataPrefix = "https://auth.mc-user.com:233/";
    public const string NideJar = "https://login.mc-user.com:233/index/jar";
    public const string AuthlibMetadata = "https://authlib-injector.yushi.moe/artifact/latest.json";
    public const string AuthlibMetadataMirror = "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json";
}
