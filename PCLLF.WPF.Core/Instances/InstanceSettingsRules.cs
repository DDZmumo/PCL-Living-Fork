using System;
namespace PCLCS;
public static class InstanceSettingsRules {
    public static void ServerLoginChanged<TInstance>(Func<bool> pageExists, Func<TInstance?> instance, Action<TInstance> setInstance, Func<string,TInstance> load, Action clearCache, Action refreshList, Func<TInstance,string> name) where TInstance : class {
        if (!pageExists()) return;
        clearCache(); if (instance() is null) return;
        setInstance(load(name(instance()!))); refreshList();
    }
}
